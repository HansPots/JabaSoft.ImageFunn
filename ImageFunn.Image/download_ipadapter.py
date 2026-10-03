from huggingface_hub import HfApi, snapshot_download

api = HfApi()
info = api.model_info("h94/IP-Adapter", files_metadata=True)
for s in info.siblings:
    if s.rfilename.startswith(("models/image_encoder", "sdxl_models/ip-adapter-plus_sdxl_vit-h")):
        print(f"{(s.size or 0)/1e9:5.2f} GB  {s.rfilename}", flush=True)

pad = snapshot_download(
    "h94/IP-Adapter",
    allow_patterns=[
        "sdxl_models/ip-adapter-plus_sdxl_vit-h.safetensors",
        "models/image_encoder/config.json",
        "models/image_encoder/model.safetensors",
    ],
)
print("KLAAR", pad)
