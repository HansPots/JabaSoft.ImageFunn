# JabaSoft.ImageFunn

Onderdeel van de JabaSoft-familie, gebouwd op dezelfde WPF-stack en in dezelfde stijl
als AI Studio en Tab Studio. Twee kanten:

- **Beeld naar tekst** - een afbeelding laten beschrijven, uitlezen (OCR) of omzetten naar
  een prompt. Loopt via de AI-broker met het in Jabasoft ingestelde beeldmodel (Ollama/LM Studio).
- **Tekst naar beeld** - een beeld genereren uit een omschrijving. Loopt via de lokale
  Python-service in `ImageFunn.Image`. Standaard **RealVisXL V5.0** (fotorealistisch, een SDXL-model); het
  model is per opdracht te kiezen, en de standaard in Jabasoft > Instellingen. **JuggernautXL Ragnarok**
  wordt gevonden in de modelmap van LM Studio en de eerste keer omgezet (±1 minuut, daarna 5 GB gecachet in
  `ImageFunn.Image/modellen`). Verder `flux` (FLUX.1-schnell).
- **Video en film** - een beeld laten bewegen, een video uit tekst, of een hele film uit scènes. Videomodel
  **Wan 2.2 (5B)**: `wan` (30 stappen, beste kwaliteit) of `wan_snel` (de gedistilleerde FastWan van
  FastVideo, 3 stappen; alleen de transformer, ±10 GB, wordt de eerste keer gedownload).

## Starten

`ImageFunn.App` is een bibliotheek, geen eigen programma: ImageFunn draait als onderdeel van Jabasoft,
in hetzelfde venster (menuknop ImageFunn; de Home-knop brengt je terug). Starten gaat dus via
`D:\Repos\Jabasoft\Start-Jabasoft.cmd`. Los bouwen kan nog wel:

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

De eerste generatie met een model downloadt dat model naar de HuggingFace-cache. De uitvoer van de
service staat in `ImageFunn.Image/service.log`.
