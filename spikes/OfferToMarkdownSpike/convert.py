"""Convert every sample with every available converter and score the Markdown (spike W-22).

Usage:
  python convert.py                  generated samples, scored against samples/generated/truth.json
  python convert.py samples/real     real offers, no truth: writes Markdown only for a human to read

Output: out/<tool>/<file>.md and out/scores.md
"""
import json
import re
import sys
import time
import unicodedata
from pathlib import Path

try:  # use the Windows certificate store so model downloads work behind TLS inspection
    import truststore
    truststore.inject_into_ssl()
except ImportError:
    pass

HERE = Path(__file__).parent
OUT = HERE / "out"
PAGE = "<!-- page {} -->"


# ---------- converters: each returns (markdown, pages_marked) ----------

def markitdown(path):
    from markitdown import MarkItDown
    text = MarkItDown().convert(str(path)).text_content
    if "\f" in text:  # pdfminer separates pages with form feeds
        pages = text.split("\f")
        return "\n\n".join(PAGE.format(i + 1) + "\n" + p for i, p in enumerate(pages) if p.strip()), True
    return text, False


def pymupdf4llm(path):
    if path.suffix.lower() != ".pdf":
        return None
    import pymupdf4llm as p4
    chunks = p4.to_markdown(str(path), page_chunks=True, show_progress=False)
    return "\n\n".join(PAGE.format(c["metadata"]["page"] if "page" in c["metadata"] else i + 1) + "\n" + c["text"]
                       for i, c in enumerate(chunks)), True


_docling = {}


def docling(path, force_ocr=False):
    try:
        from docling.datamodel.base_models import InputFormat
        from docling.datamodel.pipeline_options import EasyOcrOptions, PdfPipelineOptions
        from docling.document_converter import DocumentConverter, PdfFormatOption
    except ImportError:
        return None
    if force_ocr and path.suffix.lower() != ".pdf":
        return None
    if force_ocr not in _docling:
        ocr = EasyOcrOptions(lang=["ar", "en"], force_full_page_ocr=force_ocr)
        opts = PdfPipelineOptions(do_ocr=True, ocr_options=ocr)
        _docling[force_ocr] = DocumentConverter(format_options={InputFormat.PDF: PdfFormatOption(pipeline_options=opts)})
    doc = _docling[force_ocr].convert(str(path)).document
    marker = "<!-- page -->"
    md = doc.export_to_markdown(page_break_placeholder=marker)
    if marker in md:
        parts = md.split(marker)
        return "\n\n".join(PAGE.format(i + 1) + "\n" + p for i, p in enumerate(parts)), True
    return md, False


def _pdfpig(path, *extra):
    if path.suffix.lower() != ".pdf":
        return None
    import subprocess
    exe = HERE / "PdfPigCheck" / "bin" / "Debug" / "net9.0" / "PdfPigCheck.exe"
    out = subprocess.run([str(exe), str(path), *extra], capture_output=True, check=True)
    return out.stdout.decode("utf-8"), True


def pdfpig(path):
    return _pdfpig(path)


def pdfpig_logical(path):
    return _pdfpig(path, "--logical")


TOOLS = {"markitdown": markitdown, "pymupdf4llm": pymupdf4llm, "pdfpig": pdfpig,
         "pdfpig-logical": pdfpig_logical, "docling": docling,
         "docling-forced-ocr": lambda p: docling(p, force_ocr=True)}


# ---------- scoring ----------

ARABIC_DIACRITICS = re.compile(r"[ً-ْـ]")  # harakat and tatweel
PRESENTATION_FORMS = re.compile(r"[ﭐ-﷿ﹰ-﻿]")


def words(text):
    text = re.sub(r"<!--.*?-->", " ", text)
    text = ARABIC_DIACRITICS.sub("", text)
    text = re.sub(r"[#*|_`>\[\](){}:;,.\-]+", " ", text)
    return text.split()


def bigrams(ws):
    return list(zip(ws, ws[1:]))


def score(md, truth_pages, pages_marked):
    out_words = words(md)
    out_set = set(out_words)
    nfkc_set = set(words(unicodedata.normalize("NFKC", md)))
    out_bi = set(bigrams(out_words))
    t_words, t_bi = [], []
    for page in truth_pages:
        for line in page.split("\n"):
            w = words(line)
            t_words += w
            t_bi += bigrams(w)
    rev_bi = [(b, a) for a, b in t_bi]
    ar_words = [w for w in t_words if re.search(r"[؀-ۿ]", w)]
    return {
        "word recall": sum(w in out_set for w in t_words) / len(t_words),
        "arabic word recall": (sum(w in out_set for w in ar_words) / len(ar_words)) if ar_words else None,
        "recall after NFKC": sum(w in nfkc_set for w in t_words) / len(t_words),
        "order kept": sum(b in out_bi for b in t_bi) / len(t_bi),
        "order reversed": sum(b in out_bi for b in rev_bi) / len(rev_bi),
        "presentation-form chars": len(PRESENTATION_FORMS.findall(md)),
        "page markers": pages_marked,
    }


def pct(v):
    return "n/a" if v is None else f"{v:.0%}"


def main():
    src = Path(sys.argv[1]) if len(sys.argv) > 1 else HERE / "samples" / "generated"
    truth_file = src / "truth.json"
    truth = json.loads(truth_file.read_text(encoding="utf-8")) if truth_file.exists() else {}
    files = sorted(p for p in src.iterdir() if p.suffix.lower() in (".pdf", ".docx"))
    rows = []
    for tool, fn in TOOLS.items():
        for f in files:
            start = time.perf_counter()
            try:
                res = fn(f)
            except Exception as e:  # a converter crash is a result, not a stop
                res = (f"CONVERTER ERROR: {type(e).__name__}: {e}", False)
            secs = time.perf_counter() - start
            if res is None:
                continue
            md, marked = res
            (OUT / tool).mkdir(parents=True, exist_ok=True)
            (OUT / tool / (f.name + ".md")).write_text(md, encoding="utf-8")
            s = score(md, truth[f.name], marked) if f.name in truth else {}
            rows.append((f.name, tool, secs, s))
            print(f"{tool:12} {f.name:24} {secs:6.1f}s", flush=True)

    lines = ["| File | Tool | Seconds | Word recall | Arabic word recall | Recall after NFKC | Order kept | Order reversed | Presentation-form chars | Page markers |",
             "|---|---|---|---|---|---|---|---|---|---|"]
    for name, tool, secs, s in sorted(rows):
        if s:
            lines.append(f"| {name} | {tool} | {secs:.1f} | {pct(s['word recall'])} | {pct(s['arabic word recall'])} | "
                         f"{pct(s['recall after NFKC'])} | {pct(s['order kept'])} | {pct(s['order reversed'])} | "
                         f"{s['presentation-form chars']} | {'yes' if s['page markers'] else 'no'} |")
        else:
            lines.append(f"| {name} | {tool} | {secs:.1f} | | | | | | | |")
    OUT.mkdir(exist_ok=True)
    (OUT / "scores.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
    print("\n".join(lines))


if __name__ == "__main__":
    main()
