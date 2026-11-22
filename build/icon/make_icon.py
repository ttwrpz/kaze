"""Draws the small sizes of the Kaze app icon (see combine.py for the steps)."""
import math, sys
from PIL import Image, ImageDraw, ImageFilter, ImageChops

K = 4                      # supersampling
SMALL = len(sys.argv) > 2 and sys.argv[2] == "small"
N = 256 * K

def lerp(a, b, t): return tuple(int(a[i] + (b[i] - a[i]) * t) for i in range(len(a)))

def vgrad(w, h, top, bottom, y0=0, y1=None):
    y1 = h if y1 is None else y1
    g = Image.new("RGBA", (w, h))
    px = g.load()
    for y in range(h):
        c = lerp(top, bottom, min(1, max(0, (y - y0) / max(1, y1 - y0))))
        for x in range(w): px[x, y] = c
    return g

def diag(w, h, a, b):
    g = Image.new("RGBA", (w, h)); px = g.load()
    for y in range(h):
        for x in range(w):
            px[x, y] = lerp(a, b, (x + y) / (w + h))
    return g

def s(v): return int(v * K)

img = Image.new("RGBA", (N, N), (0, 0, 0, 0))

bx0, by0, bx1, by1, br = 20, 52, 236, 142, 30

sh = Image.new("L", (N, N), 0)
ImageDraw.Draw(sh).rounded_rectangle([s(bx0 + 4), s(by0 + 10), s(bx1 - 4), s(by1 + 8)], s(br), fill=90)
sh = sh.filter(ImageFilter.GaussianBlur(s(7)))
img.paste(Image.new("RGBA", (N, N), (20, 40, 70, 255)), (0, 0), sh)

mask = Image.new("L", (N, N), 0)
ImageDraw.Draw(mask).rounded_rectangle([s(bx0), s(by0), s(bx1), s(by1)], s(br), fill=255)
body = vgrad(N, N, (255, 255, 255, 255), (214, 222, 232, 255), s(by0), s(by1))
img.paste(body, (0, 0), mask)

# Edge stroke for definition on light backgrounds
edge = Image.new("L", (N, N), 0)
d = ImageDraw.Draw(edge)
d.rounded_rectangle([s(bx0), s(by0), s(bx1), s(by1)], s(br), fill=255)
inner = Image.new("L", (N, N), 0)
e = 9 if SMALL else 1.5
ImageDraw.Draw(inner).rounded_rectangle([s(bx0 + e), s(by0 + e), s(bx1 - e), s(by1 - e)], s(br - e), fill=255)
edge = ImageChops.subtract(edge, inner)
img.paste(Image.new("RGBA", (N, N), (120, 136, 156, 255)), (0, 0), edge.point(lambda v: v * (0.9 if SMALL else 0.55)))

hl = Image.new("L", (N, N), 0)
ImageDraw.Draw(hl).rounded_rectangle([s(bx0 + 10), s(by0 + 5), s(bx1 - 10), s(by0 + 22)], s(9), fill=110)
hl = hl.filter(ImageFilter.GaussianBlur(s(2)))
img.paste(Image.new("RGBA", (N, N), (255, 255, 255, 255)), (0, 0), hl)

slot = Image.new("L", (N, N), 0)
ImageDraw.Draw(slot).rounded_rectangle([s(44), s(108 if SMALL else 114), s(212), s(127)], s(9 if SMALL else 6.5), fill=255)
img.paste(vgrad(N, N, (40, 56, 76, 255), (78, 96, 118, 255), s(114), s(127)), (0, 0), slot)

led = Image.new("L", (N, N), 0)
ImageDraw.Draw(led).ellipse([s(198), s(80), s(210), s(92)], fill=0 if SMALL else 255)
glow = led.filter(ImageFilter.GaussianBlur(s(4)))
img.paste(Image.new("RGBA", (N, N), (76, 194, 255, 255)), (0, 0), glow.point(lambda v: v * 0.6))
img.paste(Image.new("RGBA", (N, N), (60, 180, 255, 255)), (0, 0), led)

# Cool airflow: horizontal waves under the unit, deep blue on the left to cyan on the right,
# matching the ribbons in the large artwork.
flow = Image.new("L", (N, N), 0)
fd = ImageDraw.Draw(flow)
w = 26 if SMALL else 15
rows = (172, 214) if SMALL else (162, 192, 222)
for cy in rows:
    pts = []
    for i in range(81):
        t = i / 80
        x = 62 + t * 132
        y = cy - (10 if SMALL else 8) * math.sin(t * 2 * math.pi)
        pts.append((s(x), s(y)))
    fd.line(pts, fill=255, width=s(w), joint="curve")
    for p in (pts[0], pts[-1]):
        fd.ellipse([p[0] - s(w / 2), p[1] - s(w / 2), p[0] + s(w / 2), p[1] + s(w / 2)], fill=255)
fsh = flow.filter(ImageFilter.GaussianBlur(s(4))).point(lambda v: v * 0.35)
img.paste(Image.new("RGBA", (N, N), (10, 60, 120, 255)), (0, s(3)), fsh)
hg = Image.new("RGBA", (N, N)); hp = hg.load()
for x in range(N):
    c = lerp((20, 110, 220, 255), (70, 210, 255, 255), min(1, max(0, (x - s(62)) / s(132))))
    for y in range(N): hp[x, y] = c
img.paste(hg, (0, 0), flow)

out = sys.argv[1]
img.save(out + ("/small_raw.png" if SMALL else "/big_raw.png"))
