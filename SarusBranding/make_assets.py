"""Generate Sarus Operation Planner app assets from sarus-logo-v2.svg and palette.json.

Outputs (in branding/out): mpdesktop.ico (16-256), mpdesktop150.png, mpdesktop44.png, splashdark.jpg (600x375),
sarus-icon-1024.png. File names match the upstream assets they replace, so no code changes are needed.
"""
import io
import json
from pathlib import Path

import resvg_py
from PIL import Image, ImageDraw, ImageFilter, ImageFont

HERE = Path(__file__).parent
OUT = HERE / "out"
OUT.mkdir(exist_ok=True)
pal = json.loads((HERE / "palette.json").read_text())
INK = pal["brand"]["ink"]
CRIMSON = pal["brand"]["crimson"]
BG = pal["dark"]["background"]
TEXT = pal["dark"]["text"]
MUTED = pal["dark"]["textMuted"]
SVG = HERE / "sarus-logo-v2.svg"


def hex_rgb(h):
    h = h.lstrip("#")
    return tuple(int(h[i:i + 2], 16) for i in (0, 2, 4))


def logo(width, color):
    """Logo rendered at `width`, recoloured to `color`, transparent background."""
    png = bytes(resvg_py.svg_to_bytes(svg_path=str(SVG), width=width))
    im = Image.open(io.BytesIO(png)).convert("RGBA")
    # coverage = darkness of the (black on white/transparent) artwork
    white = Image.alpha_composite(Image.new("RGBA", im.size, "white"), im).convert("L")
    alpha = white.point(lambda v: 255 - v)
    solid = Image.new("RGBA", im.size, hex_rgb(color) + (255,))
    solid.putalpha(alpha)
    return solid.crop(solid.getbbox())


def icon_master(size=1024):
    """White logo on an ink rounded square: readable on light and dark taskbars."""
    im = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    d.rounded_rectangle([0, 0, size - 1, size - 1], radius=int(size * 0.2), fill=hex_rgb(INK) + (255,))
    mark = logo(int(size * 0.86), "#FFFFFF")
    im.alpha_composite(mark, ((size - mark.width) // 2, (size - mark.height) // 2))
    return im


master = icon_master()
master.save(OUT / "sarus-icon-1024.png")
sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
master.save(OUT / "mpdesktop.ico", sizes=[(s, s) for s in sizes])
master.resize((150, 150), Image.LANCZOS).save(OUT / "mpdesktop150.png")
master.resize((44, 44), Image.LANCZOS).save(OUT / "mpdesktop44.png")

# Splash 600x375. Upstream overlays: TXT_version (403,107)-(558,132) white right-aligned,
# label1 (credit) right-aligned ending near x=560 at y=132. Keep those areas clear.
W, H = 600, 375
splash = Image.new("RGB", (W, H), hex_rgb(BG))
grad = Image.linear_gradient("L").rotate(90).resize((W, H))  # subtle left-to-right lift
splash = Image.composite(Image.new("RGB", (W, H), (30, 39, 49)), splash, grad.point(lambda v: v // 3))
# faint watermark crane, bottom-left (like upstream's faint plane)
wm = logo(520, "#FFFFFF")
wm.putalpha(wm.getchannel("A").point(lambda a: a * 10 // 255))
splash.paste(wm, (-90, 150), wm)

f_bold = ImageFont.truetype(r"C:\Windows\Fonts\segoeuib.ttf", 44)
f_reg = ImageFont.truetype(r"C:\Windows\Fonts\segoeui.ttf", 21)
mark = logo(118, TEXT)
d = ImageDraw.Draw(splash)
x_text = 300
splash.paste(mark, (x_text - mark.width - 16, 52), mark)
d.text((x_text, 40), "SARUS", font=f_bold, fill=hex_rgb(TEXT))
d.text((x_text + 2, 92), "Operation Planner", font=f_reg, fill=hex_rgb(MUTED))
d.rectangle([x_text + 2, 89, x_text + 60, 91], fill=hex_rgb(CRIMSON))
splash.save(OUT / "splashdark.jpg", quality=95)

for f in sorted(OUT.iterdir()):
    print(f.name, f.stat().st_size)

# Preview exactly as the Splash form shows it: 584x336 client, image centred, overlay labels at designer positions.
cw, ch = 584, 336
view = Image.new("RGB", (cw, ch))
view.paste(splash, ((cw - W) // 2, (ch - H) // 2))
dv = ImageDraw.Draw(view)
f_ver = ImageFont.truetype(r"C:\Windows\Fonts\micross.ttf", 11)
f_cred = ImageFont.truetype(r"C:\Windows\Fonts\micross.ttf", 11)
ver = "Version: 1.3.83"
tw = dv.textlength(ver, font=f_ver)
dv.text((403 + 155 - tw, 107 + 6), ver, font=f_ver, fill=(255, 255, 255))
dv.text((447, 132), "by Sarus Aerospace", font=f_cred, fill=hex_rgb(pal["dark"]["accent"]))
view.save(HERE / "splash-preview.png")

# Top-right menu logo (replaces upstream's 383x60 ArduPilot logo, shown at 200x31).
# Solid ink badge with white artwork: reads on both dark and light menu bars.
MW, MH = 383, 60
menu = Image.new("RGBA", (MW, MH), (0, 0, 0, 0))
ImageDraw.Draw(menu).rounded_rectangle([0, 0, MW - 1, MH - 1], radius=12, fill=hex_rgb(INK) + (255,))
crane = logo(92, "#FFFFFF")
f_menu = ImageFont.truetype(r"C:\Windows\Fonts\segoeuib.ttf", 42)
dm = ImageDraw.Draw(menu)
text_w = dm.textlength("SARUS", font=f_menu)
gap = 18
x0 = int((MW - (crane.width + gap + text_w)) / 2)
menu.alpha_composite(crane, (x0, (MH - crane.height) // 2))
tx = x0 + crane.width + gap
dm.text((tx, -3), "SARUS", font=f_menu, fill=(255, 255, 255, 255))
dm.rectangle([tx + 2, 46, tx + 56, 48], fill=hex_rgb(CRIMSON) + (255,))
menu = menu.crop((0, 0, MW, MH))
menu.save(OUT / "menu-logo.png")
# previews on dark and light bars, at on-screen size
for name, bg in (("dark", (34, 34, 34)), ("light", (240, 240, 240))):
    bar = Image.new("RGBA", (220, 40), bg + (255,))
    small = menu.resize((200, 31), Image.LANCZOS)
    bar.alpha_composite(small, (10, 4))
    bar.save(HERE / f"menu-logo-preview-{name}.png")
