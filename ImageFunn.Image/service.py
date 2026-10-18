"""
Lokale beeldservice voor ImageFunn - dezelfde rol als TabStudio.Audio voor
TabStudio: één achtergrondproces dat ImageFunn.App via HTTP aanroept, in
plaats van dat de WPF-app zelf met Python/CUDA moet omgaan.

Genereert een beeld uit tekst met FLUX.1-schnell (Apache 2.0, 4 stappen) in
4-bit, zodat het op een kaart van 12 GB past. Een taak kan een halve minuut
duren (de eerste keer langer: het model wordt dan gedownload en geladen),
dus de client start een taak en peilt de status - net als bij de
audio-service.

Bestanden blijven op schijf staan in uitvoer/<job_id>/ (beeld.png +
meta.json); de paden komen terug in de JSON, want client en service draaien
op dezelfde machine. Bij het opstarten worden de meta.json's teruggelezen,
zodat de galerij een herstart van de service overleeft.

Standaard SDXL (stabilityai/stable-diffusion-xl-base-1.0). Ander model? Zet IMAGEFUNN_MODEL op "flux"
of zet het op "flux" (FLUX.1-schnell, vraagt een Hugging Face-login).

Starten (handmatig - ImageFunn.App doet dit zelf bij het opstarten):
    .venv\\Scripts\\python.exe -m uvicorn service:app --port 5330
"""

from __future__ import annotations

import json
import os
import random
import shutil
import sys
import threading
import time
import traceback
import uuid
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Literal

from fastapi import FastAPI, HTTPException
from pydantic import BaseModel, Field

import videomodellen

sys.stdout.reconfigure(encoding="utf-8")
sys.stderr.reconfigure(encoding="utf-8")

app = FastAPI(title="ImageFunn Image Service")

UITVOER = Path(__file__).parent / "uitvoer"
UITVOER.mkdir(exist_ok=True)

# "realvis" (standaard): RealVisXL V5.0, een fotorealistische SDXL-variant. "juggernaut": JuggernautXL Ragnarok, ook fotorealistisch.
# "sdxl": het gewone SDXL-basismodel. "flux": FLUX.1-schnell (vraagt een Hugging Face-login).
MODEL_KEUZE = os.environ.get("IMAGEFUNN_MODEL", "realvis").lower()

# Juggernaut is een SDXL-model: dezelfde pijplijn, IP-Adapters en afmetingen, alleen met een ander beeldnetwerk (UNet).
SDXL_FAMILIE = MODEL_KEUZE in ("sdxl", "juggernaut", "realvis")

# Guidance: Juggernaut werkt het best rond 5; het SDXL-basismodel rond 6,5.
GUIDANCE = 5.0 if MODEL_KEUZE in ("juggernaut", "realvis") else 6.5

# Na zoveel seconden zonder opdracht laat de service zijn modellen los en STOPT hij zichzelf. Alleen het videokaartgeheugen
# laat zich in het proces vrijgeven; het werkgeheugen (RAM, 7 tot 13 GB) houden PyTorch en de CUDA-stuurprogramma's vast
# tot het proces eindigt (gemeten: geen enkel Python-object leeft nog, het RAM blijft toch staan). ImageFunn start de
# service bij de volgende opdracht vanzelf weer. 0 = nooit stoppen.
ONTLAAD_NA = int(os.environ.get("IMAGEFUNN_ONTLAAD_NA", "300"))

# Het omgezette Juggernaut-netwerk, zodat de omzetting (±1 minuut) maar één keer nodig is.
JUGGERNAUT_CACHE = Path(__file__).parent / "modellen" / "juggernaut_unet_fp16.safetensors"

# De versie van deze code: het tijdstip waarop service.py voor het laatst gewijzigd is. ImageFunn.App vergelijkt dit
# met het bestand op schijf en start de service opnieuw als er een nieuwere versie ligt dan die draait.
VERSIE = int(Path(__file__).stat().st_mtime)

Status = Literal["wachtend", "bezig", "klaar", "mislukt"]


class CastItem(BaseModel):
    """Een speler of een plek met een eigen referentiebeeld, dat de sleutelbeelden op elkaar laat lijken."""

    naam: str = ""
    soort: Literal["speler", "plek", "voorwerp"] = "speler"
    beeld: str = ""  # Engelse omschrijving om het referentiebeeld van te maken
    referentie: str | None = None  # het gemaakte referentiebeeld (png)
    opnieuw: bool = False


class Scene(BaseModel):
    """Eén scène van een film: een stuk van enkele seconden met een eigen sleutelbeeld en beweging."""

    titel: str = ""
    gebeurt: str = ""
    beeld: str = ""  # Engelse omschrijving van het eerste beeld (voor het sleutelbeeld)
    beweging: str = ""  # Engelse omschrijving van wat er beweegt en hoe de camera staat
    seconden: int = Field(4, ge=2, le=6)
    bestand: str | None = None  # al gemaakte scène (mp4): wordt hergebruikt, niet opnieuw gemaakt
    status: str = "wachtend"  # wachtend | bezig | klaar | mislukt
    seed: int | None = None
    voortgang: float = 0.0  # 0..1, alleen tijdens het animeren van deze scène
    opnieuw: bool = False  # bij soort "sleutelbeelden": dit sleutelbeeld opnieuw maken
    # Referentiebeelden uit de cast (de app zet hier de paden in):
    speler_refs: list[str] = Field(default_factory=list)
    plek_ref: str | None = None
    voorwerp_ref: str | None = None
    sleutel: str | None = None  # het sleutelbeeld (png) waarmee deze scène begint


class Opdracht(BaseModel):
    prompt: str = ""
    negatief: str = ""
    breedte: int = Field(1024, ge=256, le=2048)
    hoogte: int = Field(1024, ge=256, le=2048)
    stappen: int | None = None
    seed: int | None = None
    # "video": het beeld in bron_beeld laten bewegen (beeld naar video); zonder bron_beeld wordt het tekst naar video.
    soort: Literal["beeld", "video", "film", "sleutelbeelden", "cast"] = "beeld"
    bron_beeld: str | None = None
    frames: int = Field(97, ge=9, le=161)
    # Welk videomodel (sleutel uit videomodellen.MODELLEN). Geldt voor soort "video" en "film".
    videomodel: str = videomodellen.STANDAARD
    # Welk beeldmodel (alleen bij de SDXL-familie): "juggernaut" of "realvis"; leeg = de standaard van de service.
    beeldmodel: Literal["", "juggernaut", "realvis"] = ""
    # Alleen bij soort "film":
    scenes: list[Scene] = Field(default_factory=list)
    modus: Literal["doorlopend", "sleutelbeelden"] = "sleutelbeelden"
    overgang: Literal["hard", "zacht"] = "zacht"
    stijl: str = ""
    # Voor dezelfde personages in alle sleutelbeelden (IP-Adapter):
    referentie: str | None = None  # eigen voorbeeldbeeld
    eerste_als_referentie: bool = False  # of anders: het sleutelbeeld van scène 1
    referentie_sterkte: float = Field(0.45, ge=0.0, le=1.0)
    cast: list[CastItem] = Field(default_factory=list)
    speler_sterkte: float = Field(0.5, ge=0.0, le=1.0)
    plek_sterkte: float = Field(0.4, ge=0.0, le=1.0)
    voorwerp_sterkte: float = Field(0.35, ge=0.0, le=1.0)
    # Bijsturen van een bestaand beeld (soort "beeld" met bron_beeld): masker = wit is het gebied dat mag veranderen;
    # zonder masker verandert het hele beeld, zoveel als sterkte toelaat.
    masker: str | None = None
    sterkte: float = Field(0.5, ge=0.05, le=1.0)


