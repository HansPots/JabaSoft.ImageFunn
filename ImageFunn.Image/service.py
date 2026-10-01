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
import sys
import threading
import traceback
import uuid
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Literal

from fastapi import FastAPI, HTTPException
from pydantic import BaseModel, Field

sys.stdout.reconfigure(encoding="utf-8")
sys.stderr.reconfigure(encoding="utf-8")

app = FastAPI(title="ImageFunn Image Service")

UITVOER = Path(__file__).parent / "uitvoer"
UITVOER.mkdir(exist_ok=True)

MODEL_KEUZE = os.environ.get("IMAGEFUNN_MODEL", "sdxl").lower()

Status = Literal["wachtend", "bezig", "klaar", "mislukt"]


class Opdracht(BaseModel):
    prompt: str
    negatief: str = ""
    breedte: int = Field(1024, ge=256, le=2048)
    hoogte: int = Field(1024, ge=256, le=2048)
    stappen: int | None = None
    seed: int | None = None


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
            "seed": self.seed,
            "foutmelding": self.foutmelding,
        }


TAKEN: dict[str, Taak] = {}
_wachtrij_slot = threading.Lock()  # één beeld tegelijk: de GPU is maar één keer aanwezig
_pipe = None
_pipe_slot = threading.Lock()


def _laad_pijplijn(meld):
    """Laadt het model één keer; daarna blijft het in het geheugen."""
    global _pipe
    with _pipe_slot:
        if _pipe is not None:
            return _pipe

        import torch

        if not torch.cuda.is_available():
            raise RuntimeError("Geen CUDA-GPU gevonden - installeer PyTorch met CUDA (zie requirements.txt).")

        if MODEL_KEUZE == "sdxl":
            from diffusers import StableDiffusionXLPipeline

            meld("Model laden (SDXL) - de eerste keer wordt het gedownload…")
            pipe = StableDiffusionXLPipeline.from_pretrained(
                "stabilityai/stable-diffusion-xl-base-1.0", torch_dtype=torch.float16, variant="fp16"
            )
            pipe.enable_model_cpu_offload()
        else:
            from diffusers import BitsAndBytesConfig as DiffusersBnb
            from diffusers import FluxPipeline, FluxTransformer2DModel
            from transformers import BitsAndBytesConfig as TransformersBnb
            from transformers import T5EncoderModel

            repo = "black-forest-labs/FLUX.1-schnell"
            meld("Model laden (FLUX.1-schnell, 4-bit) - de eerste keer wordt het gedownload (±33 GB)…")
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
        return pipe


def _draai(taak: Taak) -> None:
    with _wachtrij_slot:
        taak.status = "bezig"
        try:
            import torch

            def meld(tekst: str) -> None:
                taak.stap = tekst

            pipe = _laad_pijplijn(meld)
            o = taak.opdracht
            flux = MODEL_KEUZE != "sdxl"
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
                args.update(guidance_scale=6.5, negative_prompt=o.negatief or None)

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


def _lees_bestaande() -> None:
    for meta in UITVOER.glob("*/meta.json"):
        try:
            d = json.loads(meta.read_text(encoding="utf-8"))
            taak = Taak(d["job_id"], Opdracht(prompt=d["prompt"], breedte=d["breedte"], hoogte=d["hoogte"]))
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
    return {"ok": True, "model": MODEL_KEUZE, "model_geladen": _pipe is not None, "cuda": cuda, "gpu": gpu}


@app.post("/jobs")
def start(opdracht: Opdracht) -> dict[str, str]:
    if not opdracht.prompt.strip():
        raise HTTPException(400, "Lege prompt.")
    job_id = uuid.uuid4().hex[:12]
    taak = Taak(job_id, opdracht)
    TAKEN[job_id] = taak
    threading.Thread(target=_draai, args=(taak,), daemon=True).start()
    return {"job_id": job_id}


@app.get("/jobs/{job_id}")
def status(job_id: str) -> dict[str, Any]:
    if job_id not in TAKEN:
        raise HTTPException(404, "Onbekende taak.")
    return TAKEN[job_id].als_dict()


@app.get("/jobs")
def lijst() -> list[dict[str, Any]]:
    klaar = [t for t in TAKEN.values() if t.status == "klaar"]
    return [t.als_dict() for t in sorted(klaar, key=lambda t: t.gemaakt_op, reverse=True)]
