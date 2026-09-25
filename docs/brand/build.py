"""Build the WaslaBid logo files in this folder.

Text is converted to outlines, so the SVGs render the same everywhere without
the font installed. Needs: pip install fonttools uharfbuzz. The IBM Plex Sans
Arabic fonts (OFL) are downloaded once into ./.fonts (git-ignored).

Run: python docs/brand/build.py
It also writes preview.png and waslabid-icon-512.png with headless Chrome or
Edge when one is installed.
"""
import pathlib
import urllib.request

import uharfbuzz as hb
from fontTools.pens.svgPathPen import SVGPathPen
from fontTools.pens.transformPen import TransformPen
from fontTools.ttLib import TTFont

HERE = pathlib.Path(__file__).parent
FONTS = HERE / ".fonts"
FONT_URL = "https://github.com/google/fonts/raw/main/ofl/ibmplexsansarabic/IBMPlexSansArabic-{}.ttf"

NAVY = "#0E3A5B"
TEAL = "#12A588"
WHITE = "#FFFFFF"


def font_path(weight):
    FONTS.mkdir(exist_ok=True)
    path = FONTS / f"IBMPlexSansArabic-{weight}.ttf"
    if not path.exists():
        urllib.request.urlretrieve(FONT_URL.format(weight), path)
    return path


class Face:
    def __init__(self, weight):
        path = font_path(weight)
        self.tt = TTFont(path)
        self.glyphs = self.tt.getGlyphSet()
        self.order = self.tt.getGlyphOrder()
        self.upem = self.tt["head"].unitsPerEm
        self.hb_font = hb.Font(hb.Face(hb.Blob.from_file_path(str(path))))

    def text(self, s, x, baseline, size, fill):
        """Return (svg path element, advance width) for s drawn from x at baseline."""
        buf = hb.Buffer()
        buf.add_str(s)
        buf.guess_segment_properties()
        hb.shape(self.hb_font, buf)
        scale = size / self.upem
        pen = SVGPathPen(self.glyphs)
        cursor = 0
        for info, pos in zip(buf.glyph_infos, buf.glyph_positions):
            name = self.order[info.codepoint]
            gx = x + (cursor + pos.x_offset) * scale
            gy = baseline - pos.y_offset * scale
            self.glyphs[name].draw(TransformPen(pen, (scale, 0, 0, -scale, gx, gy)))
            cursor += pos.x_advance
        return f'<path fill="{fill}" d="{pen.getCommands()}"/>', cursor * scale


# The mark: a sealed envelope whose flap is a W (Wasla, the link between two
# parties), closed by a seal at the join (the sealed bid). Drawn in a 100 x 72 box.
def mark(x, y, s, body=NAVY, flap=WHITE, seal=TEAL, dot=None):
    return f"""<g transform="translate({x} {y}) scale({s})">
  <rect x="0" y="0" width="100" height="72" rx="12" fill="{body}"/>
  <path d="M10 10 L30 40 L50 18 L70 40 L90 10" fill="none" stroke="{flap}"
        stroke-width="7" stroke-linecap="round" stroke-linejoin="round"/>
  <circle cx="50" cy="44" r="12" fill="{seal}" stroke="{body}" stroke-width="4"/>
  <circle cx="50" cy="44" r="4.5" fill="{dot or flap}"/>
</g>"""


def svg(w, h, body, bg=None):
    rect = f'<rect width="{w}" height="{h}" fill="{bg}"/>' if bg else ""
    return (f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {w} {h}" '
            f'width="{w}" height="{h}" role="img" aria-label="WaslaBid">{rect}{body}</svg>\n')


