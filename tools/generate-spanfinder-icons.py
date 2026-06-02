#!/usr/bin/env python3
# SpanFinder fork icons (dual-pane + finder lens). NOT the official three-column Span logo.
# Run: python tools/generate-spanfinder-icons.py

from pathlib import Path

from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "src" / "Span" / "Span" / "Assets"
COLOR_TOP = (0x0D, 0x94, 0x88)
COLOR_BOTTOM = (0x14, 0xB8, 0xA6)
ACCENT = (0xFB, 0xBF, 0x24)
# left pane, right pane: x, y, w, h, opacity
PANES = ((72, 118, 168, 276, 0.92), (272, 118, 168, 276, 0.55))
LENS_CENTER = (368, 156)
LENS_RADIUS = 52
LENS_HANDLE = ((406, 194), (438, 226))


def _lerp(a, b, t):
    return int(a + (b - a) * t)


def _bg_color(x, y, size):
    t = (x / max(size - 1, 1) + y / max(size - 1, 1)) * 0.5
    return (
        _lerp(COLOR_TOP[0], COLOR_BOTTOM[0], t),
        _lerp(COLOR_TOP[1], COLOR_BOTTOM[1], t),
        _lerp(COLOR_TOP[2], COLOR_BOTTOM[2], t),
    )


def _scale(v, size):
    return int(round(v * size / 512))


def render_icon_small(size):
    """Opaque icon for 16-32px shell tiles (transparent ICO looks white in Start menu)."""
    img = Image.new("RGB", (size, size), COLOR_TOP)
    px = img.load()
    for y in range(size):
        for x in range(size):
            px[x, y] = _bg_color(x, y, size)
    draw = ImageDraw.Draw(img)
    mid = size // 2
    gap = max(1, size // 16)
    draw.rectangle((mid - gap, size // 5, mid + gap, size - size // 5), fill=(255, 255, 255))
    r = max(2, size // 5)
    draw.ellipse((size - r * 2 - 1, 1, size - 1, r * 2 + 1), outline=ACCENT, width=max(1, size // 12))
    return img.convert("RGBA")


def render_icon(size):
    if size <= 32:
        return render_icon_small(size)

    # Opaque canvas — transparent corners appear as white in Start menu / taskbar cache.
    img = Image.new("RGB", (size, size), COLOR_TOP)
    px = img.load()
    for y in range(size):
        for x in range(size):
            px[x, y] = _bg_color(x, y, size)

    draw = ImageDraw.Draw(img)
    pane_radius = max(1, _scale(20, size))
    for px0, py0, pw, ph, alpha in PANES:
        fill = (
            int(255 * alpha + COLOR_TOP[0] * (1 - alpha)),
            int(255 * alpha + COLOR_TOP[1] * (1 - alpha)),
            int(255 * alpha + COLOR_TOP[2] * (1 - alpha)),
        )
        draw.rounded_rectangle(
            (_scale(px0, size), _scale(py0, size), _scale(px0 + pw, size), _scale(py0 + ph, size)),
            radius=pane_radius,
            fill=fill,
        )

    if size >= 32:
        cx = _scale(LENS_CENTER[0], size)
        cy = _scale(LENS_CENTER[1], size)
        r = _scale(LENS_RADIUS, size)
        sw = max(2, _scale(18, size))
        draw.ellipse((cx - r, cy - r, cx + r, cy + r), outline=ACCENT, width=sw)
        (x1, y1), (x2, y2) = LENS_HANDLE
        draw.line(
            (_scale(x1, size), _scale(y1, size), _scale(x2, size), _scale(y2, size)),
            fill=ACCENT,
            width=sw,
        )

    return img.convert("RGBA")


def save_png(path, image):
    path.parent.mkdir(parents=True, exist_ok=True)
    image.save(path, "PNG", optimize=True)


def save_ico(path, sizes):
    images = [render_icon(s).convert("RGBA") for s in sizes]
    # Newest image first is required for correct multi-size ICO entries in Windows shell.
    images.reverse()
    images[0].save(
        path,
        format="ICO",
        sizes=[(i.width, i.height) for i in images],
        append_images=images[1:],
    )


def render_wide(width, height):
    icon = render_icon(min(height, width))
    canvas = Image.new("RGBA", (width, height), (0, 0, 0, 0))
    scale = int(height * 0.72)
    icon = icon.resize((scale, scale), Image.Resampling.LANCZOS)
    canvas.paste(icon, ((width - scale) // 2, (height - scale) // 2), icon)
    return canvas


def main():
    ASSETS.mkdir(parents=True, exist_ok=True)
    save_ico(ASSETS / "app.ico", (16, 24, 32, 48, 64, 128, 256))
    save_png(ASSETS / "Square44x44Logo.png", render_icon(44))
    save_png(ASSETS / "Square44x44Logo.scale-200.png", render_icon(88))
    save_png(ASSETS / "Square44x44Logo.targetsize-24_altform-unplated.png", render_icon(24))
    save_png(ASSETS / "Square44x44Logo.targetsize-32_altform-unplated.png", render_icon(32))
    save_png(ASSETS / "Square44x44Logo.targetsize-48_altform-unplated.png", render_icon(48))
    save_png(ASSETS / "Square44x44Logo.targetsize-256_altform-unplated.png", render_icon(256))
    save_png(ASSETS / "Square150x150Logo.scale-200.png", render_icon(300))
    save_png(ASSETS / "StoreLogo.png", render_icon(50))
    save_png(ASSETS / "Wide310x150Logo.scale-200.png", render_wide(620, 300))
    save_png(ASSETS / "SplashScreen.scale-200.png", render_icon(256))
    save_png(ASSETS / "LockScreenLogo.scale-200.png", render_icon(48))
    save_png(ASSETS / "Onboarding" / "app-icon.png", render_icon(128))
    print("Wrote icons to", ASSETS)


if __name__ == "__main__":
    main()
