from huggingface_hub import snapshot_download

# RealVisXL V5.0 (diffusers-formaat), alleen de fp16-onderdelen; zonder de losse checkpoint-bestanden.
pad = snapshot_download(
    "SG161222/RealVisXL_V5.0",
    ignore_patterns=[
        "RealVisXL_V5.0_*.safetensors",
        "*.md",
        "unet/diffusion_pytorch_model-0000*",
        "unet/diffusion_pytorch_model.safetensors*",
        "text_encoder/model.safetensors",
        "text_encoder_2/model.safetensors",
        "vae/diffusion_pytorch_model.safetensors",
        "*.png",
        "*.jpg",
    ],
)
print("KLAAR", pad)
