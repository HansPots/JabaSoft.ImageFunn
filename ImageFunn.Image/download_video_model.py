from huggingface_hub import snapshot_download

# LTX-Video 0.9.1 (diffusers-formaat), zonder de voorbeeld-gifs in media/.
pad = snapshot_download(
    "Lightricks/LTX-Video-0.9.1",
    ignore_patterns=["media/*", "*.md", "ltx-video-2b-v0.9.1.safetensors", "*.gif"],
)
print("KLAAR", pad)