class Taak:
    def __init__(self, job_id: str, opdracht: Opdracht):
        self.job_id = job_id
        self.prompt = opdracht.prompt
        self.opdracht = opdracht
        self.gemaakt_op = datetime.now(timezone.utc).isoformat()
        self.status: Status = "wachtend"
        self.stap = "In de wachtrij"
        self.voortgang = 0.0
        self.bestand: str | None = None
        self.seed: int | None = None
        self.foutmelding: str | None = None
        self.stop = False
        # Bij een film: sleutelbeelden | model | scenes | samenvoegen | klaar
        self.fase = ""
        # Voorproefje tijdens het maken van een video: het middelste beeld zoals het er op dit moment uitziet.
        self.voorproef: str | None = None
        self.voorproef_nr = 0

    def als_dict(self) -> dict[str, Any]:
        return {
            "job_id": self.job_id,
            "prompt": self.prompt,
            "gemaakt_op": self.gemaakt_op,
            "status": self.status,
            "stap": self.stap,
            "voortgang": self.voortgang,
            "bestand": self.bestand,
            "breedte": self.opdracht.breedte,
            "hoogte": self.opdracht.hoogte,
            "stappen": self.opdracht.stappen,
            "soort": self.opdracht.soort,
            "bron_beeld": self.opdracht.bron_beeld,
            "frames": self.opdracht.frames,
            "videomodel": self.opdracht.videomodel,
            "beeldmodel": self.opdracht.beeldmodel,
            "seed": self.seed,
            "scenes": [sc.model_dump() for sc in self.opdracht.scenes],
            "modus": self.opdracht.modus,
            "overgang": self.opdracht.overgang,
            "stijl": self.opdracht.stijl,
            "cast": [c.model_dump() for c in self.opdracht.cast],
            "fase": self.fase,
            "voorproef": self.voorproef,
            "voorproef_nr": self.voorproef_nr,
            "foutmelding": self.foutmelding,
        }


TAKEN: dict[str, Taak] = {}
_wachtrij_slot = threading.Lock()  # één beeld tegelijk: de GPU is maar één keer aanwezig
_pipe = None
_pipe_model = ""  # welk beeldmodel er in _pipe staat
_vraag_model = ""  # welk beeldmodel de lopende opdracht wil (de service draait één opdracht tegelijk)
_video_pipe = None  # tekst naar video (de pijplijn van het gekozen videomodel, zie videomodellen.py)
_video_i2v = None  # beeld naar video, deelt de onderdelen van _video_pipe
_video_model = ""  # welk videomodel er in _video_pipe staat (sleutel uit videomodellen.MODELLEN)
REALVIS_ID = "SG161222/RealVisXL_V5.0"
_ip_beschikbaar = False  # is de IP-Adapter (voorbeeldbeeld voor dezelfde personages) bij SDXL geladen?
_pipe_slot = threading.Lock()


def _lmstudio_modelmap() -> Path:
    """De modelmap van LM Studio: 'downloadsFolder' uit ~/.lmstudio/settings.json (die kun je in LM Studio
    verzetten, bijv. naar D:\\Jabasoft\\modellen\\lmstudio), anders de standaardplek ~/.lmstudio/models."""
    standaard = Path.home() / ".lmstudio" / "models"
    try:
        instellingen = json.loads((Path.home() / ".lmstudio" / "settings.json").read_text(encoding="utf-8"))
        map_ = instellingen.get("downloadsFolder")
        if map_ and Path(map_).is_dir():
            return Path(map_)
    except (OSError, ValueError):
        pass
    return standaard


def _zoek_juggernaut_gguf() -> Path | None:
    """Het GGUF-bestand van Juggernaut: via IMAGEFUNN_JUGGERNAUT_GGUF, anders in de modelmap van LM Studio."""
    eigen = os.environ.get("IMAGEFUNN_JUGGERNAUT_GGUF")
    if eigen and Path(eigen).is_file():
        return Path(eigen)
    for map_ in dict.fromkeys((_lmstudio_modelmap(), Path.home() / ".lmstudio" / "models")):
        for pad in map_.glob("**/juggernautXL*.gguf"):
            return pad
    return None


def _laad_juggernaut_unet(meld):
    """
    Geeft het Juggernaut-netwerk (UNet) als fp16, of None als het bestand er niet is.

    Het GGUF-bestand (uit LM Studio's modelmap) bevat alleen dit netwerk, gekwantiseerd. Diffusers kan dat niet
    rechtstreeks lezen: we ontkwantiseren het zelf, zetten de namen om naar het formaat van diffusers en bewaren
    het resultaat als gewoon bestand (±5 GB), zodat de volgende keer laden in seconden gaat.
    """
    import torch
    from diffusers import UNet2DConditionModel

    config = UNet2DConditionModel.load_config("stabilityai/stable-diffusion-xl-base-1.0", subfolder="unet")

    def lege_unet():
        # In fp16 opbouwen: het standaardtype fp32 zou eerst 10 GB RAM vragen.
        oud = torch.get_default_dtype()
        torch.set_default_dtype(torch.float16)
        try:
            return UNet2DConditionModel.from_config(config)
        finally:
            torch.set_default_dtype(oud)

    if JUGGERNAUT_CACHE.is_file():
        from safetensors.torch import load_file

        unet = lege_unet()
        unet.load_state_dict(load_file(str(JUGGERNAUT_CACHE)), strict=True)
        return unet

    gguf_pad = _zoek_juggernaut_gguf()
    if gguf_pad is None:
        return None

    meld("Juggernaut XL voor het eerst omzetten (±1 minuut, eenmalig)…")
    import gguf
    import numpy as np
    from diffusers.loaders.single_file_utils import convert_ldm_unet_checkpoint
    from gguf.quants import dequantize
    from safetensors.torch import save_file

    plat = (gguf.GGMLQuantizationType.F16, gguf.GGMLQuantizationType.F32)
    ldm: dict[str, Any] = {}
    for tensor in gguf.GGUFReader(str(gguf_pad)).tensors:
        waarden = tensor.data if tensor.tensor_type in plat else dequantize(tensor.data, tensor.tensor_type)
        ldm["model.diffusion_model." + tensor.name] = (
            torch.from_numpy(np.array(waarden, dtype=np.float32)).reshape(tuple(reversed(tensor.shape.tolist()))).to(torch.float16)
        )

    omgezet = convert_ldm_unet_checkpoint(ldm, config)
    unet = lege_unet()
    doel = unet.state_dict()
    for naam in list(omgezet):
        # Gekwantiseerde gewichten zijn plat opgeslagen; terug naar hun echte vorm.
        if naam in doel and omgezet[naam].shape != doel[naam].shape and omgezet[naam].numel() == doel[naam].numel():
            omgezet[naam] = omgezet[naam].reshape(doel[naam].shape)
    unet.load_state_dict(omgezet, strict=True)

    JUGGERNAUT_CACHE.parent.mkdir(parents=True, exist_ok=True)
    save_file({naam: waarde.contiguous() for naam, waarde in unet.state_dict().items()}, str(JUGGERNAUT_CACHE))
    return unet


