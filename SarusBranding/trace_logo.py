"""Trace the Sarus reference logo into a clean single-colour SVG."""
from pathlib import Path

import vtracer
from PIL import Image, ImageFilter

HERE = Path(__file__).parent
src = Image.open(HERE / "sarus-logo-reference.webp").convert("L")

# Upscale first so the traced curves follow the shape, not the pixels.
big = src.resize((src.width * 4, src.height * 4), Image.LANCZOS)
big = big.filter(ImageFilter.GaussianBlur(2))
bw = big.point(lambda v: 0 if v < 128 else 255).convert("RGB")

# Crop to the artwork with a small margin.
bbox = Image.eval(bw.convert("L"), lambda v: 255 - v).getbbox()
pad = 40
bw = bw.crop((bbox[0] - pad, bbox[1] - pad, bbox[2] + pad, bbox[3] + pad))
bw.save(HERE / "_trace_input.png")

vtracer.convert_image_to_svg_py(
    str(HERE / "_trace_input.png"),
    str(HERE / "sarus-logo.svg"),
    colormode="binary",
    mode="spline",
    filter_speckle=8,
    corner_threshold=60,
    length_threshold=4.0,
    splice_threshold=45,
    path_precision=3,
)
print("size", bw.size)
