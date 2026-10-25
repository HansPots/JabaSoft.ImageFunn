"""
De videomodellen die ImageFunn kan gebruiken, op één plek: hoe elk model geladen wordt, hoe het een beeld laat bewegen
(beeld naar video), en welke instellingen erbij horen (stappen, beelden per seconde, formaat-blok, guidance).

service.py kent alleen deze lijst: een nieuw videomodel erbij zetten is een nieuwe `Videomodel` in MODELLEN.
Elke loader krijgt `meld(tekst)` om voortgang naar de app te sturen, en geeft de pijplijn terug die al op de GPU/CPU
klaarstaat (met offload). `i2v` maakt van die pijplijn er één die vanaf een beeld werkt, met dezelfde onderdelen
(from_pipe), zodat het model maar één keer in het geheugen staat.
"""

from __future__ import annotations

import inspect
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Callable

NEGATIEF_STANDAARD = "worst quality, inconsistent motion, blurry, jittery, distorted, static, still image"


@dataclass(frozen=True)
class Videomodel:
    sleutel: str
    naam: str
    laad: Callable[[Callable[[str], None]], Any]
    i2v: Callable[[Any], Any]
    blok: int = 4            # het aantal beelden is blok * n + 1
    fps: int = 24
    stappen: int = 30
    maat: int = 32           # breedte en hoogte zijn een veelvoud hiervan
    guidance: float | None = 5.0
    voorproef: bool = False  # kan het tussenbeeld uit de latents gedecodeerd worden (zie service._maak_voorproef)?
    negatief: str = NEGATIEF_STANDAARD
    frames_arg: str = "num_frames"  # hoe de pijplijn het aantal beelden noemt
    callback: bool = True           # kent de pijplijn callback_on_step_end (voortgang per stap)?
    # Hugging Face-repo's met de gewichten (voor download_videomodel.py): (repo, bestandspatronen); () = alles.
    repos: tuple[tuple[str, tuple[str, ...]], ...] = ()
    t2v: bool = True                # kan het model ook uit alleen tekst een video maken? (anders alleen beeld naar video)

    def frames(self, gewenst: int) -> int:
        return (gewenst - 1) // self.blok * self.blok + 1

    def args(self) -> dict:
        return {"guidance_scale": self.guidance} if self.guidance is not None else {}

    def roep_aan(self, pipe, *, prompt, negatief, breedte, hoogte, frames, stappen, seed, bron=None, stap_klaar=None):
        """Maakt de video en geeft de lijst beelden terug. `bron` = het beeld om te animeren (beeld naar video)."""
        import torch

        kw: dict[str, Any] = {
            "prompt": prompt,
            "negative_prompt": negatief or self.negatief,
            "width": breedte,
            "height": hoogte,
            self.frames_arg: frames,
            "num_inference_steps": stappen,
            **self.args(),
            "generator": torch.Generator("cuda").manual_seed(seed),
        }
        if bron is not None:
            kw["image"] = bron
        if stap_klaar is not None and self.callback:
            kw["callback_on_step_end"] = stap_klaar

        # Elke pijplijn kent net andere parameters (niet elke pijplijn kent guidance_scale of een eigen formaat):
        # geef alleen door wat hij aanneemt.
        toegestaan = set(inspect.signature(pipe.__call__).parameters)
        kw = {k: v for k, v in kw.items() if k in toegestaan}

        haak = None
        if stap_klaar is not None and "callback_on_step_end" not in toegestaan:
            # Geen callback: tel de aanroepen van het videonetwerk (per stap één, met CFG twee).
            per_stap = 2 if (self.guidance or 1.0) > 1.0 else 1
            teller = {"n": 0}

            def tel(_module, _args, _uit):
                teller["n"] += 1
                if teller["n"] % per_stap == 0:
                    stap_klaar(pipe, teller["n"] // per_stap - 1, None, {})

            haak = pipe.transformer.register_forward_hook(tel)
        try:
            return pipe(**kw).frames[0]
        finally:
            if haak is not None:
                haak.remove()


def _bf16():
    import torch

    return torch.bfloat16


# ------------------------------------------------------------------ Wan 2.2 (5B)

WAN_ID = "Wan-AI/Wan2.2-TI2V-5B-Diffusers"

# De transformer van Wan staat op Hugging Face in float32 (19 GB). Hij rekent toch in bfloat16, dus die omzetting
# doen we één keer en bewaren het resultaat hier (±9,5 GB): bij elk volgende laden scheelt dat de helft van het
# werkgeheugen voor dit deel. Met 32 GB werkgeheugen en een klein wisselbestand liep het laden van Wan anders vol en
# crashte de service halverwege. De map hoort bij de commit van het model waaruit hij gemaakt is: komt er een nieuwe
# versie van Wan binnen, dan wordt hij opnieuw gemaakt.
WAN_TRANSFORMER_CACHE = Path(__file__).parent / "modellen" / "wan22_ti2v_5b_transformer_bf16"


def _wan_transformer(meld):
    """De transformer in bfloat16: uit de bewaarde omzetting, of eerst omzetten (eenmalig, een paar minuten)."""
    import gc

    import torch
    from diffusers import WanTransformer3DModel
    from huggingface_hub import snapshot_download

    commit = Path(snapshot_download(WAN_ID, allow_patterns=["transformer/config.json"])).name
    merk = WAN_TRANSFORMER_CACHE / "commit.txt"

    if not (merk.is_file() and merk.read_text(encoding="utf-8").strip() == commit):
        meld("Videomodel Wan 2.2 voor het eerst omzetten naar 16-bits (eenmalig, een paar minuten)…")
        # Alleen de transformer, nog vóór de rest van de pijplijn geladen is: zo is er genoeg werkgeheugen voor.
        # Per deel ingelezen en meteen omgezet, dus de piek is de bfloat16-versie plus één deel van 4,7 GB.
        transformer = WanTransformer3DModel.from_pretrained(WAN_ID, subfolder="transformer", torch_dtype=torch.bfloat16)
        WAN_TRANSFORMER_CACHE.mkdir(parents=True, exist_ok=True)
        transformer.save_pretrained(str(WAN_TRANSFORMER_CACHE), safe_serialization=True)
        merk.write_text(commit, encoding="utf-8")
        del transformer
        gc.collect()

    return WanTransformer3DModel.from_pretrained(str(WAN_TRANSFORMER_CACHE), torch_dtype=torch.bfloat16)


def _laad_wan(meld):
    import torch
    from diffusers import AutoencoderKLWan, WanPipeline

    transformer = _wan_transformer(meld)

    meld("Videomodel laden (Wan 2.2)… (de eerste keer duurt dit langer)")
    # De VAE rekent in float32 (zo is het model getraind); de rest in bfloat16.
    vae = AutoencoderKLWan.from_pretrained(WAN_ID, subfolder="vae", torch_dtype=torch.float32)
    pipe = WanPipeline.from_pretrained(WAN_ID, vae=vae, transformer=transformer, torch_dtype=torch.bfloat16)
    pipe.enable_model_cpu_offload()
    _tekst_op_processor(pipe, _WAN_TEKST)
    pipe.vae.enable_tiling()
    pipe.set_progress_bar_config(disable=True)
    return pipe


def _wan_i2v(pipe):
    import torch
    from diffusers import WanImageToVideoPipeline

    i2v = WanImageToVideoPipeline.from_pipe(pipe, dtype=_bf16())
    i2v.vae.to(torch.float32)  # from_pipe zette ook de VAE op bfloat16; Wan wil die in float32
    # De tekstencoder staat op None (zie _WAN_TEKST); ook deze pijplijn zet de tekst om op de processor.
    _tekst_op_processor(i2v, _WAN_TEKST)
    return i2v


# ------------------------------------------------------------------ SANA-Video (2B)

SANA_ID = "Efficient-Large-Model/SANA-Video_2B_480p_diffusers"


def _laad_sana(meld):
    import torch
    from diffusers import SanaVideoPipeline

    meld("Videomodel laden (SANA-Video)… (de eerste keer duurt dit langer)")
    pipe = SanaVideoPipeline.from_pretrained(SANA_ID, torch_dtype=torch.bfloat16)
    pipe.vae.to(torch.float32)  # de VAE rekent in float32, de rest in bfloat16
    pipe.text_encoder.to(torch.bfloat16)
    pipe.enable_model_cpu_offload()
    if hasattr(pipe.vae, "enable_tiling"):
        pipe.vae.enable_tiling()
    pipe.set_progress_bar_config(disable=True)
    return pipe


def _sana_i2v(pipe):
    import torch
    from diffusers import SanaImageToVideoPipeline

    i2v = SanaImageToVideoPipeline.from_pipe(pipe, dtype=_bf16())
    i2v.vae.to(torch.float32)
    return i2v


# ------------------------------------------------------------------ Kandinsky 5.0 Lite (2B)
# Tekst naar video: de 16-staps-gedistilleerde versie (snel). Beeld naar video: de Lite-I2V; die gebruikt dezelfde
# tekst- en beeldonderdelen, dus daarvan halen we alleen het videonetwerk ("transformer") op.

KANDINSKY_T2V = "kandinskylab/Kandinsky-5.0-T2V-Lite-distilled16steps-5s-Diffusers"
KANDINSKY_I2V = "kandinskylab/Kandinsky-5.0-I2V-Lite-5s-Diffusers"


class _Tekstencoders:
    """
    De tekstencoder(s) van een videomodel: alleen in het geheugen terwijl de tekst wordt omgezet.

    Ze zijn groot (Kandinsky ±22 GB, Wan ±11 GB) en alleen nodig om de tekst om te zetten - daarna niet meer. Bleven
    ze staan, dan liep bij het decoderen van de video aan het eind het geheugen vol en crashte de service (gemeten bij
    Kandinsky: alle stappen klaar, wisselbestand vol, crash in c10.dll; bij Wan op 768x512 hetzelfde). Daarom worden
    ze vlak voor het omzetten geladen (of hergebruikt) en meteen daarna losgelaten. Kost bij een volgende opdracht
    ±20-30 s laden. Eén exemplaar per model, gedeeld door tekst-naar-video en beeld-naar-video.
    """

    def __init__(self, repo: str, namen: tuple[str, ...]):
        self.repo = repo
        self.namen = namen
        self.klassen: tuple | None = None
        self.geladen: tuple | None = None

    def laad(self):
        import torch

        if self.geladen is None:
            self.geladen = tuple(
                klasse.from_pretrained(self.repo, subfolder=naam, torch_dtype=torch.bfloat16)
                for klasse, naam in zip(self.klassen, self.namen)
            )
        return self.geladen

    def los(self):
        import gc

        self.geladen = None
        gc.collect()


def _tekst_op_processor(pipe, tekst: _Tekstencoders):
    """
    Laat de pijplijn de tekst omzetten op de processor in plaats van op de videokaart, en de tekstencoders daarna los.

    De tekstencoder van Kandinsky (Qwen2.5-VL, 7B) is in bfloat16 15,4 GB en past niet op een videokaart van 12 GB.
    Met enable_model_cpu_offload werd hij voor het omzetten toch in zijn geheel naar de videokaart geschoven; het
    stuurprogramma leende de rest uit het werkgeheugen, dat liep vol en de service crashte (gemeten: videokaart 11,5 GB
    vol, gedeeld geheugen oplopend, toezegruimte op 0). Wan (±11 GB) paste nog net, maar krap. Hier staan de
    tekstencoders op de processor en buiten de offload-keten: het omzetten duurt langer, maar alleen het resultaat
    (een paar MB) gaat naar de videokaart. De kwaliteit is gelijk. Na het omzetten gaan ze uit het geheugen.
    """
    import torch
    from accelerate.hooks import remove_hook_from_module

    aanwezig = [getattr(pipe, naam, None) for naam in tekst.namen]
    tekstencoders = [m for m in aanwezig if m is not None]
    if tekst.klassen is None and len(tekstencoders) == len(tekst.namen):
        tekst.klassen = tuple(type(m) for m in tekstencoders)
        tekst.geladen = tuple(tekstencoders)

    for model in tekstencoders:
        # De offload-hook zou hem bij elke aanroep naar de videokaart verhuizen - die gaat eraf.
        remove_hook_from_module(model, recurse=True)
        model.to("cpu")

    # Ook de hooks van de rest mogen niet naar de tekstencoders blijven wijzen, anders komt hun geheugen nooit vrij.
    pipe._all_hooks = [h for h in getattr(pipe, "_all_hooks", []) if not any(h.model is m for m in tekstencoders)]
    for h in pipe._all_hooks:
        vorige = getattr(h.hook, "prev_module_hook", None)
        if vorige is not None and any(vorige.model is m for m in tekstencoders):
            h.hook.prev_module_hook = None
    for naam in tekst.namen:
        setattr(pipe, naam, None)

    origineel = pipe.encode_prompt

    def encode_prompt(*args, device=None, **kwargs):
        doel = device or pipe._execution_device
        for naam, model in zip(tekst.namen, tekst.laad()):
            setattr(pipe, naam, model)
        try:
            uitkomst = origineel(*args, device=torch.device("cpu"), **kwargs)
        finally:
            for naam in tekst.namen:
                setattr(pipe, naam, None)
            tekst.los()
        return tuple(deel.to(doel) if isinstance(deel, torch.Tensor) else deel for deel in uitkomst)

    pipe.encode_prompt = encode_prompt


def _laad_kandinsky(meld):
    import torch
    from diffusers import Kandinsky5T2VPipeline

    meld("Videomodel laden (Kandinsky 5)… (de eerste keer duurt dit langer)")
    pipe = Kandinsky5T2VPipeline.from_pretrained(KANDINSKY_T2V, torch_dtype=torch.bfloat16)
    pipe.enable_model_cpu_offload()
    _tekst_op_processor(pipe, _KANDINSKY_TEKST)
    if hasattr(pipe.vae, "enable_tiling"):
        pipe.vae.enable_tiling()
    pipe.set_progress_bar_config(disable=True)
    return pipe


def _kandinsky_i2v(pipe):
    import torch
    from diffusers import Kandinsky5I2VPipeline

    # De tekstencoders staan hier op None: _KANDINSKY_TEKST laadt ze pas bij het omzetten van de tekst (gedeeld).
    delen = {k: getattr(pipe, k) for k in ("vae", "text_encoder", "tokenizer", "text_encoder_2", "tokenizer_2", "scheduler")}
    i2v = Kandinsky5I2VPipeline.from_pretrained(KANDINSKY_I2V, torch_dtype=torch.bfloat16, **delen)
    i2v.enable_model_cpu_offload()
    _tekst_op_processor(i2v, _KANDINSKY_TEKST)
    return i2v


# De tekstencoders van Wan (umT5-XXL, ±11 GB) en Kandinsky (±22 GB): alleen in het geheugen tijdens het omzetten.
_WAN_TEKST = _Tekstencoders(WAN_ID, ("text_encoder",))
_KANDINSKY_TEKST = _Tekstencoders(KANDINSKY_T2V, ("text_encoder", "text_encoder_2"))

MODELLEN: dict[str, Videomodel] = {
    "wan": Videomodel(
        sleutel="wan",
        naam="Wan 2.2 (5B)",
        laad=_laad_wan,
        i2v=_wan_i2v,
        blok=4,
        stappen=30,
        guidance=5.0,
        voorproef=True,
        repos=((WAN_ID, ()),),
    ),
    "sana": Videomodel(
        sleutel="sana",
        naam="SANA-Video (2B, snel)",
        laad=_laad_sana,
        i2v=_sana_i2v,
        blok=4,
        fps=16,
        stappen=30,
        guidance=6.0,
        frames_arg="frames",
        repos=((SANA_ID, ()),),
    ),
    "kandinsky": Videomodel(
        sleutel="kandinsky",
        naam="Kandinsky 5 Lite (2B)",
        laad=_laad_kandinsky,
        i2v=_kandinsky_i2v,
        blok=4,
        fps=24,
        stappen=16,        # de gedistilleerde T2V is voor 16 stappen gemaakt
        maat=16,
        guidance=1.0,
        negatief="Static, 2D cartoon, cartoon, 2d animation, paintings, images, worst quality, low quality, ugly, deformed, walking backwards",
        repos=(
            (KANDINSKY_T2V, ()),
            (KANDINSKY_I2V, ("model_index.json", "transformer/*", "scheduler/*")),
        ),
    ),
}

STANDAARD = "wan"


def kies(sleutel: str) -> Videomodel:
    if sleutel not in MODELLEN:
        raise RuntimeError(f"Onbekend videomodel '{sleutel}' (beschikbaar: {', '.join(MODELLEN)}).")
    return MODELLEN[sleutel]
