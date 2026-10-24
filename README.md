# JabaSoft.ImageFunn

Onderdeel van de JabaSoft-familie, gebouwd op dezelfde WPF-stack en in dezelfde stijl
als AI Studio en Tab Studio. Twee kanten:

- **Beeld naar tekst** - een afbeelding laten beschrijven, uitlezen (OCR) of omzetten naar
  een prompt. Loopt via de AI-broker met het in Jabasoft ingestelde beeldmodel (Ollama/LM Studio).
- **Tekst naar beeld** - een beeld genereren uit een omschrijving. Loopt via de lokale
  Python-service in `ImageFunn.Image`. Standaard **JuggernautXL Ragnarok** (zeer fotorealistisch, een
  SDXL-model). Het GGUF-bestand wordt gevonden in de modelmap van LM Studio en de eerste keer omgezet
  (±1 minuut, daarna 5 GB gecachet in `ImageFunn.Image/modellen`). Ander model? Zet `IMAGEFUNN_MODEL`
  op `sdxl` (het gewone SDXL-basismodel) of `flux`.

## Starten

`ImageFunn.App` is een bibliotheek, geen eigen programma: ImageFunn draait als onderdeel van Jabasoft,
in hetzelfde venster (menuknop ImageFunn; de Home-knop brengt je terug). Starten gaat dus via
`D:\Repos\Jabasoft.TheApp\Start-Jabasoft.cmd`. Los bouwen kan nog wel:

```powershell
dotnet build JabaSoft.ImageFunn.slnx
```

Bij het openen van ImageFunn wordt de beeldservice gestart (poort 5330) zodra de venv bestaat.

## Python-omgeving (eenmalig)

```powershell
cd ImageFunn.Image
py -3.11 -m venv .venv
.venv\Scripts\python.exe -m pip install torch torchvision --index-url https://download.pytorch.org/whl/cu124
.venv\Scripts\python.exe -m pip install -r requirements.txt
```

De eerste generatie downloadt het model (±33 GB, naar de HuggingFace-cache). Liever SDXL
(kleiner, ±7 GB)? Zet de omgevingsvariabele `IMAGEFUNN_MODEL=sdxl`.
