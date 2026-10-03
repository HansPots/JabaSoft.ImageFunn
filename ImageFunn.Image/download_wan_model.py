from huggingface_hub import snapshot_download

# Wan 2.2 TI2V-5B (diffusers-formaat): tekst naar video en beeld naar video in één model.
pad = snapshot_download("Wan-AI/Wan2.2-TI2V-5B-Diffusers", ignore_patterns=["*.md", "*.gif", "assets/*"])
print("KLAAR", pad)
