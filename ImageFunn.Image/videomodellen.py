"""
De videomodellen die ImageFunn kan gebruiken, op één plek: hoe elk model geladen wordt, hoe het een beeld laat bewegen
(beeld naar video), en welke instellingen erbij horen (stappen, beelden per seconde, formaat-blok, guidance).

service.py kent alleen deze lijst: een nieuw videomodel erbij zetten is een nieuwe `Videomodel` in MODELLEN.
Elke loader krijgt `meld(tekst)` om voortgang naar de app te sturen, en geeft de pijplijn terug die al op de GPU/CPU
klaarstaat (met offload). `i2v` maakt van die pijplijn er één die vanaf een beeld werkt, met dezelfde onderdelen
(from_pipe), zodat het model maar één keer in het geheugen staat.

Er zijn twee modellen, allebei Wan 2.2 (5B): het gewone (30 stappen met guidance) en een gedistilleerde versie van
FastVideo die in 3 stappen zonder guidance werkt (±20 keer minder rekenwerk). Ze delen de VAE en de tekstencoder.
"""

from __future__ import annotations

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
    guidance: float = 5.0    # 1.0 = zonder guidance (dan telt de negatieve prompt niet mee)
    voorproef: bool = False  # kan het tussenbeeld uit de latents gedecodeerd worden (zie service._maak_voorproef)?
    negatief: str = NEGATIEF_STANDAARD
    # Hugging Face-repo's met de gewichten (voor download_videomodel.py): (repo, bestandspatronen); () = alles.
    repos: tuple[tuple[str, tuple[str, ...]], ...] = ()

    def frames(self, gewenst: int) -> int:
        return (gewenst - 1) // self.blok * self.blok + 1

    def roep_aan(self, pipe, *, prompt, negatief, breedte, hoogte, frames, stappen, seed, bron=None, stap_klaar=None):
        """Maakt de video en geeft de lijst beelden terug. `bron` = het beeld om te animeren (beeld naar video)."""
        import torch

        kw: dict[str, Any] = {
            "prompt": prompt,
            "negative_prompt": negatief or self.negatief,
            "width": breedte,
            "height": hoogte,
            "num_frames": frames,
            "num_inference_steps": stappen,
            "guidance_scale": self.guidance,
            "generator": torch.Generator("cuda").manual_seed(seed),
        }
        if bron is not None:
            kw["image"] = bron
        if stap_klaar is not None:
            kw["callback_on_step_end"] = stap_klaar
        if hasattr(pipe.scheduler, "zet_zaad"):
            pipe.scheduler.zet_zaad(seed)
        return pipe(**kw).frames[0]


# ------------------------------------------------------------------ tekst omzetten


class TekstKlaar(Exception):
    """Afgebroken na het omzetten van de tekst (zie bereid_tekst_voor): de rest van de pijplijn hoeft niet."""


class _Tekstencoders:
    """
    De tekstencoder(s) van een videomodel: alleen in het geheugen terwijl de tekst wordt omgezet.

    Ze zijn groot (Wan ±11 GB) en alleen nodig om de tekst om te zetten - daarna niet meer. Bleven ze staan, dan liep
    bij het decoderen van de video aan het eind het geheugen vol en crashte de service (gemeten bij Wan op 768x512).
    Daarom worden ze vlak voor het omzetten geladen en meteen daarna losgelaten. Het resultaat (een paar MB per tekst)
    wordt bewaard, zodat dezelfde tekst niet opnieuw omgezet hoeft te worden. Eén exemplaar per tekstencoder, gedeeld
    door alle pijplijnen die hem gebruiken.
    """

    MAX_BEWAARD = 64

    def __init__(self, repo: str, namen: tuple[str, ...]):
        self.repo = repo
        self.namen = namen
        self.klassen: tuple | None = None
        self.geladen: tuple | None = None
        self.bewaard: dict[tuple, tuple] = {}
        self.vast = False          # tijdens bereid_tekst_voor: niet loslaten tussen de teksten door
        self.alleen_tekst = False  # tijdens bereid_tekst_voor: na het omzetten de pijplijn afbreken

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

    def bewaar(self, sleutel: tuple | None, uitkomst: tuple) -> None:
        if sleutel is None:
            return
        if len(self.bewaard) >= self.MAX_BEWAARD:
            self.bewaard.pop(next(iter(self.bewaard)))
        self.bewaard[sleutel] = uitkomst


