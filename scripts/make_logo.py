#!/usr/bin/env python3
"""Renders assets/logo.svg: sync arrows around a play mark, plus the wordmark as outlines.

Needs fonttools and uharfbuzz (pip install fonttools uharfbuzz) and Adwaita Sans.
Render the png afterwards with:
    rsvg-convert -w 1600 -h 900 assets/logo.svg -o assets/logo.png
"""

import argparse
import math
import tempfile
from pathlib import Path

import uharfbuzz as hb
from fontTools.pens.svgPathPen import SVGPathPen
from fontTools.pens.transformPen import TransformPen
from fontTools.ttLib import TTFont
from fontTools.varLib.instancer import instantiateVariableFont

ROOT = Path(__file__).resolve().parent.parent
DEFAULT_FONT = "/usr/share/fonts/Adwaita/AdwaitaSans-Regular.ttf"

# The dimmed prefix keeps the long name readable at catalogue thumbnail size.
WORDMARK = [("jellyfin-", "#8E96A3"), ("cc-sync", "#FFFFFF")]

# Same canvas, icon box and wordmark origin as jellyfin-watchnotify's logo.
ICON_TRANSFORM = "translate(75.9 246.1) scale(17.3516)"
WORDMARK_X = 545.4
WORDMARK_RIGHT_MARGIN = 80
WORDMARK_MAX_CAP = 118
ICON_CENTER_Y = 450


def fmt(value: float) -> str:
    return ("%.2f" % value).rstrip("0").rstrip(".")


def wordmark_paths(font_path: str) -> list[str]:
    font = instantiateVariableFont(TTFont(font_path), {"wght": 700})
    with tempfile.NamedTemporaryFile(suffix=".ttf") as tmp:
        font.save(tmp.name)
        hbfont = hb.Font(hb.Face(hb.Blob.from_file_path(tmp.name)))

    glyph_set = font.getGlyphSet()
    order = font.getGlyphOrder()
    cap = font["OS/2"].sCapHeight

    buf = hb.Buffer()
    buf.add_str("".join(text for text, _ in WORDMARK))
    buf.guess_segment_properties()
    hb.shape(hbfont, buf, {"kern": True, "liga": False})

    glyphs, advance = [], 0
    for info, pos in zip(buf.glyph_infos, buf.glyph_positions):
        glyphs.append((order[info.codepoint], advance + pos.x_offset, pos.y_offset))
        advance += pos.x_advance

    max_width = 1600 - WORDMARK_X - WORDMARK_RIGHT_MARGIN
    scale = min(max_width / advance, WORDMARK_MAX_CAP / cap)
    baseline = ICON_CENTER_Y + cap * scale / 2

    paths, index = [], 0
    for text, color in WORDMARK:
        pen = SVGPathPen(glyph_set, ntos=fmt)
        for name, gx, gy in glyphs[index:index + len(text)]:
            transform = (scale, 0, 0, -scale, WORDMARK_X + gx * scale, baseline - gy * scale)
            glyph_set[name].draw(TransformPen(pen, transform))
        index += len(text)
        paths.append(f'  <path fill="{color}" d="{pen.getCommands()}"/>')
    return paths


def icon_paths() -> list[str]:
    center, radius = 12.0, 8.4

    def point(deg: float) -> tuple[float, float]:
        a = math.radians(deg)
        return center + radius * math.cos(a), center + radius * math.sin(a)

    def arc(start: float, end: float) -> str:
        (x0, y0), (x1, y1) = point(start), point(end)
        return f"M{fmt(x0)} {fmt(y0)}A{fmt(radius)} {fmt(radius)} 0 0 1 {fmt(x1)} {fmt(y1)}"

    def head(deg: float, length: float = 3.1, spread: float = 45) -> str:
        a = math.radians(deg)
        px, py = point(deg)
        # Unit vector pointing back along the clockwise tangent.
        bx, by = math.sin(a), -math.cos(a)
        arms = []
        for s in (spread, -spread):
            sa = math.radians(s)
            arms.append((px + (bx * math.cos(sa) - by * math.sin(sa)) * length,
                         py + (bx * math.sin(sa) + by * math.cos(sa)) * length))
        (ax, ay), (cx, cy) = arms
        return f"M{fmt(ax)} {fmt(ay)}L{fmt(px)} {fmt(py)}L{fmt(cx)} {fmt(cy)}"

    return [
        f'    <path d="{arc(212, 338)}"/>',
        f'    <path d="{head(338)}"/>',
        f'    <path d="{arc(32, 158)}"/>',
        f'    <path d="{head(158)}"/>',
        '    <path d="M10 8.9 10 15.1 15.3 12Z" fill="url(#accent)" stroke-width="0.9"/>',
    ]


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--font", default=DEFAULT_FONT)
    parser.add_argument("--out", default=str(ROOT / "assets" / "logo.svg"))
    args = parser.parse_args()

    icon = "\n".join(icon_paths())
    wordmark = "\n".join(wordmark_paths(args.font))
    svg = f"""<svg xmlns="http://www.w3.org/2000/svg" width="1600" height="900" viewBox="0 0 1600 900">
  <defs>
    <linearGradient id="bg" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="#17171C"/>
      <stop offset="0.55" stop-color="#0E0E12"/>
      <stop offset="1" stop-color="#08080A"/>
    </linearGradient>
    <radialGradient id="glow" cx="0.28" cy="0.5" r="0.55">
      <stop offset="0" stop-color="#3E7BB4" stop-opacity="0.15"/>
      <stop offset="1" stop-color="#3E7BB4" stop-opacity="0"/>
    </radialGradient>
    <linearGradient id="accent" x1="0" y1="0" x2="1" y2="1">
      <stop offset="0" stop-color="#00A4DC"/>
      <stop offset="1" stop-color="#AA5CC3"/>
    </linearGradient>
  </defs>

  <rect width="1600" height="900" fill="url(#bg)"/>
  <rect width="1600" height="900" fill="url(#glow)"/>

  <g transform="{ICON_TRANSFORM}"
     fill="none" stroke="url(#accent)" stroke-width="2.6" stroke-linecap="round" stroke-linejoin="round">
{icon}
  </g>

{wordmark}
</svg>
"""
    Path(args.out).write_text(svg, encoding="utf-8")
    print(args.out)


if __name__ == "__main__":
    main()
