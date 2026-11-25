"""
Een beeld 2× vergroten met Real-ESRGAN x2 (ai-forever/Real-ESRGAN, RealESRGAN_x2.pth, ±67 MB, BSD-3).

Het netwerk (RRDBNet, zoals in basicsr) staat hier zelf beschreven, zodat er geen extra pakket nodig is. Het werkt in
tegels met overlap: zo blijft het geheugen klein, ook bij grote beelden, en vallen er geen naden op.
"""

from __future__ import annotations

from typing import Callable

REPO = "ai-forever/Real-ESRGAN"
BESTAND = "RealESRGAN_x2.pth"

_net = None


def _bouw_net():
    import torch
    from torch import nn
    from torch.nn import functional as F

    class RDB(nn.Module):
        def __init__(self, f=64, g=32):
            super().__init__()
            self.conv1 = nn.Conv2d(f, g, 3, 1, 1)
            self.conv2 = nn.Conv2d(f + g, g, 3, 1, 1)
            self.conv3 = nn.Conv2d(f + 2 * g, g, 3, 1, 1)
            self.conv4 = nn.Conv2d(f + 3 * g, g, 3, 1, 1)
            self.conv5 = nn.Conv2d(f + 4 * g, f, 3, 1, 1)
            self.lrelu = nn.LeakyReLU(0.2, True)

        def forward(self, x):
            x1 = self.lrelu(self.conv1(x))
            x2 = self.lrelu(self.conv2(torch.cat((x, x1), 1)))
            x3 = self.lrelu(self.conv3(torch.cat((x, x1, x2), 1)))
            x4 = self.lrelu(self.conv4(torch.cat((x, x1, x2, x3), 1)))
            x5 = self.conv5(torch.cat((x, x1, x2, x3, x4), 1))
            return x5 * 0.2 + x

    class RRDB(nn.Module):
        def __init__(self, f=64):
            super().__init__()
            self.rdb1, self.rdb2, self.rdb3 = RDB(f), RDB(f), RDB(f)

        def forward(self, x):
            return self.rdb3(self.rdb2(self.rdb1(x))) * 0.2 + x

    class RRDBNet(nn.Module):
        """Real-ESRGAN x2: eerst pixel-unshuffle (beeld 2× kleiner, 4× zoveel kanalen), dan 4× omhoog = netto 2×."""

        def __init__(self, f=64, blokken=23):
            super().__init__()
            self.conv_first = nn.Conv2d(12, f, 3, 1, 1)
            self.body = nn.Sequential(*[RRDB(f) for _ in range(blokken)])
            self.conv_body = nn.Conv2d(f, f, 3, 1, 1)
            self.conv_up1 = nn.Conv2d(f, f, 3, 1, 1)
            self.conv_up2 = nn.Conv2d(f, f, 3, 1, 1)
            self.conv_hr = nn.Conv2d(f, f, 3, 1, 1)
            self.conv_last = nn.Conv2d(f, 3, 3, 1, 1)
            self.lrelu = nn.LeakyReLU(0.2, True)

        def forward(self, x):
            feat = self.conv_first(F.pixel_unshuffle(x, 2))
            feat = feat + self.conv_body(self.body(feat))
            feat = self.lrelu(self.conv_up1(F.interpolate(feat, scale_factor=2, mode="nearest")))
            feat = self.lrelu(self.conv_up2(F.interpolate(feat, scale_factor=2, mode="nearest")))
            return self.conv_last(self.lrelu(self.conv_hr(feat)))

    return RRDBNet()


def laad(revisie: Callable[[str], str | None]):
    """Het netwerk op de GPU in fp16; één keer laden, daarna blijft het (klein: ±70 MB)."""
    global _net
    if _net is None:
        import torch
        from huggingface_hub import hf_hub_download

        pad = hf_hub_download(REPO, BESTAND, revision=revisie(REPO))
        sd = torch.load(pad, map_location="cpu", weights_only=True)
        sd = sd.get("params_ema", sd.get("params", sd))
        net = _bouw_net()
        net.load_state_dict(sd, strict=True)
        _net = net.eval().half().to("cuda")
    return _net


def vergroot(beeld, revisie: Callable[[str], str | None], tegel: int = 512, rand: int = 16, voortgang=None):
    """Geeft een PIL-beeld terug dat 2× zo breed en hoog is. tegel = grootte van een tegel in invoerpixels."""
    import numpy as np
    import torch
    from PIL import Image

    net = laad(revisie)
    rgb = np.asarray(beeld.convert("RGB"), dtype=np.float32) / 255.0
    x = torch.from_numpy(rgb).permute(2, 0, 1).unsqueeze(0).half().to("cuda")
    _, _, h, w = x.shape
    # Pixel-unshuffle vraagt even afmetingen: zo nodig één pixel spiegelen en het teveel straks afknippen.
    ph, pw = h % 2, w % 2
    if ph or pw:
        x = torch.nn.functional.pad(x, (0, pw, 0, ph), mode="reflect")
    H, W = x.shape[2], x.shape[3]
    uit = torch.zeros((1, 3, H * 2, W * 2), dtype=torch.float16, device="cuda")
    tegels = [(y, xx) for y in range(0, H, tegel) for xx in range(0, W, tegel)]
    with torch.inference_mode():
        for n, (y0, x0) in enumerate(tegels):
            y1, x1 = min(y0 + tegel, H), min(x0 + tegel, W)
            # Tegel met een rand eromheen rekenen (even getallen), en alleen het midden bewaren: geen naden.
            ya, xa = max(y0 - rand, 0), max(x0 - rand, 0)
            yb, xb = min(y1 + rand, H), min(x1 + rand, W)
            stuk = net(x[:, :, ya:yb, xa:xb])
            uit[:, :, y0 * 2:y1 * 2, x0 * 2:x1 * 2] = stuk[:, :, (y0 - ya) * 2:(y1 - ya) * 2, (x0 - xa) * 2:(x1 - xa) * 2]
            if voortgang:
                voortgang((n + 1) / len(tegels))
    uit = uit[:, :, : h * 2, : w * 2].clamp(0, 1)
    arr = (uit[0].permute(1, 2, 0).float().cpu().numpy() * 255.0).round().astype(np.uint8)
    return Image.fromarray(arr)