def _laad_pijplijn(meld):
    """Laadt het model één keer; daarna blijft het in het geheugen."""
    global _pipe, _pipe_model, _ip_beschikbaar
    with _pipe_slot:
        gevraagd = (_vraag_model or MODEL_KEUZE) if SDXL_FAMILIE else MODEL_KEUZE
        if _pipe is not None:
            if _pipe_model == gevraagd:
                return _pipe
            meld("Ander beeldmodel: het huidige wordt losgelaten…")
            _geef_beeld_vrij()
        _geef_video_vrij()

        import torch

        if not torch.cuda.is_available():
            raise RuntimeError("Geen CUDA-GPU gevonden - installeer PyTorch met CUDA (zie requirements.txt).")

        if SDXL_FAMILIE:
            from diffusers import StableDiffusionXLPipeline

            sdxl = "stabilityai/stable-diffusion-xl-base-1.0"
            extra: dict[str, Any] = {}
            if gevraagd == "realvis":
                sdxl = REALVIS_ID
                meld("Beeldmodel laden (RealVisXL)…")
            elif gevraagd == "juggernaut":
                meld("Beeldmodel laden (Juggernaut XL)…")
                try:
                    unet = _laad_juggernaut_unet(meld)
                except Exception:  # noqa: BLE001 - lukt het niet, dan werkt het gewone SDXL nog steeds
                    traceback.print_exc()
                    unet = None
                if unet is not None:
                    extra["unet"] = unet
                else:
                    meld("Juggernaut niet beschikbaar - het gewone SDXL wordt gebruikt…")
            else:
                meld("Beeldmodel laden (SDXL)…")
            try:
                # Met IP-Adapter: een voorbeeldbeeld waar nieuwe beelden op lijken (dezelfde robot in elke scène).
                from transformers import CLIPVisionModelWithProjection

                encoder = CLIPVisionModelWithProjection.from_pretrained(
                    "h94/IP-Adapter", subfolder="models/image_encoder", torch_dtype=torch.float16
                )
                pipe = StableDiffusionXLPipeline.from_pretrained(
                    sdxl, torch_dtype=torch.float16, variant="fp16", image_encoder=encoder, **extra
                )
                gewicht = "ip-adapter-plus_sdxl_vit-h.safetensors"
                # Drie keer dezelfde adapter: de eerste volgt een speler, de tweede een plek, de derde een voorwerp (eigen sterkte per adapter).
                pipe.load_ip_adapter("h94/IP-Adapter", subfolder="sdxl_models", weight_name=[gewicht, gewicht, gewicht])
                _ip_beschikbaar = True
            except Exception:  # noqa: BLE001 - zonder adapter werkt SDXL gewoon, alleen zonder voorbeeldbeeld
                traceback.print_exc()
                _ip_beschikbaar = False
                pipe = StableDiffusionXLPipeline.from_pretrained(sdxl, torch_dtype=torch.float16, variant="fp16", **extra)
            if "unet" in extra or gevraagd == "realvis":
                # De sampler die bij Juggernaut en RealVisXL hoort (DPM++ 2M SDE met Karras-sigma's).
                from diffusers import DPMSolverMultistepScheduler

                pipe.scheduler = DPMSolverMultistepScheduler.from_config(
                    pipe.scheduler.config, use_karras_sigmas=True, algorithm_type="sde-dpmsolver++"
                )
            pipe.enable_model_cpu_offload()
        else:
            from diffusers import BitsAndBytesConfig as DiffusersBnb
            from diffusers import FluxPipeline, FluxTransformer2DModel
            from transformers import BitsAndBytesConfig as TransformersBnb
            from transformers import T5EncoderModel

            repo = "black-forest-labs/FLUX.1-schnell"
            meld("Beeldmodel laden (FLUX.1-schnell)…")
            transformer = FluxTransformer2DModel.from_pretrained(
                repo,
                subfolder="transformer",
                quantization_config=DiffusersBnb(
                    load_in_4bit=True, bnb_4bit_quant_type="nf4", bnb_4bit_compute_dtype=torch.bfloat16
                ),
                torch_dtype=torch.bfloat16,
            )
            t5 = T5EncoderModel.from_pretrained(
                repo,
                subfolder="text_encoder_2",
                quantization_config=TransformersBnb(load_in_8bit=True),
                torch_dtype=torch.bfloat16,
            )
            pipe = FluxPipeline.from_pretrained(
                repo, transformer=transformer, text_encoder_2=t5, torch_dtype=torch.bfloat16
            )
            pipe.enable_model_cpu_offload()

        pipe.set_progress_bar_config(disable=True)
        _pipe = pipe
        _pipe_model = gevraagd
        return pipe


def _draai_beeld(taak: Taak) -> None:
    with _wachtrij_slot:
        taak.status = "bezig"
        try:
            import torch

            def meld(tekst: str) -> None:
                taak.stap = tekst

            pipe = _laad_pijplijn(meld)
            o = taak.opdracht
            flux = not SDXL_FAMILIE
            stappen = o.stappen or (4 if flux else 30)
            # FLUX rekent in blokken van 16 pixels, SDXL van 8.
            blok = 16 if flux else 8
            breedte, hoogte = o.breedte // blok * blok, o.hoogte // blok * blok
            seed = o.seed if o.seed is not None else random.randint(0, 2**31 - 1)
            taak.seed = seed

            def stap_klaar(_pipe, i, _t, kwargs):
                taak.voortgang = (i + 1) / stappen
                taak.stap = f"Genereren: stap {i + 1} van {stappen}"
                return kwargs

            args: dict[str, Any] = dict(
                prompt=o.prompt,
                width=breedte,
                height=hoogte,
                num_inference_steps=stappen,
                generator=torch.Generator("cpu").manual_seed(seed),
                callback_on_step_end=stap_klaar,
            )
            if flux:
                args.update(guidance_scale=0.0, max_sequence_length=256)
            else:
                args.update(guidance_scale=GUIDANCE, negative_prompt=o.negatief or None)
                args.update(_ip_args(pipe))

            taak.stap = "Genereren…"
            beeld = pipe(**args).images[0]

            map_ = UITVOER / taak.job_id
            map_.mkdir(parents=True, exist_ok=True)
            pad = map_ / "beeld.png"
            beeld.save(pad)
            taak.bestand = str(pad)
            taak.voortgang = 1.0
            taak.stap = "Klaar"
            taak.status = "klaar"
            (map_ / "meta.json").write_text(json.dumps(taak.als_dict(), ensure_ascii=False), encoding="utf-8")
        except Exception as fout:  # noqa: BLE001 - alles hoort als melding bij de client te komen
            traceback.print_exc()
            taak.status = "mislukt"
            taak.foutmelding = str(fout)
            taak.stap = "Mislukt"


def _geef_video_vrij() -> None:
    """Eén model tegelijk in het geheugen: de GPU en het RAM zijn niet groot genoeg voor beide."""
    global _video_pipe, _video_i2v, _video_model
    if _video_pipe is not None:
        import gc

        import torch

        _video_pipe = None
        _video_i2v = None
        _video_model = ""
        gc.collect()
        torch.cuda.empty_cache()


_bewerk_pijplijnen: dict[str, Any] = {}  # beeld naar beeld en inpainting, delen de onderdelen van _pipe


def _geef_beeld_vrij() -> None:
    global _pipe, _pipe_model
    _pipe_model = ""
    _bewerk_pijplijnen.clear()
    if _pipe is not None:
        import gc

        import torch

        _pipe = None
        gc.collect()
        torch.cuda.empty_cache()


