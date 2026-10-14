"""Generate the LlamaManager application icon.

Design: dark navy rounded-square plate with a blue->violet gradient,
a stylized llama head silhouette (side profile) and subtle chip nodes
suggesting local inference.

Rendered from vector math at any size; writes a multi-resolution .ico
and a preview .png.
"""

import os
from PIL import Image, ImageDraw

# --- palette -------------------------------------------------------------
PLATE_TOP = (0x1D, 0x26, 0x45)
PLATE_BOTTOM = (0x0C, 0x11, 0x22)
GRAD_A = (0x5B, 0x8C, 0xFF)   # blue
GRAD_B = (0x8B, 0x5C, 0xFF)   # violet
HEAD = (0xF2, 0xF6, 0xFF)     # llama silhouette
HEAD_SHADE = (0xC3, 0xD2, 0xF5)
EYE = (0x14, 0x1A, 0x2E)
NODE = (0x7F, 0x9D, 0xFF)

SIZES = (16, 20, 24, 32, 40, 48, 64, 128, 256)


def lerp(a, b, t):
    return tuple(round(a[i] + (b[i] - a[i]) * t) for i in range(3))


def rounded_rect_mask(size, radius_ratio):
    ss = 4
    mask = Image.new("L", (size * ss, size * ss), 0)
    ImageDraw.Draw(mask).rounded_rectangle(
        [0, 0, size * ss - 1, size * ss - 1], radius=size * ss * radius_ratio, fill=255
    )
    return mask.resize((size, size), Image.LANCZOS)


def llama_head(size):
    """Stylized llama side profile, returned in unit coords (0..size).

    Landmarks: long neck lower-left, muzzle right, two ears on top,
    rounded skull. Tuned to read clearly from 32px down.
    """
    s = size
    return [
        (0.300 * s, 0.760 * s),   # neck base, left
        (0.318 * s, 0.520 * s),   # neck up
        (0.352 * s, 0.430 * s),   # jaw / cheek
        (0.300 * s, 0.372 * s),   # back of skull
        (0.330 * s, 0.300 * s),   # ear notch
        (0.372 * s, 0.232 * s),   # left ear tip
        (0.418 * s, 0.296 * s),   # between ears
        (0.470 * s, 0.226 * s),   # right ear tip
        (0.512 * s, 0.300 * s),   # forehead
        (0.610 * s, 0.372 * s),   # brow
        (0.706 * s, 0.430 * s),   # muzzle top
        (0.712 * s, 0.512 * s),   # muzzle tip
        (0.640 * s, 0.548 * s),   # mouth
        (0.556 * s, 0.520 * s),   # chin
        (0.470 * s, 0.560 * s),   # throat
        (0.462 * s, 0.760 * s),   # neck base, right
    ]


def draw_icon(size):
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    # --- plate ------------------------------------------------------------
    for y in range(size):
        t = y / max(size - 1, 1)
        d.line([(0, y), (size, y)], fill=lerp(PLATE_TOP, PLATE_BOTTOM, t) + (255,))

    # --- blue -> violet diagonal glow (top-left to bottom-right) ----------
    glow = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    gd = ImageDraw.Draw(glow)
    steps = size * 2
    for i in range(steps):
        t = i / max(steps - 1, 1)
        col = lerp(GRAD_A, GRAD_B, t)
        x = -size + (size * 3) * t
        gd.line([(x, 0), (x - size, size)], fill=col + (46,), width=max(1, size // 16))
    img.alpha_composite(glow)

    # --- chip nodes: a few dots + thin rails, bottom-right ----------------
    if size >= 32:
        lw = max(1, round(size / 160))
        for (ax, ay, bx, by) in (
            (0.62, 0.80, 0.86, 0.80),
            (0.62, 0.88, 0.86, 0.88),
            (0.74, 0.72, 0.74, 0.92),
        ):
            d.line(
                [(ax * size, ay * size), (bx * size, by * size)], fill=NODE + (120,), width=lw
            )
        for (nx, ny) in ((0.62, 0.80), (0.86, 0.80), (0.62, 0.88), (0.86, 0.88)):
            r = max(1, size * 0.016)
            d.ellipse(
                [nx * size - r, ny * size - r, nx * size + r, ny * size + r], fill=NODE + (200,)
            )

    # --- llama head silhouette -------------------------------------------
    ss = 4
    layer = Image.new("RGBA", (size * ss, size * ss), (0, 0, 0, 0))
    ld = ImageDraw.Draw(layer)
    pts = [(x * ss, y * ss) for x, y in llama_head(size)]
    ld.polygon(pts, fill=HEAD + (255,))

    if size >= 24:
        # ear inner shading and eye
        ld.polygon(
            [
                (0.372 * size * ss, 0.240 * size * ss),
                (0.400 * size * ss, 0.290 * size * ss),
                (0.348 * size * ss, 0.320 * size * ss),
            ],
            fill=HEAD_SHADE + (255,),
        )
        r = size * 0.030 * ss
        cx, cy = 0.585 * size * ss, 0.392 * size * ss
        ld.ellipse([cx - r, cy - r, cx + r, cy + r], fill=EYE + (255,))
        # soften outline
        ld.line(pts + [pts[0]], fill=HEAD + (255,), width=round(size * 0.018 * ss), joint="curve")

    img.alpha_composite(layer.resize((size, size), Image.LANCZOS))

    # --- rounded-square mask ---------------------------------------------
    img.putalpha(
        Image.composite(
            img.getchannel("A"), Image.new("L", (size, size), 0), rounded_rect_mask(size, 0.22)
        )
    )
    return img


def main():
    out_dir = r"C:\Users\li.feng\.llama\llama-manager\assets"
    os.makedirs(out_dir, exist_ok=True)

    images = [draw_icon(s) for s in SIZES]
    images[-1].save(
        os.path.join(out_dir, "LlamaManager.ico"),
        format="ICO",
        sizes=[(s, s) for s in SIZES],
        append_images=images[:-1],
    )
    images[-1].save(os.path.join(out_dir, "LlamaManager-256.png"), format="PNG")
    print("ico:", os.path.join(out_dir, "LlamaManager.ico"))
    print("png:", os.path.join(out_dir, "LlamaManager-256.png"))


if __name__ == "__main__":
    main()
