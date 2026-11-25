"""Laadt een extra beeldmodel (zimage of fluxdev), maakt één beeld en stuurt het één keer bij; meet tijd en VRAM-piek.
Gebruik: .venv\\Scripts\\python.exe test_beeldmodel.py zimage|fluxdev [uitvoermap]
"""
import os
import sys
import time
from pathlib import Path

os.environ.setdefault("PYTORCH_CUDA_ALLOC_CONF", "expandable_segments:True")

import torch

import service_modellen as sm
import videomodellen

model = sys.argv[1]
uit = Path(sys.argv[2] if len(sys.argv) > 2 else ".")
uit.mkdir(parents=True, exist_ok=True)
inst = sm.INSTELLINGEN[model]

t = time.time()
pipe = sm.LADERS[model](print, videomodellen.revisie)
pipe.set_progress_bar_config(disable=True)
print(f"geladen in {time.time() - t:.0f} s")

torch.cuda.reset_peak_memory_stats()
t = time.time()
beeld = pipe(
    prompt="cinematic film still of a red sportscar on a wet city street at night, neon sign that says JABASOFT",
    width=1024, height=1024, num_inference_steps=inst["stappen"], guidance_scale=inst["guidance"],
    generator=torch.Generator("cpu").manual_seed(1),
).images[0]
beeld.save(uit / f"{model}_beeld.png")
print(f"beeld: {time.time() - t:.0f} s, piek {torch.cuda.max_memory_allocated() / 2**30:.1f} GB")

bewerk = sm.bewerk_pijplijn(model, pipe, inpaint=False)
bewerk.set_progress_bar_config(disable=True)
torch.cuda.reset_peak_memory_stats()
t = time.time()
beeld2 = bewerk(
    prompt="cinematic film still of a blue sportscar on a wet city street at night, neon sign that says JABASOFT",
    image=beeld, strength=0.5, width=1024, height=1024, num_inference_steps=inst["stappen"],
    guidance_scale=inst["guidance"], generator=torch.Generator("cpu").manual_seed(2),
).images[0]
beeld2.save(uit / f"{model}_bijgestuurd.png")
print(f"bijsturen: {time.time() - t:.0f} s, piek {torch.cuda.max_memory_allocated() / 2**30:.1f} GB")
