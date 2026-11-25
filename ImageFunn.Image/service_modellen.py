"""
De twee nieuwere beeldmodellen naast RealVisXL, Juggernaut en FLUX.1-schnell: Z-Image Turbo en FLUX.1-dev. Allebei
laden ze hun beeldnetwerk uit een GGUF-bestand (gekwantiseerd, zodat het op een kaart van 12 GB past); diffusers
ontkwantiseert per laag tijdens het rekenen.

- "zimage": Z-Image Turbo (Tongyi-MAI, 6B, Apache 2.0). Gedistilleerd: 8 stappen zonder guidance, fotografisch, kan
  tekst in beeld zetten. Tekstencoder Qwen3-4B (bf16, ±8 GB, wisselt via cpu-offload).
- "fluxdev": FLUX.1-dev (12B, niet-commerciële licentie). Mooier dan schnell, ±25 stappen, guidance 3,5 (ingebakken,
  geen negatieve prompt). Het beeldnetwerk is de GGUF Q6_K (Q8 is 12,7 GB en past niet); tekstencoders en VAE zijn
  dezelfde als die van FLUX.1-schnell en komen daarvandaan.

Beide kunnen ook bijsturen (beeld naar beeld en inpainting) via hun eigen Img2Img-/Inpaint-pijplijnen.
"""

from __future__ import annotations

import json
from pathlib import Path
from typing import Any, Callable

ZIMAGE_REPO = "Tongyi-MAI/Z-Image-Turbo"
ZIMAGE_GGUF_REPO = "unsloth/Z-Image-Turbo-GGUF"
ZIMAGE_GGUF = "z-image-turbo-Q8_0.gguf"

SCHNELL_REPO = "black-forest-labs/FLUX.1-schnell"
FLUXDEV_GGUF_REPO = "city96/FLUX.1-dev-gguf"
FLUXDEV_GGUF = "flux1-dev-Q6_K.gguf"

# De beschrijving van het FLUX.1-dev-netwerk: die van schnell met guidance_embeds aan (het enige verschil). Zo hoeft de
# afgeschermde FLUX.1-dev-repo niet benaderd te worden.
FLUXDEV_CONFIG = Path(__file__).parent / "modellen" / "flux1_dev_config"

# Per model: stappen, guidance, rekenblok (pixels), en of de negatieve prompt meetelt.
INSTELLINGEN: dict[str, dict[str, Any]] = {
    "zimage": dict(stappen=8, guidance=0.0, blok=16, negatief=False),
    "fluxdev": dict(stappen=25, guidance=3.5, blok=16, negatief=False),
}


def _gguf_pad(repo: str, bestand: str, revisie: Callable[[str], str | None]) -> str:
    from huggingface_hub import hf_hub_download

    return hf_hub_download(repo, bestand, revision=revisie(repo))


def laad_zimage(meld: Callable[[str], None], revisie: Callable[[str], str | None]):
    import torch
    from diffusers import GGUFQuantizationConfig, ZImagePipeline, ZImageTransformer2DModel

    meld("Beeldmodel laden (Z-Image Turbo)…")
    vast = revisie(ZIMAGE_REPO)
    transformer = ZImageTransformer2DModel.from_single_file(
        _gguf_pad(ZIMAGE_GGUF_REPO, ZIMAGE_GGUF, revisie),
        quantization_config=GGUFQuantizationConfig(compute_dtype=torch.bfloat16),
        config=ZIMAGE_REPO, subfolder="transformer", revision=vast,
        torch_dtype=torch.bfloat16,
    )
    pipe = ZImagePipeline.from_pretrained(ZIMAGE_REPO, transformer=transformer, torch_dtype=torch.bfloat16, revision=vast)
    pipe.enable_model_cpu_offload()
    return pipe


def _fluxdev_config(revisie: Callable[[str], str | None]) -> Path:
    if not (FLUXDEV_CONFIG / "transformer" / "config.json").is_file():
        from diffusers import FluxTransformer2DModel

        config = dict(FluxTransformer2DModel.load_config(SCHNELL_REPO, subfolder="transformer", revision=revisie(SCHNELL_REPO)))
        config["guidance_embeds"] = True
        (FLUXDEV_CONFIG / "transformer").mkdir(parents=True, exist_ok=True)
        (FLUXDEV_CONFIG / "transformer" / "config.json").write_text(json.dumps(config, indent=2), encoding="utf-8")
    return FLUXDEV_CONFIG


