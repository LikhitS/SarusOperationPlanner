"""Generate SarusDark.mpsystheme and SarusLight.mpsystheme (upstream ThemeColorTable XML format).
HUD colours are copied unchanged from upstream's default BurntKermit theme: pilots rely on them."""
import json
from pathlib import Path
from xml.sax.saxutils import quoteattr

HERE = Path(__file__).parent
OUT = HERE / "out"
pal = json.loads((HERE / "palette.json").read_text())
d, l, s = pal["dark"], pal["light"], pal["status"]
g = pal["glass"]

HUD = [  # identical to BurntKermit.mpsystheme
    ("HUD text and drawings", "LightGray", None, "HudText"),
    ("HUD Ground top", "#9bb824", None, "HudGroundTop"),
    ("HUD Ground bottom", "#414f07", None, "HudGroundBot"),
    ("HUD Sky top", "Blue", None, "HudSkyTop"),
    ("HUD Sky bottom", "LightBlue", None, "HudSkyBot"),
]

THEMES = {
    "SarusDark": ("BurnKermitIconSet", [
        ("Background", d["background"], None, "BGColor"),
        ("Control Background", d["surface"], None, "ControlBGColor"),
        ("Text", d["text"], None, "TextColor"),
        ("TextBox Background", d["surfaceRaised"], None, "BGColorTextBox"),
        ("Button Text", d["accentText"], None, "ButtonTextColor"),
        ("Button Background top", d["accent"], None, "ButBG"),
        ("Button Background bottom", d["accentHover"], None, "ButBGBot"),
        ("ProgressBar Top", pal["brand"]["sky"], None, "ProgressBarColorTop"),
        ("ProgressBar Bottom", d["accent"], None, "ProgressBarColorBot"),
        ("ProgressBar Outline", d["accentHover"], None, "ProgressBarOutlineColor"),
        ("BannerColor1", l["accentHover"], None, "BannerColor1"),
        ("BannerColor2", d["accent"], None, "BannerColor2"),
        ("Disabled Button", d["border"], 150, "ColorNotEnabled"),
        ("Button Mouseover", d["accentText"], 73, "ColorMouseOver"),
        ("Button Mousedown", d["accentText"], 73, "ColorMouseDown"),
        ("CurrentPPM Background", s["ok"], None, "CurrentPPMBackground"),
        ("Graph Chart Fill", "#141A21", None, "ZedGraphChartFill"),
        ("Graph Pane Fill", d["surface"], None, "ZedGraphPaneFill"),
        ("Graph Legend Fill", "#7C8A98", None, "ZedGraphLegendFill"),
        ("Rich Text Box text", d["text"], None, "RTBForeColor"),
        ("BackStageView Button Area", "#0B0F13", None, "BSVButtonAreaBGColor"),
        ("BSV Unselected Text", "#C4CDD6", None, "UnselectedTextColour"),
        ("Horizontal ProgressBar", d["accent"], None, "HorizontalPBValueColor"),
    ]),
    "SarusLight": ("HighContrastIconSet", [
        ("Background", l["surfaceRaised"], None, "BGColor"),
        ("Control Background", l["background"], None, "ControlBGColor"),
        ("Text", l["text"], None, "TextColor"),
        ("TextBox Background", l["surface"], None, "BGColorTextBox"),
        ("Button Text", l["accentText"], None, "ButtonTextColor"),
        ("Button Background top", l["accent"], None, "ButBG"),
        ("Button Background bottom", pal["brand"]["sky"], None, "ButBGBot"),
        ("ProgressBar Top", l["accent"], None, "ProgressBarColorTop"),
        ("ProgressBar Bottom", pal["brand"]["sky"], None, "ProgressBarColorBot"),
        ("ProgressBar Outline", l["accentHover"], None, "ProgressBarOutlineColor"),
        ("BannerColor1", l["accentHover"], None, "BannerColor1"),
        ("BannerColor2", pal["brand"]["sky"], None, "BannerColor2"),
        ("Disabled Button", l["border"], 150, "ColorNotEnabled"),
        ("Button Mouseover", l["text"], 40, "ColorMouseOver"),
        ("Button Mousedown", l["text"], 60, "ColorMouseDown"),
        ("CurrentPPM Background", s["ok"], None, "CurrentPPMBackground"),
        ("Graph Chart Fill", l["surface"], None, "ZedGraphChartFill"),
        ("Graph Pane Fill", l["background"], None, "ZedGraphPaneFill"),
        ("Graph Legend Fill", l["border"], None, "ZedGraphLegendFill"),
        ("Rich Text Box text", l["text"], None, "RTBForeColor"),
        ("BackStageView Button Area", "#DDE3E9", None, "BSVButtonAreaBGColor"),
        ("BSV Unselected Text", l["textMuted"], None, "UnselectedTextColour"),
        ("Horizontal ProgressBar", l["accent"], None, "HorizontalPBValueColor"),
    ]),
    # opt-in theme; SarusGlass.cs adds the frosted menu bar, the acrylic window frame and the raised buttons
    "SarusGlass": ("BurnKermitIconSet", [
        ("Background", g["background"], None, "BGColor"),
        ("Control Background", g["surface"], None, "ControlBGColor"),
        ("Text", g["text"], None, "TextColor"),
        ("TextBox Background", g["surfaceRaised"], None, "BGColorTextBox"),
        ("Button Text", g["text"], None, "ButtonTextColor"),
        ("Button Background top", g["button"], None, "ButBG"),
        ("Button Background bottom", g["button"], None, "ButBGBot"),
        ("ProgressBar Top", g["accent"], None, "ProgressBarColorTop"),
        ("ProgressBar Bottom", g["accent"], None, "ProgressBarColorBot"),
        ("ProgressBar Outline", g["accentHover"], None, "ProgressBarOutlineColor"),
        ("BannerColor1", g["surfaceRaised"], None, "BannerColor1"),
        ("BannerColor2", g["accent"], None, "BannerColor2"),
        ("Disabled Button", g["background"], 140, "ColorNotEnabled"),
        ("Button Mouseover", "#FFFFFF", 22, "ColorMouseOver"),
        ("Button Mousedown", "#000000", 60, "ColorMouseDown"),
        ("CurrentPPM Background", s["ok"], None, "CurrentPPMBackground"),
        ("Graph Chart Fill", g["background"], None, "ZedGraphChartFill"),
        ("Graph Pane Fill", g["surface"], None, "ZedGraphPaneFill"),
        ("Graph Legend Fill", g["border"], None, "ZedGraphLegendFill"),
        ("Rich Text Box text", g["text"], None, "RTBForeColor"),
        ("BackStageView Button Area", g["panelEdge"], None, "BSVButtonAreaBGColor"),
        ("BSV Unselected Text", g["textMuted"], None, "UnselectedTextColour"),
        ("Horizontal ProgressBar", g["accent"], None, "HorizontalPBValueColor"),
    ]),
}

for name, (iconset, colors) in THEMES.items():
    rows = []
    for item, web, alpha, var in colors + HUD:
        a = f" Alpha={quoteattr(str(alpha))}" if alpha is not None else ""
        rows.append(f"    <ThemeColor>\n      <strColorItemName>{item}</strColorItemName>\n"
                    f"      <clrColor Web={quoteattr(web)}{a} />\n      <strVariableName>{var}</strVariableName>\n    </ThemeColor>")
    xml = ('<?xml version="1.0" encoding="utf-8"?>\n'
           '<ThemeColorTable xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">\n'
           "  <colors>\n" + "\n".join(rows) + "\n  </colors>\n"
           f"  <iconSet>{iconset}</iconSet>\n  <terminalTheming>true</terminalTheming>\n</ThemeColorTable>\n")
    (OUT / f"{name}.mpsystheme").write_text(xml, encoding="utf-8")
    print(name, len(colors) + len(HUD), "colours")