def build():
    semi, bold, reg = Face("SemiBold"), Face("Bold"), Face("Regular")

    def wordmark(x, baseline, size, wasla=NAVY, bid=TEAL):
        a, wa = semi.text("Wasla", x, baseline, size, wasla)
        b, wb = bold.text("Bid", x + wa, baseline, size, bid)
        return a + b, wa + wb

    def arabic(x_end, baseline, size, color=NAVY, accent=TEAL):
        # Right-aligned at x_end: "وصلة" then "بد" to its left.
        _, w1 = semi.text("وصلة", 0, 0, size, color)
        _, gap = semi.text(" ", 0, 0, size, color)
        _, w2 = bold.text("بد", 0, 0, size, accent)
        p1, _ = semi.text("وصلة", x_end - w1, baseline, size, color)
        p2, _ = bold.text("بد", x_end - w1 - gap - w2, baseline, size, accent)
        return p1 + p2, w1 + gap + w2

    files = {}

    # 1. Icon: favicon, app icon, avatar.
    files["waslabid-icon.svg"] = svg(120, 120, mark(10, 24, 1.0))

    # 2. Horizontal English lockup.
    wm, ww = wordmark(0, 0, 64)
    w = 140 + ww + 20
    files["waslabid-horizontal.svg"] = svg(round(w), 120, mark(10, 24, 1.0) + wordmark(128, 83, 64)[0])

    # 3. Bilingual lockup: English over Arabic.
    en, enw = wordmark(128, 64, 56)
    ar, arw = arabic(128 + enw, 112, 40)
    files["waslabid-bilingual.svg"] = svg(round(128 + max(enw, arw) + 20), 136, mark(10, 32, 1.0) + en + ar)

    # 4. Arabic lockup, mark on the right (RTL reading start).
    ar, arw = arabic(0, 0, 60)
    w = round(arw + 20 + 140)
    ar, _ = arabic(w - 128, 84, 60)
    files["waslabid-arabic.svg"] = svg(w, 120, ar + mark(w - 110, 24, 1.0))

    # 5. Reversed bilingual on navy, for dark backgrounds.
    en, enw = wordmark(128, 64, 56, wasla=WHITE)
    ar, arw = arabic(128 + enw, 112, 40, color=WHITE)
    w = round(128 + max(enw, arw) + 20)
    files["waslabid-bilingual-reversed.svg"] = svg(
        w, 136, mark(10, 32, 1.0, body=WHITE, flap=NAVY) + en + ar, bg=NAVY)

    # 6. One-colour navy, for stamps, fax, and embossing.
    en, enw = wordmark(128, 83, 64, bid=NAVY)
    files["waslabid-mono.svg"] = svg(round(128 + enw + 20), 120, mark(10, 24, 1.0, seal=WHITE, dot=NAVY) + en)

    for name, content in files.items():
        (HERE / name).write_text(content, encoding="utf-8")
        print("wrote", name)
    render(files)


BROWSERS = [
    r"C:\Program Files\Google\Chrome\Application\chrome.exe",
    r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
    "/usr/bin/google-chrome",
    "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
]


def shoot(html, out, w, h):
    import subprocess
    import tempfile
    browser = next((b for b in BROWSERS if pathlib.Path(b).exists()), None)
    if not browser:
        print("no Chrome or Edge found; skipped", out)
        return
    with tempfile.NamedTemporaryFile("w", suffix=".html", delete=False, encoding="utf-8") as f:
        f.write(html)
    subprocess.run([browser, "--headless=new", "--disable-gpu", "--hide-scrollbars",
                    f"--window-size={w},{h}", f"--screenshot={HERE / out}",
                    pathlib.Path(f.name).as_uri()], check=True, capture_output=True)
    print("wrote", out)


def render(files):
    tile = ('<div style="background:{bg};padding:28px 32px;border-radius:12px;'
            'display:flex;align-items:center;gap:16px">{svg}</div>')
    order = ["waslabid-horizontal.svg", "waslabid-bilingual.svg", "waslabid-arabic.svg",
             "waslabid-bilingual-reversed.svg", "waslabid-mono.svg"]
    tiles = "".join(tile.format(bg=NAVY if "reversed" in n else "#F4F6F8", svg=files[n]) for n in order)
    sizes = "".join(f'<img width="{s}" height="{s}" src="data:image/svg+xml;utf8,'
                    f'{files["waslabid-icon.svg"].replace(chr(34), chr(39)).replace("#", "%23")}">'
                    for s in (120, 64, 32, 16))
    html = (f'<body style="margin:0;padding:32px;font-family:sans-serif;background:#fff;'
            f'display:grid;gap:20px;grid-template-columns:1fr 1fr">{tiles}'
            f'{tile.format(bg="#F4F6F8", svg=sizes)}</body>')
    shoot(html, "preview.png", 1400, 760)
    icon = files["waslabid-icon.svg"].replace('width="120" height="120"', 'width="512" height="512"')
    shoot(f'<body style="margin:0">{icon}</body>', "waslabid-icon-512.png", 512, 512)


if __name__ == "__main__":
    build()
