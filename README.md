# JabaSoft.ImageFunn

Onderdeel van de JabaSoft-familie, gebouwd op dezelfde WPF-stack en in dezelfde stijl
als AI Studio en Tab Studio. Twee kanten:

- **Beeld naar tekst** - een afbeelding laten beschrijven, uitlezen (OCR) of omzetten naar
  een prompt. Loopt via de AI-broker met het in Jabasoft ingestelde beeldmodel (Ollama/LM Studio).
- **Tekst naar beeld** - een beeld genereren uit een omschrijving. Loopt via de lokale
  Python-service in `ImageFunn.Image` (FLUX.1-schnell in 4-bit, past op 12 GB VRAM).

## Starten

```powershell
dotnet build JabaSoft.ImageFunn.slnx
dotnet run --project ImageFunn.App
```

`ImageFunn.App` start de beeldservice zelf (poort 5330) zodra de venv bestaat.

## Python-omgeving (eenmalig)

```powershell
cd ImageFunn.Image
py -3.11 -m venv .venv
.venv\Scripts\python.exe -m pip install torch torchvision --index-url https://download.pytorch.org/whl/cu124
.venv\Scripts\python.exe -m pip install -r requirements.txt
```

De eerste generatie downloadt het model (±33 GB, naar de HuggingFace-cache). Liever SDXL
(kleiner, ±7 GB)? Zet de omgevingsvariabele `IMAGEFUNN_MODEL=sdxl`.
