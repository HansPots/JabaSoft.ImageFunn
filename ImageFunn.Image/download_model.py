from huggingface_hub import snapshot_download

# SDXL base in fp16, alleen wat diffusers nodig heeft (geen onnx/openvino, geen losse root-checkpoints).
pad = snapshot_download(
    "stabilityai/stable-diffusion-xl-base-1.0",
    allow_patterns=[
        "model_index.json",
        "scheduler/*", "tokenizer/*", "tokenizer_2/*",
        "text_encoder/config.json", "text_encoder/model.fp16.safetensors",
        "text_encoder_2/config.json", "text_encoder_2/model.fp16.safetensors",
        "unet/config.json", "unet/diffusion_pytorch_model.fp16.safetensors",
        "vae/config.json", "vae/diffusion_pytorch_model.fp16.safetensors",
    ],
)
print("KLAAR", pad)
