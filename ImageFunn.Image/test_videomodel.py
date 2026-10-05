"""Snelle rooktest van een videomodel zonder de app: laadt het, maakt een korte clip, meet tijd en geheugen,
en bewaart de video plus een contactblad (eerste/midden/laatste beeld) om te bekijken.

Gebruik: .venv\\Scripts\\python.exe test_videomodel.py <sleutel> t2v|i2v [seconden] [breedte] [hoogte] [stappen]
"""

import sys
import time
from pathlib import Path

import torch
from diffusers.utils import export_to_video
from PIL import Image

import videomodellen

sleutel, modus = sys.argv[1], sys.argv[2]
seconden = float(sys.argv[3]) if len(sys.argv) > 3 else 2.0
vm = videomodellen.kies(sleutel)
breedte = int(sys.argv[4]) if len(sys.argv) > 4 else 640
hoogte = int(sys.argv[5]) if len(sys.argv) > 5 else 384
stappen = int(sys.argv[6]) if len(sys.argv) > 6 else vm.stappen
breedte, hoogte = breedte // vm.maat * vm.maat, hoogte // vm.maat * vm.maat

uit = Path(__file__).parent / "uitvoer" / "test"
uit.mkdir(parents=True, exist_ok=True)

t0 = time.time()
pipe = vm.laad(lambda t: print(t, flush=True))
bron = None
if modus == "i2v":
    pipe = vm.i2v(pipe)
    foto = next((Path(__file__).parent / "uitvoer").glob("*/beeld_*.png"), None) or next(
        (Path(__file__).parent / "uitvoer").glob("*/*.png"), None
    )
    bron = Image.open(foto).convert("RGB").resize((breedte, hoogte), Image.LANCZOS)
    print("bronbeeld:", foto)
print(f"geladen in {time.time() - t0:.0f} s", flush=True)

frames = vm.frames(round(seconden * vm.fps) + 1)
prompt = "A fluffy white cat walks slowly across a sunny garden lawn, soft morning light, gentle breeze moving the grass, cinematic, shallow depth of field"
torch.cuda.reset_peak_memory_stats()
t1 = time.time()
beelden = vm.roep_aan(
    pipe, prompt=prompt, negatief="", breedte=breedte, hoogte=hoogte, frames=frames, stappen=stappen, seed=7, bron=bron,
    stap_klaar=lambda p, i, t, kw: (print(f"stap {i + 1}/{stappen} {time.time() - t1:.0f}s", flush=True), kw)[1],
)
duur = time.time() - t1
print(f"KLAAR {len(beelden)} beelden {breedte}x{hoogte}, {stappen} stappen, {duur:.0f} s ({duur / stappen:.1f} s/stap), "
      f"piek VRAM {torch.cuda.max_memory_allocated() / 1e9:.1f} GB")

pad = uit / f"{sleutel}_{modus}.mp4"
export_to_video(beelden, str(pad), fps=vm.fps)
blad = Image.new("RGB", (breedte * 3, hoogte))
for k, idx in enumerate((0, len(beelden) // 2, len(beelden) - 1)):
    b = beelden[idx]
    b = Image.fromarray((b * 255).astype('uint8') if b.dtype != 'uint8' else b) if hasattr(b, 'dtype') else b
    blad.paste(b.convert("RGB"), (k * breedte, 0))
blad.save(uit / f"{sleutel}_{modus}.png")
print("opgeslagen:", pad)
