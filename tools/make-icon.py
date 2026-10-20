# Draws Lilt's wave mark and writes Assets/lilt.ico (16-256 px) and Assets/lilt-512.png. Needs Pillow.
import sys
from PIL import Image, ImageDraw

def cubic(p0, p1, p2, p3, n=200):
    for i in range(n + 1):
        t = i / n
        u = 1 - t
        yield (u**3 * p0[0] + 3 * u * u * t * p1[0] + 3 * u * t * t * p2[0] + t**3 * p3[0],
               u**3 * p0[1] + 3 * u * u * t * p1[1] + 3 * u * t * t * p2[1] + t**3 * p3[1])

def wave(dx, dy):
    # Two cubic segments, a soft "~" rising left to right, in a 64x64 box.
    a = [(8, 40), (14, 30), (22, 26), (30, 32)]
    b = [(30, 32), (38, 38), (46, 34), (52, 24)]
    pts = list(cubic(*a)) + list(cubic(*b))
    return [(x + dx, y + dy) for x, y in pts]

def draw(size, tile):
    s = 8  # supersample
    big = size * s
    img = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    k = big / 64
    if tile:
        d.rounded_rectangle([0, 0, big - 1, big - 1], radius=int(14 * k), fill=(0x1C, 0x20, 0x25, 255))
        d.rounded_rectangle([int(1 * k), int(1 * k), big - int(1 * k), big - int(1 * k)], radius=int(13 * k), outline=(255, 255, 255, 22), width=max(1, int(0.8 * k)))
    width = (7.5 if tile else 9.5) * k
    scale = 0.86 if tile else 1.08
    off = 32 * (1 - scale)
    for (dx, dy), color in (((4, 6), (0x49, 0x6B, 0x5D, 255)), ((-2, -3), (0x8D, 0xB5, 0xA4, 255))):
        for x, y in wave(dx, dy):
            cx, cy = (x * scale + off) * k, (y * scale + off) * k
            r = width / 2
            d.ellipse([cx - r, cy - r, cx + r, cy + r], fill=color)
    return img.resize((size, size), Image.LANCZOS)

out = sys.argv[1] if len(sys.argv) > 1 else "src/Lilt.App/Assets"
frames = [draw(n, tile=n >= 32) for n in (16, 20, 24, 32, 40, 48, 64, 128, 256)]
frames[-1].save(f"{out}/lilt.ico", sizes=[f.size for f in frames], append_images=frames[:-1])
draw(512, True).save(f"{out}/lilt-512.png")
draw(64, False).save(f"{out}/lilt-mark-64.png")
print("ok")
