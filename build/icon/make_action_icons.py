"""Renders the jump list action icons (see actions/README.md) into src/Kaze.App/Assets/Actions.

Each Fluent icon is drawn in one colour that reads on both the light and the dark Start menu, at
the sizes Windows asks for. Run from the repository root: python build/icon/make_action_icons.py
"""
import os
import re
from PIL import Image, ImageChops, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "..", "..", "src", "Kaze.App", "Assets", "Actions")
K = 32  # supersampling: the 20 px design is drawn at 640 px

ICONS = {
    "power-on": ("power", (47, 170, 79)),
    "power-off": ("power", (138, 138, 138)),
    "sleep": ("weather_moon", (126, 107, 214)),
    "panel": ("options", (45, 140, 224)),
    "cool": ("snowflake", (45, 140, 224)),
    "heat": ("weather_sunny", (242, 113, 28)),
    "dry": ("drop", (26, 166, 196)),
    "fan": ("wind", (107, 175, 61)),
    "auto": ("arrow_sync", (176, 92, 200)),
    "preset": ("star", (232, 163, 23)),
}
SIZES = [16, 20, 24, 32, 40, 48, 64]


def subpaths(d):
    """Flattens absolute M/L/H/V/C/Z path data into closed polygons."""
    tokens = re.findall(r"[MLHVCZ]|-?\d*\.?\d+(?:e-?\d+)?", d)
    polys, cur, i, x, y, cmd = [], [], 0, 0.0, 0.0, None

    def num():
        nonlocal i
        i += 1
        return float(tokens[i - 1])

    while i < len(tokens):
        if tokens[i] in "MLHVCZ":
            cmd = tokens[i]
            i += 1
            if cmd == "Z":
                if cur: polys.append(cur)
                cur = []
                continue
        if cmd == "M":
            if cur: polys.append(cur)
            x, y = num(), num(); cur = [(x, y)]; cmd = "L"
        elif cmd == "L":
            x, y = num(), num(); cur.append((x, y))
        elif cmd == "H":
            x = num(); cur.append((x, y))
        elif cmd == "V":
            y = num(); cur.append((x, y))
        elif cmd == "C":
            x1, y1, x2, y2, x3, y3 = (num() for _ in range(6))
            for s in range(1, 17):
                t = s / 16
                a, b, c, e = (1 - t) ** 3, 3 * (1 - t) ** 2 * t, 3 * (1 - t) * t * t, t ** 3
                cur.append((a * x + b * x1 + c * x2 + e * x3, a * y + b * y1 + c * y2 + e * y3))
            x, y = x3, y3
    if cur: polys.append(cur)
    return polys


def render(svg_path, colour):
    d = " ".join(re.findall(r' d="([^"]+)"', open(svg_path, encoding="utf-8").read()))
    n = 20 * K
    mask = Image.new("L", (n, n), 0)
    for poly in subpaths(d):
        layer = Image.new("L", (n, n), 0)
        if len(poly) > 2:
            ImageDraw.Draw(layer).polygon([(px * K, py * K) for px, py in poly], fill=255)
        mask = ImageChops.logical_xor(mask.convert("1"), layer.convert("1")).convert("L")  # even-odd
    img = Image.new("RGBA", (n, n), colour + (255,))
    img.putalpha(mask)
    return img


os.makedirs(OUT, exist_ok=True)
for name, (source, colour) in ICONS.items():
    big = render(os.path.join(HERE, "actions", source + ".svg"), colour)
    frames = [big.resize((s, s), Image.LANCZOS) for s in SIZES]
    frames[-1].save(os.path.join(OUT, name + ".ico"), sizes=[(s, s) for s in SIZES], append_images=frames[:-1])
print("wrote", len(ICONS), "icons to", os.path.normpath(OUT))