def _laad_videopijplijn(meld, vanaf_beeld: bool, model: str = videomodellen.STANDAARD):
    """
    Het gekozen videomodel (zie videomodellen.py). Eén keer laden, daarna blijft het in het geheugen; tekst naar video en
    beeld naar video delen dezelfde onderdelen (from_pipe), dus het model staat maar één keer in het geheugen.
    Wordt een ander videomodel gevraagd, dan wordt het huidige eerst losgelaten: twee samen passen niet.
    """
    global _video_pipe, _video_i2v, _video_model
    with _pipe_slot:
        if _video_pipe is not None and _video_model != model:
            _geef_video_vrij()
        if _video_pipe is None:
            import torch

            vm = videomodellen.kies(model)
            if not torch.cuda.is_available():
                raise RuntimeError("Geen CUDA-GPU gevonden - installeer PyTorch met CUDA (zie requirements.txt).")

            _geef_beeld_vrij()
            _video_pipe = vm.laad(meld)
            _video_model = model
        return _laad_i2v() if vanaf_beeld else _video_pipe


def _laad_i2v():
    global _video_i2v
    if _video_i2v is None:
        _video_i2v = videomodellen.kies(_video_model).i2v(_video_pipe)
        _video_i2v.set_progress_bar_config(disable=True)
    return _video_i2v


def _draai_bewerk(taak: Taak) -> None:
    """
    Een bestaand beeld bijsturen. Zonder masker is dat beeld naar beeld: het beeld wordt deels opnieuw opgebouwd, zoveel
    als "sterkte" toelaat (laag = de indeling blijft, hoog = meer vrijheid). Met een masker is het inpainting: alleen het
    witte gebied wordt opnieuw gemaakt, de rest blijft zoals het was.
    """
    with _wachtrij_slot:
        taak.status = "bezig"
        try:
            import torch
            from diffusers import StableDiffusionXLImg2ImgPipeline, StableDiffusionXLInpaintPipeline
            from PIL import Image

            o = taak.opdracht
            if not SDXL_FAMILIE:
                raise RuntimeError("Bijsturen werkt alleen met Juggernaut of SDXL.")
            if not _bestaat_pad(o.bron_beeld):
                raise RuntimeError("Het beeld om bij te sturen is niet gevonden.")

            basis = _laad_pijplijn(lambda t: setattr(taak, "stap", t))

            # Het formaat blijft zoals het was (veelvoud van 8), met een bovengrens van ongeveer 1,3 megapixel.
            bron = Image.open(o.bron_beeld).convert("RGB")
            k = min(1.0, (1_300_000 / (bron.width * bron.height)) ** 0.5)
            breedte, hoogte = max(64, round(bron.width * k / 8) * 8), max(64, round(bron.height * k / 8) * 8)
            bron = bron.resize((breedte, hoogte), Image.LANCZOS)

            masker = None
            if _bestaat_pad(o.masker):
                masker = Image.open(o.masker).convert("L").resize((breedte, hoogte), Image.LANCZOS)
                masker = masker.point(lambda v: 255 if v > 127 else 0)

            soort = "inpaint" if masker is not None else "i2i"
            if soort not in _bewerk_pijplijnen:
                klasse = StableDiffusionXLInpaintPipeline if masker is not None else StableDiffusionXLImg2ImgPipeline
                _bewerk_pijplijnen[soort] = klasse.from_pipe(basis, dtype=torch.float16)
                _bewerk_pijplijnen[soort].set_progress_bar_config(disable=True)
            pijplijn = _bewerk_pijplijnen[soort]

            stappen = o.stappen or 30
            totaal = max(1, int(stappen * o.sterkte))
            seed = o.seed if o.seed is not None else random.randint(0, 2**31 - 1)
            taak.seed = seed

            def stap_klaar(_p, i, _t, kwargs):
                taak.voortgang = min(1.0, (i + 1) / totaal)
                taak.stap = f"Bijsturen: stap {i + 1} van {totaal}"
                return kwargs

            args: dict[str, Any] = dict(
                prompt=o.prompt,
                negative_prompt=o.negatief or None,
                image=bron,
                strength=o.sterkte,
                num_inference_steps=stappen,
                guidance_scale=GUIDANCE,
                generator=torch.Generator("cpu").manual_seed(seed),
                callback_on_step_end=stap_klaar,
            )
            args.update(_ip_args(pijplijn))
            if masker is not None:
                # padding_mask_crop: alleen rond het gebied wordt op volle resolutie gewerkt, wat de details beter maakt.
                args.update(mask_image=masker, padding_mask_crop=32, width=breedte, height=hoogte)

            taak.stap = "Bijsturen…"
            beeld = pijplijn(**args).images[0]

            map_ = UITVOER / taak.job_id
            map_.mkdir(parents=True, exist_ok=True)
            pad = map_ / "beeld.png"
            beeld.save(pad)
            taak.bestand = str(pad)
            taak.voortgang = 1.0
            taak.stap = "Klaar"
            taak.status = "klaar"
            (map_ / "meta.json").write_text(json.dumps(taak.als_dict(), ensure_ascii=False), encoding="utf-8")
        except Exception as fout:  # noqa: BLE001 - alles hoort als melding bij de client te komen
            traceback.print_exc()
            taak.status = "mislukt"
            taak.foutmelding = str(fout)
            taak.stap = "Mislukt"


def _maak_voorproef(pipe, latents, taak: Taak, map_: Path) -> None:
    """
    Decodeert het middelste beeld uit de tussenstand van het maken en zet het als voorproefje klaar voor de app
    (alleen voor videomodellen met `voorproef=True`, nu Wan).
    Draait op de CPU in een eigen draadje, zodat de GPU doorwerkt; het videonetwerk zelf wordt hier niet gebruikt.
    Mislukt het, dan blijft het voorproefje gewoon weg en gaat het maken gewoon door.
    """
    import torch
    from PIL import Image

    try:
        with torch.no_grad():
            vae = getattr(taak, "_vae_kopie", None)
            if vae is None:
                # Een eigen kopie van de VAE op de CPU (float32): de echte staat soms op de GPU en wordt door de offload-haken
                # heen en weer gezet, en de GPU zit vol met het videonetwerk.
                orig = pipe.vae
                vae = type(orig).from_config(orig.config)
                vae.load_state_dict({k: v.detach().cpu().float() for k, v in orig.state_dict().items()})
                vae = vae.to(torch.float32).eval()
                if hasattr(vae, "enable_tiling"):
                    vae.enable_tiling()
                taak._vae_kopie = vae
            z = vae.config.z_dim
            gemiddeld = torch.tensor(vae.config.latents_mean).view(1, z, 1, 1, 1)
            spreiding = 1.0 / torch.tensor(vae.config.latents_std).view(1, z, 1, 1, 1)
            lat = latents / spreiding + gemiddeld
            t = lat.shape[2] // 2
            # _decode en niet decode: decode vuurt de offload-haak af, die dan het videonetwerk van de GPU haalt midden in het maken.
            uit = vae._decode(lat[:, :, t : t + 1].to(vae.dtype)).sample
            beeld = uit[0, :, 0] if uit.dim() == 5 else uit[0]
            beeld = ((beeld.float().clamp(-1, 1) + 1) / 2 * 255).byte().permute(1, 2, 0).numpy()
        nr = taak.voorproef_nr + 1
        pad = map_ / f"voorproef_{nr}.jpg"
        Image.fromarray(beeld).save(pad, quality=85)
        taak.voorproef = str(pad)
        taak.voorproef_nr = nr
        if nr > 2:
            (map_ / f"voorproef_{nr - 2}.jpg").unlink(missing_ok=True)
    except Exception:  # noqa: BLE001 - een voorproefje is een extraatje
        traceback.print_exc()


