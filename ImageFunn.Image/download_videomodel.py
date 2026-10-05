"""Haalt de gewichten van een videomodel op (zie videomodellen.py), zodat het eerste gebruik niet hoeft te wachten.
Gebruik: .venv\Scripts\python.exe download_videomodel.py <sleutel>
"""
import sys

from huggingface_hub import snapshot_download

import videomodellen

vm = videomodellen.kies(sys.argv[1])
for repo, patronen in vm.repos:
    pad = snapshot_download(
        repo,
        allow_patterns=list(patronen) or None,
        ignore_patterns=["*.md", "*.gif", "*.png", "*.jpg", "*.mp4"],
    )
    print("KLAAR", repo, pad, flush=True)
