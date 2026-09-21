"""Convert a project markdown document into OneNote 2013 page XML.
Usage: python md2onenote.py <pageId> <out.xml> <doc.md> [mermaid-png ...]
Mermaid blocks are replaced, in order, by the PNG paths given; ![alt](path) images are embedded from paths relative to the document."""
import sys, re, base64, html, pathlib
from pathlib import Path
from PIL import Image

MD = Path(sys.argv[3])
HERE = Path(__file__).parent
IMAGES = sys.argv[4:]
NS = "http://schemas.microsoft.com/office/onenote/2013/onenote"
MAX_W = 900

page_id, out_path = sys.argv[1], sys.argv[2]
lines = MD.read_text(encoding="utf-8").splitlines()


def inline(text: str) -> str:
    """Escape, then apply bold / code / links (OneNote T accepts simple HTML)."""
    t = html.escape(text, quote=False)
    t = re.sub(r"\[([^\]]+)\]\(([^)]+)\)", r'<a href="\2">\1</a>', t)
    t = re.sub(r"\*\*(.+?)\*\*", r"<b>\1</b>", t)
    t = re.sub(r"(?<!\*)\*(?!\*)(.+?)\*(?!\*)", r"<i>\1</i>", t)
    t = re.sub(r"`([^`]+)`", r'<span style="font-family:Consolas">\1</span>', t)
    return t


def T(s: str) -> str:
    return f"<one:T><![CDATA[{s}]]></one:T>"


def oe(content: str, style=None, extra="") -> str:
    attr = f' quickStyleIndex="{style}"' if style is not None else ""
    return f"<one:OE{attr}>{extra}{content}</one:OE>"


def image_oe(path: Path) -> str:
    w, h = Image.open(path).size
    if w > MAX_W:
        h = round(h * MAX_W / w)
        w = MAX_W
    data = base64.b64encode(path.read_bytes()).decode()
    return (f'<one:OE><one:Image format="png"><one:Size width="{w}" height="{h}" isSetByUser="true"/>'
            f"<one:Data>{data}</one:Data></one:Image></one:OE>")


def table_oe(rows):
    ncol = max(len(r) for r in rows)
    width = MAX_W / ncol
    cols = "".join(f'<one:Column index="{i}" width="{width:.0f}"/>' for i in range(ncol))
    out = []
    for ri, r in enumerate(rows):
        cells = []
        for ci in range(ncol):
            txt = r[ci] if ci < len(r) else ""
            txt = inline(txt)
            if ri == 0:
                txt = f"<b>{txt}</b>"
            shading = ' shadingColor="#E7E6F5"' if ri == 0 else ""
            cells.append(f"<one:Cell{shading}><one:OEChildren>{oe(T(txt))}</one:OEChildren></one:Cell>")
        out.append("<one:Row>" + "".join(cells) + "</one:Row>")
    return f'<one:OE><one:Table bordersVisible="true"><one:Columns>{cols}</one:Columns>{"".join(out)}</one:Table></one:OE>'


# quick styles: 1=h1 2=h2 3=h3 4=p (indices chosen to avoid the default PageTitle at 0)
STYLES = {"h1": 1, "h2": 2, "h3": 3, "p": 4}
qs = (
    f'<one:QuickStyleDef index="1" name="h1" fontColor="#1E4E79" highlightColor="automatic" font="Calibri" fontSize="18.0" spaceBefore="12.0" spaceAfter="4.0"/>'
    f'<one:QuickStyleDef index="2" name="h2" fontColor="#2F5496" highlightColor="automatic" font="Calibri" fontSize="15.0" spaceBefore="10.0" spaceAfter="3.0"/>'
    f'<one:QuickStyleDef index="3" name="h3" fontColor="#404040" highlightColor="automatic" font="Calibri" fontSize="13.0" spaceBefore="8.0" spaceAfter="2.0"/>'
    f'<one:QuickStyleDef index="4" name="p" fontColor="automatic" highlightColor="automatic" font="Calibri" fontSize="11.0" spaceBefore="0.0" spaceAfter="0.0"/>'
)

body = []
title = None
i = 0
img_idx = 0
para = []
table = []


def flush_para():
    global para
    if para:
        body.append(oe(T(inline(" ".join(para))), STYLES["p"]))
        para = []


def flush_table():
    global table
    if table:
        body.append(table_oe(table))
        table = []


while i < len(lines):
    ln = lines[i]
    if ln.startswith("```mermaid"):
        flush_para(); flush_table()
        while not lines[i].strip() == "```":
            i += 1
        body.append(image_oe(Path(IMAGES[img_idx])))
        img_idx += 1
        i += 1
        continue
    m_img = re.match(r"^!\[[^\]]*\]\(([^)]+)\)\s*$", ln)
    if m_img:
        flush_para(); flush_table()
        img = pathlib.Path(m_img.group(1))
        if not img.is_absolute(): img = MD.parent / img
        body.append(image_oe(img))
        i += 1
        continue
    if ln.startswith("```"):
        flush_para(); flush_table()
        i += 1
        code = []
        while not lines[i].strip() == "```":
            code.append(html.escape(lines[i], quote=False).replace(" ", "&nbsp;"))
            i += 1
        for c in code:
            body.append(oe(T('<span style="font-family:Consolas;font-size:9.5pt">' + (c or "&nbsp;") + "</span>"), STYLES["p"]))
        i += 1
        continue
    if ln.startswith("# ") and title is None:
        title = ln[2:].strip(); i += 1; continue
    m = re.match(r"^(#{1,3}) (.*)", ln)
    if m:
        flush_para(); flush_table()
        level = {1: "h1", 2: "h2", 3: "h3"}[len(m.group(1))]
        body.append(oe(T(inline(m.group(2))), STYLES[level]))
        i += 1; continue
    if ln.startswith("|"):
        flush_para()
        cells = [c.strip() for c in ln.strip().strip("|").split("|")]
        if not all(re.fullmatch(r":?-+:?", c) for c in cells):
            table.append(cells)
        i += 1; continue
    flush_table()
    m = re.match(r"^(\d+)\. (.*)", ln)
    if m:
        flush_para()
        body.append(oe(T(inline(m.group(2))), STYLES["p"],
                       '<one:List><one:Number numberSequence="0" numberFormat="##."/></one:List>'))
        i += 1; continue
    m = re.match(r"^- (.*)", ln)
    if m:
        flush_para()
        body.append(oe(T(inline(m.group(1))), STYLES["p"], '<one:List><one:Bullet bullet="2"/></one:List>'))
        i += 1; continue
    if ln.strip() == "":
        flush_para(); i += 1; continue
    para.append(ln.strip())
    i += 1
flush_para(); flush_table()

xml = (f'<?xml version="1.0"?><one:Page xmlns:one="{NS}" ID="{page_id}" lang="en-US">'
       f"{qs}<one:Title><one:OE>{T(html.escape(title, quote=False))}</one:OE></one:Title>"
       f'<one:Outline><one:Position x="36" y="86" z="0"/><one:Size width="{MAX_W + 30}" height="200" isSetByUser="true"/>'
       f'<one:OEChildren>{"".join(body)}</one:OEChildren></one:Outline></one:Page>')
Path(out_path).write_text(xml, encoding="utf-8")
print(f"title={title} blocks={len(body)} images={img_idx} bytes={len(xml)}")
