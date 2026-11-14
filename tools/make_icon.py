"""Renders the ETaskManager icon at each size from vector shapes (supersampled), then writes
a multi-resolution .ico (PNG-compressed entries), a 256 px PNG logo and a preview sheet."""
import io
import struct
import sys
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter

OUT_DIR = Path(sys.argv[1])
PREVIEW = Path(sys.argv[2])
SS = 8  # supersampling factor


def lerp(a, b, t):
    return tuple(round(a[i] + (b[i] - a[i]) * t) for i in range(len(a)))


def hexc(h, a=255):
    h = h.lstrip("#")
    return (int(h[0:2], 16), int(h[2:4], 16), int(h[4:6], 16), a)


def vgradient(w, h, top, bottom):
    g = Image.new("RGBA", (w, h))
    px = g.load()
    for y in range(h):
        c = lerp(top, bottom, y / max(1, h - 1))
        for x in range(w):
            px[x, y] = c
    return g


def rounded_mask(w, h, box, r):
    m = Image.new("L", (w, h), 0)
    ImageDraw.Draw(m).rounded_rectangle(box, r, fill=255)
    return m


def render(size):
    S = size * SS
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))

    tiny = size <= 20
    small = size <= 40

    # --- tile ---------------------------------------------------------------------------
    margin = 0 if tiny else round(S * 0.045)
    radius = S * (0.24 if tiny else 0.22)
    box = (margin, margin, S - margin - 1, S - margin - 1)
    bg = vgradient(S, S, hexc("#25344D"), hexc("#0B1119"))
    img.paste(bg, (0, 0), rounded_mask(S, S, box, radius))
    if not tiny:
        # subtle top highlight rim
        d = ImageDraw.Draw(img)
        d.rounded_rectangle(box, radius, outline=hexc("#4A6286", 120), width=max(SS, round(S * 0.012)))

    # --- plot area ----------------------------------------------------------------------
    pad = S * (0.17 if tiny else 0.2)
    left, right = margin + pad, S - margin - pad
    bottom = S - margin - pad * (0.95 if tiny else 0.9)
    top = margin + pad * (0.9 if tiny else 1.05)
    ph = bottom - top

    heights = [0.42, 0.68, 0.52, 0.95] if not tiny else [0.45, 0.75, 0.55, 1.0]
    n = len(heights)
    gap = (right - left) * (0.16 if tiny else 0.14)
    bw = ((right - left) - gap * (n - 1)) / n
    bar_r = bw * (0.18 if tiny else 0.28)

    cool_top, cool_bot = hexc("#5CC8FF"), hexc("#2563C9")
    hot_top, hot_bot = hexc("#FFB347"), hexc("#F0483E")

    tops = []
    for i, hf in enumerate(heights):
        x0 = left + i * (bw + gap)
        x1 = x0 + bw
        y0 = bottom - ph * hf
        hot = i == n - 1
        grad = vgradient(S, S, hot_top if hot else cool_top, hot_bot if hot else cool_bot)
        # gradient spans the bar itself
        grad = vgradient(int(x1 - x0) + 1, int(bottom - y0) + 1,
                         hot_top if hot else cool_top, hot_bot if hot else cool_bot)
        mask = Image.new("L", grad.size, 0)
        ImageDraw.Draw(mask).rounded_rectangle((0, 0, grad.size[0] - 1, grad.size[1] - 1), bar_r, fill=255)
        img.paste(grad, (int(x0), int(y0)), mask)
        tops.append(((x0 + x1) / 2, y0))

    # --- pulse line over the bars (not at tiny sizes: it would turn to mush) -------------
    if not tiny:
        lw = S * (0.05 if small else 0.04)
        lift = S * 0.07
        pts = [(left - pad * 0.35, bottom - ph * 0.30)]
        pts += [(x, y - lift) for x, y in tops]
        pts[-1] = (pts[-1][0], max(top - lift * 0.2, pts[-1][1]))

        line = Image.new("RGBA", (S, S), (0, 0, 0, 0))
        ld = ImageDraw.Draw(line)
        ld.line(pts, fill=hexc("#E9F7FF"), width=round(lw), joint="curve")
        for x, y in pts:  # round joins and caps
            r = lw * 0.5
            ld.ellipse((x - r, y - r, x + r, y + r), fill=hexc("#E9F7FF"))
        # end dot
        ex, ey = pts[-1]
        r = lw * (1.25 if small else 1.1)
        ld.ellipse((ex - r, ey - r, ex + r, ey + r), fill=hexc("#FFFFFF"))

        if not small:
            glow = line.copy()
            alpha = glow.getchannel("A").filter(ImageFilter.GaussianBlur(S * 0.03))
            glow = Image.new("RGBA", (S, S), hexc("#5CC8FF"))
            glow.putalpha(alpha.point(lambda a: int(a * 0.9)))
            img = Image.alpha_composite(img, glow)
        # dark under-stroke so the line separates from the bars
        shadow = Image.new("RGBA", (S, S), (0, 0, 0, 0))
        sd = ImageDraw.Draw(shadow)
        sd.line(pts, fill=hexc("#0B1119", 230), width=round(lw * 2.1), joint="curve")
        for x, y in pts:
            r = lw * 1.05
            sd.ellipse((x - r, y - r, x + r, y + r), fill=hexc("#0B1119", 230))
        img = Image.alpha_composite(img, shadow)
        img = Image.alpha_composite(img, line)

    return img.resize((size, size), Image.LANCZOS)


def write_ico(path, images):
    entries, blobs = [], []
    offset = 6 + 16 * len(images)
    for im in images:
        buf = io.BytesIO()
        im.save(buf, "PNG")
        data = buf.getvalue()
        w = im.width if im.width < 256 else 0
        entries.append(struct.pack("<BBBBHHII", w, w, 0, 0, 1, 32, len(data), offset))
        blobs.append(data)
        offset += len(data)
    with open(path, "wb") as f:
        f.write(struct.pack("<HHH", 0, 1, len(images)))
        for e in entries:
            f.write(e)
        for b in blobs:
            f.write(b)


sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
images = [render(s) for s in sizes]
OUT_DIR.mkdir(parents=True, exist_ok=True)
write_ico(OUT_DIR / "app.ico", images)
render(512).save(OUT_DIR / "logo.png")

# Preview: every size on a dark and a light taskbar-like strip, plus 4x zoom of small sizes.
W = 900
sheet = Image.new("RGBA", (W, 520), hexc("#202020"))
d = ImageDraw.Draw(sheet)
d.rectangle((0, 260, W, 520), fill=hexc("#F3F3F3"))
for row, y0 in enumerate((20, 280)):
    x = 20
    for im in images:
        sheet.alpha_composite(im, (x, y0))
        x += im.width + 16
    x = 20
    for s in (16, 24, 32):
        im = images[sizes.index(s)].resize((s * 4, s * 4), Image.NEAREST)
        sheet.alpha_composite(im, (x, y0 + 130 if row == 0 else y0 + 130))
        x += s * 4 + 30
sheet.save(PREVIEW)
print("ok")
