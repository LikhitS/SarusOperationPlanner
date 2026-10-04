"""Sarus top-menu icons from Tabler Icons (MIT, https://tabler.io/icons), rendered at the exact pixel
sizes of the upstream icons they override (light_* for dark themes, dark_* for light themes).
Upstream loads these files from the program folder when present, so no code changes are needed."""
import io
import json
import re
import urllib.request
from pathlib import Path

import resvg_py
from PIL import Image

HERE = Path(__file__).parent
OUT = HERE / "out" / "menuicons"
OUT.mkdir(parents=True, exist_ok=True)
SRC = HERE / "tabler"
SRC.mkdir(exist_ok=True)
pal = json.loads((HERE / "palette.json").read_text())

VERSION = "3.35.0"
# upstream name -> (tabler icon, upstream canvas size)
ICONS = {
    "flightdata_icon": ("gauge", (49, 45)),
    "flightplan_icon": ("route", (49, 45)),
    "initialsetup_icon": ("cpu", (52, 45)),
    "tuningconfig_icon": ("adjustments-horizontal", (52, 45)),
    "simulation_icon": ("drone", (64, 45)),
    "terminal_icon": ("terminal-2", (53, 45)),
    "help_icon": ("help-circle", (54, 45)),
    "connect_icon": ("plug-connected", (62, 45)),
    "disconnect_icon": ("plug-connected-x", (54, 45)),
}
VARIANTS = {
    # file prefix: (icon colour, accent for connect/disconnect, menu background)
    "light": (pal["dark"]["text"], pal["dark"]["accent"], pal["dark"]["surface"]),
    "dark": (pal["light"]["text"], pal["light"]["accent"], pal["light"]["surfaceRaised"]),
}


def fetch(name):
    f = SRC / f"{name}.svg"
    if not f.exists():
        url = f"https://unpkg.com/@tabler/icons@{VERSION}/icons/outline/{name}.svg"
        f.write_bytes(urllib.request.urlopen(url, timeout=30).read())
    return f.read_text()


def render(svg_text, color, px):
    svg = svg_text.replace('stroke="currentColor"', f'stroke="{color}"')
    svg = re.sub(r'stroke-width="[\d.]+"', 'stroke-width="1.75"', svg)
    tmp = SRC / "_tmp.svg"
    tmp.write_text(svg)
    return Image.open(io.BytesIO(bytes(resvg_py.svg_to_bytes(svg_path=str(tmp), width=px, height=px)))).convert("RGBA")


for prefix, (fg, accent, bg) in VARIANTS.items():
    for upstream, (tabler, (w, h)) in ICONS.items():
        color = accent if upstream.startswith(("connect", "disconnect")) else fg
        glyph = render(fetch(tabler), color, 36)
        canvas = Image.new("RGBA", (w, h), (0, 0, 0, 0))
        canvas.alpha_composite(glyph, ((w - glyph.width) // 2, (h - glyph.height) // 2))
        canvas.save(OUT / f"{prefix}_{upstream}.png")
    # tiled menu-bar background (upstream: light_icon_background.png / dark_icon_background.png)
    Image.new("RGB", (150, 150), bg).save(OUT / f"{prefix}_icon_background.png")

(SRC / "_tmp.svg").unlink(missing_ok=True)
(OUT / "TABLER-LICENSE.txt").write_bytes(
    urllib.request.urlopen(f"https://unpkg.com/@tabler/icons@{VERSION}/LICENSE", timeout=30).read())

# preview strips
for prefix, (fg, accent, bg) in VARIANTS.items():
    names = list(ICONS)
    strip = Image.new("RGBA", (sum(ICONS[n][1][0] for n in names) + 10 * len(names), 45), bg)
    x = 0
    for n in names:
        im = Image.open(OUT / f"{prefix}_{n}.png")
        strip.alpha_composite(im, (x, 0))
        x += im.width + 10
    strip.resize((strip.width * 2, 90), Image.LANCZOS).save(HERE / f"menuicons-preview-{prefix}.png")
print(sorted(p.name for p in OUT.iterdir()))
