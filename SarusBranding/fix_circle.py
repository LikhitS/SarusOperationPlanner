"""Replace the traced (slightly wobbly) ring of the Sarus logo with a geometrically perfect one.

Rasterise the traced SVG, fit a circle to the ring where the bird does not cover it, rebuild the ring band
from (bird pixels that survive a morphological opening) + (a perfect annulus), then re-trace to SVG.
"""
from pathlib import Path

import numpy as np
import resvg_py
import vtracer
from PIL import Image
from scipy import ndimage

HERE = Path(__file__).parent
W = 1600

png = bytes(resvg_py.svg_to_bytes(svg_path=str(HERE / "sarus-logo.svg"), width=W))
(HERE / "_big.png").write_bytes(png)
img = Image.open(HERE / "_big.png").convert("RGBA")
gray = np.array(Image.alpha_composite(Image.new("RGBA", img.size, "white"), img).convert("L"))
ink = gray < 128
H, Wd = ink.shape
yy, xx = np.nonzero(ink)

# Iterative least-squares circle fit on ring pixels not covered by the bird.
cx, cy, r = Wd / 2, H * 0.48, H * 0.42
for _ in range(8):
    d = np.hypot(xx - cx, yy - cy)
    sel = (np.abs(d - r) < max(6, r * 0.05)) & (
        (yy > cy + r * 0.35) | ((yy < cy - r * 0.2) & (np.abs(xx - cx) > r * 0.25)))
    x, y = xx[sel].astype(float), yy[sel].astype(float)
    sol, *_ = np.linalg.lstsq(np.c_[2 * x, 2 * y, np.ones_like(x)], x ** 2 + y ** 2, rcond=None)
    cx, cy = sol[0], sol[1]
    r = np.sqrt(sol[2] + cx ** 2 + cy ** 2)

d_all = np.hypot(xx - cx, yy - cy)
radial = d_all[(np.abs(d_all - r) < r * 0.06) & (yy > cy + r * 0.35)]
r_in, r_out = np.percentile(radial, 2), np.percentile(radial, 98)
thick = r_out - r_in
print(f"centre=({cx:.1f},{cy:.1f}) r={r:.1f} ring thickness {thick:.1f}px")

# Bird mask: opening with a disc wider than the ring removes the ring but keeps the (thicker) bird.
rad = int(np.ceil(thick)) + 1
disc = np.hypot(*np.mgrid[-rad:rad + 1, -rad:rad + 1]) <= rad
bird = ndimage.binary_opening(ink, structure=disc)

Y, X = np.mgrid[0:H, 0:Wd]
D = np.hypot(X - cx, Y - cy)
mid, half = (r_in + r_out) / 2, thick / 2
ring = np.abs(D - mid) <= half
band = np.abs(D - mid) <= half + thick * 1.5

out = ink.copy()
out[band] = bird[band] | ring[band]

Image.fromarray(np.where(out, 0, 255).astype(np.uint8)).convert("RGB").save(HERE / "_fixed.png")
vtracer.convert_image_to_svg_py(str(HERE / "_fixed.png"), str(HERE / "sarus-logo-v2.svg"),
                                colormode="binary", mode="spline", filter_speckle=12,
                                corner_threshold=60, length_threshold=4.0, splice_threshold=45, path_precision=2)
(HERE / "sarus-logo-v2-preview.png").write_bytes(
    bytes(resvg_py.svg_to_bytes(svg_path=str(HERE / "sarus-logo-v2.svg"), width=1000)))
for f in ("_big.png", "_fixed.png"):
    (HERE / f).unlink()
print("ok")