def _tekstsleutel(args: tuple, kwargs: dict) -> tuple | None:
    """Waaraan een aanroep van encode_prompt herkend wordt; None als er al tensors meegegeven zijn (niet te bewaren)."""
    import torch

    delen = []
    for naam, waarde in [(str(i), a) for i, a in enumerate(args)] + sorted(kwargs.items()):
        if isinstance(waarde, torch.Tensor):
            return None
        delen.append((naam, repr(waarde)))
    return tuple(delen)


def _tekst_op_processor(pipe, tekst: _Tekstencoders):
    """
    Laat de pijplijn de tekst omzetten op de processor in plaats van op de videokaart, en de tekstencoders daarna los.

    Met enable_model_cpu_offload werd de tekstencoder voor het omzetten in zijn geheel naar de videokaart geschoven;
    met ±11 GB paste dat maar net naast de rest. Hier staat hij op de processor en buiten de offload-keten: het omzetten
    duurt langer, maar alleen het resultaat (een paar MB) gaat naar de videokaart. De kwaliteit is gelijk.
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
        sleutel = _tekstsleutel(args, kwargs)
        uitkomst = tekst.bewaard.get(sleutel) if sleutel is not None else None
        if uitkomst is None:
            for naam, model in zip(tekst.namen, tekst.laad()):
                setattr(pipe, naam, model)
            try:
                uitkomst = origineel(*args, device=torch.device("cpu"), **kwargs)
            finally:
                for naam in tekst.namen:
                    setattr(pipe, naam, None)
                if not tekst.vast:
                    tekst.los()
            tekst.bewaar(sleutel, uitkomst)
        if tekst.alleen_tekst:
            raise TekstKlaar()
        return tuple(deel.to(doel) if isinstance(deel, torch.Tensor) else deel for deel in uitkomst)

    pipe.encode_prompt = encode_prompt
    pipe._jaba_tekst = tekst


def bereid_tekst_voor(vm: Videomodel, pipe, aanroepen: list[dict], meld=None) -> None:
    """
    Zet de teksten van een reeks aanroepen (bijv. alle scènes van een film) in één keer om: de tekstencoder wordt één
    keer geladen in plaats van per aanroep, en de uitkomsten worden bewaard. Elke aanroep loopt de pijplijn in tot de
    tekst omgezet is en breekt dan af; de echte aanroep later vindt de bewaarde tekst. `aanroepen` bevat de argumenten
    voor roep_aan (zonder stap_klaar); een plaatsvervangend bronbeeld is genoeg, het beeld wordt nog niet gebruikt.
    meld(k, totaal) wordt voor elke tekst aangeroepen.
    """
    tekst: _Tekstencoders | None = getattr(pipe, "_jaba_tekst", None)
    if tekst is None:
        return
    tekst.vast = True
    tekst.alleen_tekst = True
    try:
        for k, kw in enumerate(aanroepen):
            if meld:
                meld(k, len(aanroepen))
            try:
                vm.roep_aan(pipe, **kw)
            except TekstKlaar:
                pass
    finally:
        tekst.alleen_tekst = False
        tekst.vast = False
        tekst.los()


# ------------------------------------------------------------------ Wan 2.2 (5B)

WAN_ID = "Wan-AI/Wan2.2-TI2V-5B-Diffusers"

# De transformer van Wan staat op Hugging Face in float32 (19 GB). Hij rekent toch in bfloat16, dus die omzetting
# doen we één keer en bewaren het resultaat hier (±9,5 GB): bij elk volgende laden scheelt dat de helft van het
# werkgeheugen voor dit deel. Met 32 GB werkgeheugen en een klein wisselbestand liep het laden van Wan anders vol en
# crashte de service halverwege. De map hoort bij de commit van het model waaruit hij gemaakt is: komt er een nieuwe
# versie van Wan binnen, dan wordt hij opnieuw gemaakt (de float32-bestanden worden dan opnieuw gedownload).
WAN_TRANSFORMER_CACHE = Path(__file__).parent / "modellen" / "wan22_ti2v_5b_transformer_bf16"

# De tekstencoder van Wan (umT5-XXL, ±11 GB): alleen in het geheugen tijdens het omzetten. De snelle Wan gebruikt dezelfde.
_WAN_TEKST = _Tekstencoders(WAN_ID, ("text_encoder",))


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


def _wan_pijplijn(transformer):
    """De Wan-pijplijn om een transformer heen: VAE (float32, zo is hij getraind) en tekstencoder van Wan zelf."""
    import torch
    from diffusers import AutoencoderKLWan, WanPipeline

    # De gewichten van het videonetwerk bewaard in 8-bit (float8), rekenen gebeurt per laag in bfloat16. In bfloat16
    # alleen is het netwerk al 10 GB; met de tussenresultaten kwam de piek op 13,7 GB, meer dan de 12 GB van de kaart.
    # Windows leende de rest uit het werkgeheugen en dat maakte elke stap ruim een minuut (gemeten: 88 s/stap). In 8-bit
    # is het ±5 GB en past alles; het kost een heel klein beetje precisie. Normlagen en inbeddingen blijven ongemoeid.
    # Pas ná from_pretrained: dat zet meegegeven onderdelen nog om naar torch_dtype, en dat liep stuk op de 8-bit-opslag.
    vae = AutoencoderKLWan.from_pretrained(WAN_ID, subfolder="vae", torch_dtype=torch.float32)
    pipe = WanPipeline.from_pretrained(WAN_ID, vae=vae, transformer=transformer, torch_dtype=torch.bfloat16)
    pipe.transformer.enable_layerwise_casting(storage_dtype=torch.float8_e4m3fn, compute_dtype=torch.bfloat16)
    pipe.enable_model_cpu_offload()
    _tekst_op_processor(pipe, _WAN_TEKST)
    pipe.vae.enable_tiling()
    pipe.set_progress_bar_config(disable=True)
    return pipe


def _laad_wan(meld):
    transformer = _wan_transformer(meld)
    meld("Videomodel laden (Wan 2.2)… (de eerste keer duurt dit langer)")
    return _wan_pijplijn(transformer)


def _wan_i2v(pipe):
    from diffusers import WanImageToVideoPipeline

    # Zelf opbouwen uit dezelfde onderdelen in plaats van from_pipe: dat zet alles om naar één type (zonder opgave
    # float32), ook het 8-bit-netwerk - en daar crashte het proces op. Zo blijven de onderdelen precies zoals ze zijn,
    # met hun offload-haken. expand_timesteps: Wan 2.2 (5B) houdt het eerste beeld vast via een masker.
    i2v = WanImageToVideoPipeline(
        tokenizer=pipe.tokenizer,
        text_encoder=None,
        vae=pipe.vae,
        scheduler=pipe.scheduler,
        transformer=pipe.transformer,
        expand_timesteps=True,
    )
    i2v.set_progress_bar_config(disable=True)
    # De tekstencoder staat op None (zie _WAN_TEKST); ook deze pijplijn zet de tekst om op de processor.
    _tekst_op_processor(i2v, _WAN_TEKST)
    return i2v


# ------------------------------------------------------------------ Wan 2.2 snel (FastWan, 3 stappen)

# FastVideo heeft Wan 2.2 (5B) gedistilleerd met DMD: dezelfde opbouw, eigen gewichten (±10 GB, al in bfloat16), en
# maar 3 stappen zonder guidance in plaats van 30 met (dus 3 in plaats van 60 keer het videonetwerk). Alleen de
# transformer komt uit deze repo; VAE en tekstencoder zijn die van Wan zelf.
FASTWAN_ID = "FastVideo/FastWan2.2-TI2V-5B-FullAttn-Diffusers"


def _dmd_scheduler():
    """
    Het stappenschema waarvoor FastWan getraind is (DMD): op tijdstip 1000, 757 en 522 voorspelt het netwerk telkens
    het eindbeeld; tussen de stappen door krijgt dat eindbeeld weer verse ruis tot het volgende tijdstip. Diffusers
    heeft dit schema niet als scheduler, vandaar deze kleine eigen versie.
    """
    import torch
    from diffusers.configuration_utils import ConfigMixin, register_to_config
    from diffusers.schedulers.scheduling_utils import SchedulerMixin

    class DmdScheduler(SchedulerMixin, ConfigMixin):
        config_name = "scheduler_config.json"
        order = 1

        @register_to_config
        def __init__(self, num_train_timesteps: int = 1000, tijden: tuple = (1000, 757, 522)):
            self.timesteps = torch.tensor(tijden, dtype=torch.float32)
            self.sigmas = torch.cat([self.timesteps / num_train_timesteps, torch.zeros(1)])
            self._stap = 0
            self._zaad = 0
            self._generator = None

        def zet_zaad(self, seed: int) -> None:
            self._zaad = seed
            self._generator = None

        def set_timesteps(self, num_inference_steps: int | None = None, device=None, **_):
            tijden = list(self.config.tijden)[: max(1, num_inference_steps or len(self.config.tijden))]
            self.timesteps = torch.tensor(tijden, dtype=torch.float32, device=device)
            self.sigmas = torch.cat([self.timesteps / self.config.num_train_timesteps, torch.zeros(1, device=device)])
            self._stap = 0

        def set_begin_index(self, begin_index: int = 0):
            self._stap = begin_index

        def step(self, model_output, timestep, sample, return_dict: bool = False, **_):
            sigma = self.sigmas[self._stap]
            # Wan voorspelt de stroom (ruis min beeld): het eindbeeld is dan de huidige stand min sigma keer die stroom.
            eindbeeld = sample.float() - sigma * model_output.float()
            self._stap += 1
            if self._stap < len(self.timesteps):
                volgende = self.sigmas[self._stap]
                if self._generator is None or self._generator.device != eindbeeld.device:
                    self._generator = torch.Generator(eindbeeld.device).manual_seed(self._zaad + 1)
                ruis = torch.randn(eindbeeld.shape, generator=self._generator, device=eindbeeld.device)
                uit = (1 - volgende) * eindbeeld + volgende * ruis
            else:
                uit = eindbeeld
            uit = uit.to(sample.dtype)
            if return_dict:
                from diffusers.schedulers.scheduling_utils import SchedulerOutput

                return SchedulerOutput(prev_sample=uit)
            return (uit,)

    return DmdScheduler()


def _laad_wan_snel(meld):
    import torch
    from diffusers import WanTransformer3DModel

    meld("Videomodel laden (Wan 2.2 snel)… (de eerste keer wordt ±10 GB gedownload)")
    transformer = WanTransformer3DModel.from_pretrained(FASTWAN_ID, subfolder="transformer", torch_dtype=torch.bfloat16)
    pipe = _wan_pijplijn(transformer)
    pipe.scheduler = _dmd_scheduler()
    return pipe


def _wan_snel_i2v(pipe):
    i2v = _wan_i2v(pipe)
    i2v.scheduler = _dmd_scheduler()
    return i2v


MODELLEN: dict[str, Videomodel] = {
    "wan": Videomodel(
        sleutel="wan",
        naam="Wan 2.2 (5B)",
        laad=_laad_wan,
        i2v=_wan_i2v,
        stappen=30,
        guidance=5.0,
        voorproef=True,
        repos=((WAN_ID, ()),),
    ),
    "wan_snel": Videomodel(
        sleutel="wan_snel",
        naam="Wan 2.2 snel (3 stappen)",
        laad=_laad_wan_snel,
        i2v=_wan_snel_i2v,
        stappen=3,
        guidance=1.0,
        repos=((FASTWAN_ID, ("transformer/*",)),),
    ),
}

STANDAARD = "wan"


def kies(sleutel: str) -> Videomodel:
    if sleutel not in MODELLEN:
        raise RuntimeError(f"Onbekend videomodel '{sleutel}' (beschikbaar: {', '.join(MODELLEN)}).")
    return MODELLEN[sleutel]