def laad_fluxdev(meld: Callable[[str], None], revisie: Callable[[str], str | None]):
    import torch
    from diffusers import FluxPipeline, FluxTransformer2DModel, GGUFQuantizationConfig
    from transformers import BitsAndBytesConfig as TransformersBnb
    from transformers import T5EncoderModel

    meld("Beeldmodel laden (FLUX.1-dev)…")
    transformer = FluxTransformer2DModel.from_single_file(
        _gguf_pad(FLUXDEV_GGUF_REPO, FLUXDEV_GGUF, revisie),
        quantization_config=GGUFQuantizationConfig(compute_dtype=torch.bfloat16),
        config=str(_fluxdev_config(revisie)), subfolder="transformer",
        torch_dtype=torch.bfloat16,
    )
    vast = revisie(SCHNELL_REPO)
    # T5 in 4-bit, net als bij schnell: zo wisselt hij netjes met het beeldnetwerk binnen 12 GB.
    t5 = T5EncoderModel.from_pretrained(
        SCHNELL_REPO, subfolder="text_encoder_2",
        quantization_config=TransformersBnb(load_in_4bit=True, bnb_4bit_quant_type="nf4", bnb_4bit_compute_dtype=torch.bfloat16),
        torch_dtype=torch.bfloat16, revision=vast,
    )
    pipe = FluxPipeline.from_pretrained(
        SCHNELL_REPO, transformer=transformer, text_encoder_2=t5, torch_dtype=torch.bfloat16, revision=vast
    )
    return _fluxdev_klaarzetten(_met_t5_wissel(pipe))


def _met_t5_wissel(pipe):
    """
    Dezelfde pijplijn, maar met een klasse die T5 alleen voor het omzetten van de tekst naar de GPU haalt (zie
    _fluxdev_klaarzetten). Werkt voor FluxPipeline, FluxImg2ImgPipeline en FluxInpaintPipeline.
    """
    import torch

    basis = type(pipe)

    class MetT5Wissel(basis):
        @property
        def _execution_device(self):
            return torch.device("cuda")

        def encode_prompt(self, *args, **kwargs):
            self.text_encoder_2.to("cuda")
            try:
                return super().encode_prompt(*args, **kwargs)
            finally:
                self.text_encoder_2.to("cpu")
                torch.cuda.empty_cache()

    pipe.__class__ = MetT5Wissel
    return pipe


def _fluxdev_klaarzetten(pipe):
    """
    Geen model-cpu-offload voor FLUX.1-dev: dan staat het hele beeldnetwerk (9,9 GB) plus het rekenwerk op de kaart, en
    dat loopt op 12 GB over naar het gedeelde geheugen (gemeten 29 s per stap). In plaats daarvan gaat het
    beeldnetwerk per 4 blokken naar de GPU, overlappend met het rekenen (group offload met een eigen stream): gemeten
    2,4 s per stap, piek 4,6 GB. CLIP en VAE zijn klein en blijven op de GPU; T5 (4-bit) komt alleen voor de tekst.
    """
    import torch

    if not getattr(pipe.transformer, "_jabasoft_groep", False):
        from diffusers.hooks import apply_group_offloading

        apply_group_offloading(pipe.transformer, onload_device=torch.device("cuda"), offload_device=torch.device("cpu"),
                               offload_type="block_level", num_blocks_per_group=4, use_stream=True)
        pipe.transformer._jabasoft_groep = True
    pipe.text_encoder.to("cuda")
    pipe.vae.to("cuda")
    return pipe


LADERS = {"zimage": laad_zimage, "fluxdev": laad_fluxdev}


def bewerk_pijplijn(model: str, basis, inpaint: bool):
    """
    Een pijplijn voor bijsturen (beeld naar beeld, of inpainting) met dezelfde onderdelen als <basis>, dus zonder het
    model nog eens te laden. Niet via from_pipe: die cast alles naar één dtype, en dat weigert een GGUF-model.
    """
    if model == "zimage":
        from diffusers import ZImageImg2ImgPipeline as I2I
        from diffusers import ZImageInpaintPipeline as Inpaint
    else:
        from diffusers import FluxImg2ImgPipeline as I2I
        from diffusers import FluxInpaintPipeline as Inpaint
    klasse = Inpaint if inpaint else I2I
    namen = set(klasse._get_signature_keys(klasse)[0]) | set(klasse._optional_components)
    pipe = klasse(**{k: v for k, v in basis.components.items() if k in namen})
    if model == "fluxdev":
        return _fluxdev_klaarzetten(_met_t5_wissel(pipe))
    pipe.enable_model_cpu_offload()
    return pipe
