"""Builds AppIcon.ico and AppIcon.png.

Each size uses the artwork that reads best at it:
  48 px and up   large_source.png, the detailed rendered unit
  32 px          big_raw.png from make_icon.py, simpler and bolder
  16 to 24 px    small_raw.png from make_icon.py, with a heavier outline

    python make_icon.py .          # writes big_raw.png
    python make_icon.py . small    # writes small_raw.png
    python combine.py              # writes AppIcon.ico and AppIcon.png here

Copy the results to src/Kaze.App/Assets and tools/Kaze.Setup.
"""
from PIL import Image


def prepare_large(path, margin=0.05):
    """Drops the near invisible fringe left by background removal, then crops and centres in a square."""
    im = Image.open(path).convert("RGBA")
    alpha = im.getchannel("A").point(lambda a: 0 if a < 24 else a)
    im.putalpha(alpha)
    im = im.crop(alpha.getbbox())
    side = round(max(im.size) * (1 + 2 * margin))
    square = Image.new("RGBA", (side, side), (0, 0, 0, 0))
    square.alpha_composite(im, ((side - im.width) // 2, (side - im.height) // 2))
    return square


large = prepare_large("large_source.png")
big = Image.open("big_raw.png")
small = Image.open("small_raw.png")


def source(n):
    return small if n <= 24 else big if n <= 32 else large


sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
frames = {n: source(n).resize((n, n), Image.LANCZOS) for n in sizes}
frames[256].save("AppIcon.png")
frames[256].save("AppIcon.ico", sizes=[(n, n) for n in sizes], append_images=[frames[n] for n in sizes if n != 256])