def _draai_video(taak: Taak) -> None:
    with _wachtrij_slot:
        taak.status = "bezig"
        try:
            import torch
            from diffusers.utils import export_to_video
            from PIL import Image

            o = taak.opdracht
            vanaf_beeld = bool(o.bron_beeld)
            if vanaf_beeld and not Path(o.bron_beeld).is_file():
                raise RuntimeError("Het bronbeeld is niet gevonden.")

            vm = videomodellen.kies(o.videomodel)
            if not vanaf_beeld and not vm.t2v:
                raise RuntimeError(f"{vm.naam} kan alleen een beeld laten bewegen: kies een bronbeeld of een ander videomodel.")
            pipe = _laad_videopijplijn(lambda tekst: setattr(taak, "stap", tekst), vanaf_beeld, o.videomodel)

            frames = vm.frames(o.frames)
            if vanaf_beeld:
                # Het formaat volgt de verhouding van het bronbeeld.
                bron = Image.open(o.bron_beeld).convert("RGB")
                schaal = (o.breedte * o.hoogte / (bron.width * bron.height)) ** 0.5
                breedte = max(256, round(bron.width * schaal / vm.maat) * vm.maat)
                hoogte = max(256, round(bron.height * schaal / vm.maat) * vm.maat)
                bron = bron.resize((breedte, hoogte), Image.LANCZOS)
            else:
                bron = None
                breedte = max(256, o.breedte // vm.maat * vm.maat)
                hoogte = max(256, o.hoogte // vm.maat * vm.maat)
            stappen = o.stappen or vm.stappen
            seed = o.seed if o.seed is not None else random.randint(0, 2**31 - 1)
            taak.seed = seed

            map_ = UITVOER / taak.job_id
            map_.mkdir(parents=True, exist_ok=True)
            voorproef_draad: list[threading.Thread] = []

            def stap_klaar(_pipe, i, _t, kwargs):
                taak.voortgang = (i + 1) / stappen
                taak.stap = f"{'Animeren' if vanaf_beeld else 'Video maken'}: stap {i + 1} van {stappen}"
                bezig = voorproef_draad and voorproef_draad[0].is_alive()
                if i + 1 >= stappen:
                    # Het eindbeeld wordt zo gedecodeerd, met dezelfde VAE: eerst het voorproefje afmaken.
                    if bezig:
                        voorproef_draad[0].join(timeout=180)
                # Pas vanaf stap 8 (daarvoor is het alleen ruis); daarna zodra de vorige klaar is.
                elif vm.voorproef and i + 1 >= 8 and not bezig and "latents" in kwargs:
                    lat = kwargs["latents"].detach().float().cpu()
                    voorproef_draad[:] = [
                        threading.Thread(
                            target=_maak_voorproef,
                            args=(pipe, lat, taak, map_),
                            daemon=True,
                        )
                    ]
                    voorproef_draad[0].start()
                return kwargs

            taak.stap = "Animeren…" if vanaf_beeld else "Video maken…"
            beelden = vm.roep_aan(
                pipe, prompt=o.prompt, negatief=o.negatief, breedte=breedte, hoogte=hoogte, frames=frames,
                stappen=stappen, seed=seed, bron=bron, stap_klaar=stap_klaar,
            )

            taak.stap = "Video opslaan…"
            taak.voorproef = None
            taak._vae_kopie = None
            for oud in map_.glob("voorproef_*.jpg"):
                oud.unlink(missing_ok=True)
            pad = map_ / "video.mp4"
            export_to_video(beelden, str(pad), fps=vm.fps)
            taak.bestand = str(pad)
            taak.voortgang = 1.0
            taak.stap = "Klaar"
            taak.status = "klaar"
            (map_ / "meta.json").write_text(json.dumps(taak.als_dict(), ensure_ascii=False), encoding="utf-8")
        except Exception as fout:  # noqa: BLE001 - alles hoort als melding bij de client te komen
            traceback.print_exc()
            taak.status = "mislukt"
            taak.foutmelding = str(fout)
            taak.stap = "Mislukt"


def _naar_24fps(beelden: list, fps: int) -> list:
    """Videomodellen met een ander tempo (bijv. 16 fps) krijgen dubbele beelden, zodat scènes in de film op 24 fps passen."""
    if fps == 24:
        return beelden
    n = round(len(beelden) * 24 / fps)
    return [beelden[min(len(beelden) - 1, int(i * fps / 24))] for i in range(n)]


class _Gestopt(Exception):
    pass


def _ip_args(
    pipe,
    speler=None,
    plek=None,
    speler_sterkte: float = 0.5,
    plek_sterkte: float = 0.4,
    voorwerp=None,
    voorwerp_sterkte: float = 0.35,
) -> dict:
    """
    De extra invoer voor de twee IP-Adapters: adapter 1 het beeld van de speler, adapter 2 het beeld van de plek.
    Ontbreekt er een, dan krijgt die een grijs beeld met sterkte 0.
    """
    if SDXL_FAMILIE and _ip_beschikbaar:
        from PIL import Image

        leeg = Image.new("RGB", (224, 224), (127, 127, 127))
        pipe.set_ip_adapter_scale(
            [
                speler_sterkte if speler is not None else 0.0,
                plek_sterkte if plek is not None else 0.0,
                voorwerp_sterkte if voorwerp is not None else 0.0,
            ]
        )
        return {
            "ip_adapter_image": [
                speler if speler is not None else leeg,
                plek if plek is not None else leeg,
                voorwerp if voorwerp is not None else leeg,
            ]
        }
    return {}


def _bestaat_pad(pad) -> bool:
    return bool(pad) and Path(pad).is_file()


def _render_sleutelbeeld(
    prompt: str,
    breedte: int,
    hoogte: int,
    seed: int,
    meld,
    bij_stap=None,
    speler=None,
    plek=None,
    speler_sterkte: float = 0.5,
    plek_sterkte: float = 0.4,
    negatief: str | None = None,
    voorwerp=None,
    voorwerp_sterkte: float = 0.35,
):
    """Eén sleutelbeeld voor een scène, op ongeveer 1 megapixel met dezelfde verhouding als de film."""
    import torch

    pipe = _laad_pijplijn(meld)
    flux = not SDXL_FAMILIE
    blok = 16 if flux else 8
    k = (1024 * 1024 / (breedte * hoogte)) ** 0.5
    b, h = max(blok, round(breedte * k / blok) * blok), max(blok, round(hoogte * k / blok) * blok)
    args: dict[str, Any] = dict(
        prompt=prompt,
        width=b,
        height=h,
        num_inference_steps=4 if flux else 30,
        generator=torch.Generator("cpu").manual_seed(seed),
    )
    if bij_stap is not None:
        def na_stap(_pipe, i, _t, kwargs):
            bij_stap(i + 1, args["num_inference_steps"])
            return kwargs

        args["callback_on_step_end"] = na_stap
    if flux:
        args.update(guidance_scale=0.0, max_sequence_length=256)
    else:
        args.update(guidance_scale=GUIDANCE, negative_prompt=negatief or "blurry, low quality, deformed, text, watermark")
        args.update(_ip_args(pipe, speler, plek, speler_sterkte, plek_sterkte, voorwerp, voorwerp_sterkte))
    return pipe(**args).images[0]


def _maak_sleutelbeelden(taak: "Taak", volgorde: list[int], scenemap: Path, basis_seed: int, na_beeld=None) -> dict:
    """
    Maakt de sleutelbeelden van de scènes in <volgorde> en zet het pad in scene.sleutel.
    Met een voorbeeldbeeld (eigen, of het beeld van scène 1) lijken de personages overal op elkaar.
    na_beeld(k, stap, totaal) wordt tijdens het maken aangeroepen; stap is None zodra beeld k af is.
    """
    from PIL import Image

    o = taak.opdracht
    scenes = o.scenes
    stijl = (o.stijl.strip() + ", ") if o.stijl.strip() else ""
    eigen = Image.open(o.referentie).convert("RGB") if _bestaat_pad(o.referentie) else None
    resultaat: dict[int, Any] = {}

    for k, i in enumerate(volgorde):
        if taak.stop:
            raise _Gestopt()
        taak.stap = f"Sleutelbeeld {k + 1} van {len(volgorde)}: voorbereiden"

        sc = scenes[i]
        speler = Image.open([p for p in sc.speler_refs if _bestaat_pad(p)][0]).convert("RGB") if any(_bestaat_pad(p) for p in sc.speler_refs) else None
        plek = Image.open(sc.plek_ref).convert("RGB") if _bestaat_pad(sc.plek_ref) else None
        voorwerp = Image.open(sc.voorwerp_ref).convert("RGB") if _bestaat_pad(sc.voorwerp_ref) else None
        speler_sterkte = o.speler_sterkte
        if speler is None:
            # Geen speler uit de cast: valt terug op een eigen voorbeeld of het beeld van scène 1.
            speler = eigen
            if speler is None and o.eerste_als_referentie and i != 0 and _bestaat_pad(scenes[0].sleutel):
                speler = Image.open(scenes[0].sleutel).convert("RGB")
            speler_sterkte = o.referentie_sterkte

        def stap(st, totaal, k=k):
            taak.stap = f"Sleutelbeeld {k + 1} van {len(volgorde)}: stap {st} van {totaal}"
            if na_beeld:
                na_beeld(k, st, totaal)

        beeld = _render_sleutelbeeld(
            # De scène eerst: het beeldmodel leest maar ±77 woorddelen en kapt de rest af.
            scenes[i].beeld + (", " + stijl.rstrip(", ") if stijl else ""),
            o.breedte,
            o.hoogte,
            basis_seed + i,
            lambda t: setattr(taak, "stap", t),
            stap,
            speler,
            plek,
            speler_sterkte,
            o.plek_sterkte,
            o.negatief or None,
            voorwerp,
            o.voorwerp_sterkte,
        )
        pad = scenemap / f"sleutel_{i}_{int(time.time() * 1000)}.png"
        beeld.save(pad)
        scenes[i].sleutel = str(pad)
        scenes[i].opnieuw = False
        resultaat[i] = beeld
        if na_beeld:
            na_beeld(k, None, None)

    return resultaat


def _draai_cast(taak: "Taak") -> None:
    """Referentiebeelden maken van de spelers (op een neutrale achtergrond) en de plekken (zonder personages)."""
    with _wachtrij_slot:
        taak.status = "bezig"
        taak.fase = "cast"
        o = taak.opdracht
        cast = o.cast
        try:
            map_ = UITVOER / taak.job_id / "cast"
            map_.mkdir(parents=True, exist_ok=True)
            basis_seed = o.seed if o.seed is not None else random.randint(0, 2**31 - 1)
            taak.seed = basis_seed
            stijl = (o.stijl.strip() + ", ") if o.stijl.strip() else ""
            nodig = [i for i, c in enumerate(cast) if c.opnieuw or not _bestaat_pad(c.referentie)]

            for k, i in enumerate(nodig):
                if taak.stop:
                    raise _Gestopt()
                c = cast[i]
                if c.soort == "speler":
                    prompt = stijl + c.beeld + ", full body, standing, centered, plain light gray studio background"
                    breedte, hoogte = 768, 1024
                elif c.soort == "voorwerp":
                    prompt = stijl + c.beeld + ", a single object, centered, plain light gray studio background, no people"
                    breedte, hoogte = 1024, 1024
                else:
                    prompt = stijl + c.beeld + ", empty landscape, no people, no characters, no animals"
                    breedte, hoogte = o.breedte, o.hoogte
                taak.stap = f"Referentiebeeld {k + 1} van {len(nodig)}: {c.naam}"

                def stap(st, totaal, k=k):
                    taak.voortgang = (k + st / totaal) / max(1, len(nodig))

                beeld = _render_sleutelbeeld(
                    prompt, breedte, hoogte, basis_seed + i, lambda t: setattr(taak, "stap", t), stap,
                    negatief=o.negatief or None,
                )
                pad = map_ / f"cast_{i}_{int(time.time() * 1000)}.png"
                beeld.save(pad)
                c.referentie = str(pad)
                c.opnieuw = False
                taak.voortgang = (k + 1) / max(1, len(nodig))

            taak.voortgang = 1.0
            taak.fase = "cast_klaar"
            taak.stap = "Klaar"
            taak.status = "klaar"
        except _Gestopt:
            taak.status = "mislukt"
            taak.foutmelding = "Gestopt."
            taak.stap = "Gestopt"
        except Exception as fout:  # noqa: BLE001 - alles hoort als melding bij de client te komen
            traceback.print_exc()
            taak.status = "mislukt"
            taak.foutmelding = str(fout)
            taak.stap = "Mislukt"


def _draai_sleutelbeelden(taak: "Taak") -> None:
    """Alleen de sleutelbeelden maken, zodat de gebruiker ze kan beoordelen voordat er geanimeerd wordt."""
    with _wachtrij_slot:
        taak.status = "bezig"
        taak.fase = "sleutelbeelden"
        o = taak.opdracht
        scenes = o.scenes
        try:
            scenemap = UITVOER / taak.job_id / "scenes"
            scenemap.mkdir(parents=True, exist_ok=True)
            basis_seed = o.seed if o.seed is not None else random.randint(0, 2**31 - 1)
            taak.seed = basis_seed

            volgorde = [i for i, sc in enumerate(scenes) if sc.opnieuw or not _bestaat_pad(sc.sleutel)]
            if o.modus == "doorlopend":
                volgorde = [i for i in volgorde if i == 0]  # de rest begint op het laatste beeld van de vorige scène

            def na_beeld(k, st, totaal):
                taak.voortgang = (k + (1 if st is None else st / totaal)) / max(1, len(volgorde))

            _maak_sleutelbeelden(taak, volgorde, scenemap, basis_seed, na_beeld)
            taak.voortgang = 1.0
            taak.fase = "sleutelbeelden_klaar"
            taak.stap = "Klaar"
            taak.status = "klaar"
        except _Gestopt:
            taak.status = "mislukt"
            taak.foutmelding = "Gestopt."
            taak.stap = "Gestopt"
        except Exception as fout:  # noqa: BLE001 - alles hoort als melding bij de client te komen
            traceback.print_exc()
            taak.status = "mislukt"
            taak.foutmelding = str(fout)
            taak.stap = "Mislukt"


def _draai_film(taak: Taak) -> None:
    with _wachtrij_slot:
        taak.status = "bezig"
        o = taak.opdracht
        scenes = o.scenes
        n = len(scenes)
        try:
            import imageio.v2 as iio2
            import imageio.v3 as iio
            import numpy as np
            import torch
            from diffusers.utils import export_to_video
            from PIL import Image

            map_ = UITVOER / taak.job_id
            scenemap = map_ / "scenes"
            scenemap.mkdir(parents=True, exist_ok=True)
            basis_seed = o.seed if o.seed is not None else random.randint(0, 2**31 - 1)
            taak.seed = basis_seed

            def controleer_stop() -> None:
                if taak.stop:
                    raise _Gestopt()

            def bestaat(sc: Scene) -> bool:
                return bool(sc.bestand) and Path(sc.bestand).is_file()

            te_doen = [i for i, sc in enumerate(scenes) if not bestaat(sc)]
            for i, sc in enumerate(scenes):
                sc.status = "wachtend" if i in te_doen else "klaar"
                if i in te_doen:
                    sc.bestand = None
                    sc.voortgang = 0.0
                    # Een goedgekeurd sleutelbeeld blijft staan; bij "doorlopend" geldt dat alleen voor de eerste scène.
                    if not (_bestaat_pad(sc.sleutel) and (o.modus == "sleutelbeelden" or i == 0)):
                        sc.sleutel = None

            # Sleutelbeelden: bij "sleutelbeelden" voor elke nieuwe scène; bij "doorlopend" alleen voor de eerste,
            # de rest begint op het laatste beeld van de vorige scène.
            gebruik = te_doen if o.modus == "sleutelbeelden" else [i for i in te_doen if i == 0]
            sleutel: dict[int, Image.Image] = {}
            for i in gebruik:
                if _bestaat_pad(scenes[i].sleutel):
                    sleutel[i] = Image.open(scenes[i].sleutel).convert("RGB")  # een eerder (goedgekeurd) beeld
            nodig = [i for i in gebruik if i not in sleutel]
            eenheden_totaal = len(nodig) + 6 * len(te_doen)  # een scène kost ongeveer 6 keer een sleutelbeeld
            klaar_eenheden = 0.0

            def zet_voortgang(extra: float = 0.0) -> None:
                taak.voortgang = min(0.97, 0.97 * (klaar_eenheden + extra) / max(1, eenheden_totaal))

            def na_beeld(_k, st, totaal) -> None:
                nonlocal klaar_eenheden
                if st is None:
                    klaar_eenheden += 1
                    zet_voortgang()
                else:
                    zet_voortgang(st / totaal)

            taak.fase = "sleutelbeelden" if nodig else "model"
            sleutel.update(_maak_sleutelbeelden(taak, nodig, scenemap, basis_seed, na_beeld))

            if te_doen:
                taak.fase = "model"
                taak.stap = "Videomodel laden… (de eerste keer duurt dit langer)"
                pipe = _laad_videopijplijn(lambda t: setattr(taak, "stap", t), True, o.videomodel)
                taak.fase = "scenes"

            vm = videomodellen.kies(o.videomodel)
            vorige_laatste: Image.Image | None = None
            for i, sc in enumerate(scenes):
                controleer_stop()
                if i not in te_doen:
                    # Hergebruikte scène: voor "doorlopend" moet het laatste beeld wel bekend zijn.
                    if o.modus == "doorlopend" and (i + 1) in te_doen:
                        vorige_laatste = Image.fromarray(iio.imread(sc.bestand)[-1])
                    continue

                sc.status = "bezig"
                sc.voortgang = 0.0
                if o.modus == "sleutelbeelden" or i == 0:
                    start = sleutel[i]
                else:
                    if vorige_laatste is None:
                        vorige_laatste = Image.fromarray(iio.imread(scenes[i - 1].bestand)[-1])
                    start = vorige_laatste
                    # Bij "doorlopend" is het startbeeld het laatste beeld van de vorige scène: toon dat.
                    start_pad = scenemap / f"start_{i}.png"
                    start.save(start_pad)
                    sc.sleutel = str(start_pad)

                breedte = max(256, o.breedte // vm.maat * vm.maat)
                hoogte = max(256, o.hoogte // vm.maat * vm.maat)
                frames = vm.frames(sc.seconden * vm.fps + 1)
                start = start.convert("RGB").resize((breedte, hoogte), Image.LANCZOS)
                stappen = vm.stappen
                seed = basis_seed + i
                sc.seed = seed
                teller = f"Scène {i + 1} van {n}"
                taak.stap = f"{teller}: tekst en beeld voorbereiden"

                def stap_klaar(pipe_, st, _t, kwargs, teller=teller, sc_=sc):
                    if taak.stop:
                        pipe_._interrupt = True
                    taak.stap = f"{teller}: animeren, stap {st + 1} van {stappen}"
                    sc_.voortgang = (st + 1) / stappen
                    zet_voortgang(6 * (st + 1) / stappen)
                    return kwargs

                beelden = vm.roep_aan(
                    pipe, prompt=(sc.beweging or sc.beeld or "natural motion"),
                    negatief="worst quality, inconsistent motion, blurry, jittery, distorted",
                    breedte=breedte, hoogte=hoogte, frames=frames, stappen=stappen, seed=seed,
                    bron=start, stap_klaar=stap_klaar,
                )
                controleer_stop()

                pad = scenemap / f"scene_{i}.mp4"
                export_to_video(_naar_24fps(beelden, vm.fps), str(pad), fps=24)
                sc.bestand = str(pad)
                sc.status = "klaar"
                sc.voortgang = 1.0
                vorige_laatste = beelden[-1]
                klaar_eenheden += 6
                zet_voortgang()

            # Samenvoegen, met een zachte overgang waar dat gevraagd is.
            taak.fase = "samenvoegen"
            taak.stap = "Film samenvoegen…"
            overlap = 12 if o.overgang == "zacht" else 0
            uit = map_ / "film.mp4"
            schrijver = iio2.get_writer(str(uit), fps=24, codec="libx264", quality=8, macro_block_size=None)
            try:
                tail = None
                for idx, sc in enumerate(scenes):
                    taak.stap = f"Film samenvoegen: scène {idx + 1} van {n}"
                    taak.voortgang = 0.97 + 0.03 * idx / max(1, n)
                    arr = iio.imread(sc.bestand)
                    if idx > 0 and overlap and tail is not None:
                        for k in range(overlap):
                            a = (k + 1) / (overlap + 1)
                            schrijver.append_data(
                                ((1 - a) * tail[k].astype(np.float32) + a * arr[k].astype(np.float32)).astype(np.uint8)
                            )
                        begin = overlap
                    else:
                        begin = 0
                    if overlap and idx < n - 1:
                        einde = len(arr) - overlap
                        tail = arr[einde:]
                    else:
                        einde = len(arr)
                        tail = None
                    for frame in arr[begin:einde]:
                        schrijver.append_data(frame)
            finally:
                schrijver.close()

            taak.bestand = str(uit)
            taak.voortgang = 1.0
            taak.fase = "klaar"
            taak.stap = "Klaar"
            taak.status = "klaar"
            (map_ / "meta.json").write_text(json.dumps(taak.als_dict(), ensure_ascii=False), encoding="utf-8")
        except _Gestopt:
            taak.status = "mislukt"
            taak.foutmelding = "Gestopt. De al gemaakte scènes blijven bewaard."
            taak.stap = "Gestopt"
            for sc in scenes:
                if sc.status == "bezig":
                    sc.status = "wachtend"
        except Exception as fout:  # noqa: BLE001 - alles hoort als melding bij de client te komen
            traceback.print_exc()
            taak.status = "mislukt"
            taak.foutmelding = str(fout)
            taak.stap = "Mislukt"
            for sc in scenes:
                if sc.status == "bezig":
                    sc.status = "mislukt"


_laatst_gebruikt = time.monotonic()


def _draai(taak: Taak) -> None:
    global _laatst_gebruikt, _vraag_model
    _vraag_model = taak.opdracht.beeldmodel
    try:
        if taak.opdracht.soort == "cast":
            _draai_cast(taak)
        elif taak.opdracht.soort == "sleutelbeelden":
            _draai_sleutelbeelden(taak)
        elif taak.opdracht.soort == "film":
            _draai_film(taak)
        elif taak.opdracht.soort == "video":
            _draai_video(taak)
        elif taak.opdracht.soort == "beeld" and taak.opdracht.bron_beeld:
            _draai_bewerk(taak)
        else:
            _draai_beeld(taak)
    finally:
        # De klok voor het ontladen start pas als een opdracht klaar is, niet als hij begint.
        _laatst_gebruikt = time.monotonic()


def _ontlaad_alles() -> bool:
    """Laat het beeldmodel en het videomodel los en geeft GPU en RAM terug. True als er iets geladen was."""
    if _pipe is None and _video_pipe is None:
        return False

    import gc

    _geef_video_vrij()
    _geef_beeld_vrij()
    gc.collect()
    try:
        import torch

        torch.cuda.empty_cache()
    except Exception:  # noqa: BLE001 - zonder torch valt er ook niets vrij te geven
        pass
    return True


def _ontlaad_wacht() -> None:
    """Achtergrondtaak: ontlaadt de modellen als ze lang genoeg ongebruikt zijn en er geen opdracht loopt."""
    while True:
        time.sleep(15)
        if ONTLAAD_NA <= 0 or time.monotonic() - _laatst_gebruikt < ONTLAAD_NA:
            continue

        # Het slot is van de opdracht die loopt: lukt het nemen, dan loopt er niets en komt er ook geen nieuwe tussendoor.
        if not _wachtrij_slot.acquire(blocking=False):
            continue
        try:
            if _ontlaad_alles():
                print(f"Modellen ontladen na {ONTLAAD_NA} seconden stilstand; de service stopt om ook het werkgeheugen vrij te geven.", flush=True)
                os._exit(0)
        finally:
            _wachtrij_slot.release()


threading.Thread(target=_ontlaad_wacht, daemon=True, name="ontlader").start()


def _lees_bestaande() -> None:
    for meta in UITVOER.glob("*/meta.json"):
        try:
            d = json.loads(meta.read_text(encoding="utf-8"))
            taak = Taak(
                d["job_id"],
                Opdracht(
                    prompt=d["prompt"],
                    breedte=d["breedte"],
                    hoogte=d["hoogte"],
                    soort=d.get("soort", "beeld"),
                    bron_beeld=d.get("bron_beeld"),
                    frames=d.get("frames", 97),
                    scenes=[Scene(**sc) for sc in d.get("scenes", [])],
                    modus=d.get("modus", "sleutelbeelden"),
                    overgang=d.get("overgang", "zacht"),
                    stijl=d.get("stijl", ""),
                ),
            )
            taak.gemaakt_op, taak.status, taak.stap = d["gemaakt_op"], "klaar", "Klaar"
            taak.bestand, taak.seed, taak.voortgang = d["bestand"], d.get("seed"), 1.0
            taak.opdracht.stappen = d.get("stappen")
            TAKEN[taak.job_id] = taak
        except Exception:  # noqa: BLE001 - een kapotte meta.json mag de service niet blokkeren
            continue


_lees_bestaande()


@app.get("/health")
def health() -> dict[str, Any]:
    try:
        import torch

        cuda = torch.cuda.is_available()
        gpu = torch.cuda.get_device_name(0) if cuda else None
    except Exception:  # noqa: BLE001
        cuda, gpu = False, None
    return {
        "ok": True,
        "versie": VERSIE,
        "model": MODEL_KEUZE,
        "model_geladen": _pipe is not None,
        "video_model_geladen": _video_pipe is not None,
        "video_model": _video_model,
        "beeld_model": _pipe_model,
        "ontlaad_na": ONTLAAD_NA,
        "cuda": cuda,
        "gpu": gpu,
    }


@app.post("/jobs")
def start(opdracht: Opdracht) -> dict[str, str]:
    if opdracht.soort == "cast":
        if not opdracht.cast:
            raise HTTPException(400, "Geen spelers of plekken.")
        if any(not c.beeld.strip() for c in opdracht.cast if c.opnieuw or not c.referentie):
            raise HTTPException(400, "Elke speler of plek heeft een omschrijving nodig.")
        opdracht.prompt = opdracht.prompt or "Cast"
    elif opdracht.soort == "sleutelbeelden":
        if not opdracht.scenes:
            raise HTTPException(400, "Geen scènes.")
        if any(not sc.beeld.strip() for sc in opdracht.scenes if sc.opnieuw or not sc.sleutel):
            raise HTTPException(400, "Elke scène waarvan een beeld gemaakt wordt heeft een beeldomschrijving nodig.")
        opdracht.prompt = opdracht.prompt or "Sleutelbeelden"
    elif opdracht.soort == "film":
        if not opdracht.scenes:
            raise HTTPException(400, "Een film heeft minstens één scène nodig.")
        if any(not sc.beeld.strip() for sc in opdracht.scenes if not sc.bestand):
            raise HTTPException(400, "Elke scène die nog gemaakt moet worden heeft een beeldomschrijving nodig.")
        opdracht.prompt = opdracht.prompt or (opdracht.scenes[0].titel or opdracht.scenes[0].gebeurt or "Film")
    elif opdracht.soort == "video":
        # Met bron_beeld: dat beeld laten bewegen. Zonder: een video maken uit alleen de tekst.
        if not opdracht.prompt.strip():
            if not opdracht.bron_beeld:
                raise HTTPException(400, "Lege prompt.")
            opdracht.prompt = "The scene comes to life with subtle, natural motion and a slow, steady camera."
    elif not opdracht.prompt.strip():
        raise HTTPException(400, "Lege prompt.")
    job_id = uuid.uuid4().hex[:12]
    taak = Taak(job_id, opdracht)
    TAKEN[job_id] = taak
    threading.Thread(target=_draai, args=(taak,), daemon=True).start()
    return {"job_id": job_id}


@app.delete("/jobs/{job_id}")
def verwijder(job_id: str) -> dict[str, bool]:
    """Verwijdert een afgeronde opdracht met alle bestanden (beeld, video, scènes). Een lopende opdracht niet."""
    taak = TAKEN.get(job_id)
    if taak is None:
        raise HTTPException(404, "Onbekende taak.")
    if taak.status in ("wachtend", "bezig"):
        raise HTTPException(409, "Deze opdracht loopt nog.")

    # Alleen een map direct onder uitvoer/ - nooit iets daarbuiten.
    map_ = (UITVOER / job_id).resolve()
    if map_.parent != UITVOER.resolve():
        raise HTTPException(400, "Ongeldig pad.")

    if map_.is_dir():
        shutil.rmtree(map_)
    TAKEN.pop(job_id, None)
    return {"verwijderd": True}


@app.post("/ontladen")
def ontladen() -> dict[str, bool]:
    """Laat de modellen meteen los en stopt de service, om al het geheugen vrij te geven. Kan niet tijdens een opdracht."""
    if not _wachtrij_slot.acquire(blocking=False):
        raise HTTPException(409, "Er loopt een opdracht.")
    try:
        ontladen = _ontlaad_alles()
        if ontladen:
            # Even wachten zodat dit antwoord nog verstuurd wordt.
            threading.Timer(0.5, lambda: os._exit(0)).start()
        return {"ontladen": ontladen}
    finally:
        _wachtrij_slot.release()


@app.post("/jobs/{job_id}/stop")
def stop(job_id: str) -> dict[str, bool]:
    if job_id not in TAKEN:
        raise HTTPException(404, "Onbekende taak.")
    TAKEN[job_id].stop = True
    return {"ok": True}


@app.get("/jobs/{job_id}")
def status(job_id: str) -> dict[str, Any]:
    if job_id not in TAKEN:
        raise HTTPException(404, "Onbekende taak.")
    return TAKEN[job_id].als_dict()


@app.get("/jobs")
def lijst(soort: str | None = None) -> list[dict[str, Any]]:
    klaar = [t for t in TAKEN.values() if t.status == "klaar" and (soort is None or t.opdracht.soort == soort)]
    return [t.als_dict() for t in sorted(klaar, key=lambda t: t.gemaakt_op, reverse=True)]
