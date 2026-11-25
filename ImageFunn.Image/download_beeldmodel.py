"""Haalt de gewichten van een extra beeldmodel op, zodat het eerste gebruik niet hoeft te wachten.
Gebruik: .venv\\Scripts\\python.exe download_beeldmodel.py zimage|fluxdev

zimage: tekstencoder (Qwen3-4B), VAE en configuratie van Tongyi-MAI/Z-Image-Turbo (±8,3 GB; het fp32-beeldnetwerk van
        24,6 GB niet) + het beeldnetwerk als GGUF Q8_0 van unsloth (±7,2 GB).
fluxdev: alleen het beeldnetwerk van FLUX.1-dev als GGUF Q6_K (±9,9 GB); tekstencoders en VAE komen van FLUX.1-schnell.
"""
import sys

from huggingface_hub import hf_hub_download, snapshot_download

import service_modellen as sm

keuze = sys.argv[1]
if keuze == "zimage":
    pad = snapshot_download(sm.ZIMAGE_REPO, ignore_patterns=["transformer/*.safetensors", "*.md", "*.png", "*.jpg", "*.gif", "assets/*"])
    print("KLAAR", sm.ZIMAGE_REPO, pad, flush=True)  # transformer/config.json komt wel mee (nodig voor de GGUF)
    pad = hf_hub_download(sm.ZIMAGE_GGUF_REPO, sm.ZIMAGE_GGUF)
    print("KLAAR", sm.ZIMAGE_GGUF_REPO, pad, flush=True)
elif keuze == "fluxdev":
    pad = hf_hub_download(sm.FLUXDEV_GGUF_REPO, sm.FLUXDEV_GGUF)
    print("KLAAR", sm.FLUXDEV_GGUF_REPO, pad, flush=True)
else:
    raise SystemExit("Onbekend: " + keuze)
