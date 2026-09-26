"""Test the generated tenders (spike W-22).

1. Answer keys are consistent with the RFPs.
2. A deterministic price checker (the F-48 rules that need no LLM) finds exactly the planted financial errors.
3. Every file converts to Markdown (MarkItDown for DOCX, Docling for PDF and scans, PdfPig logical for text PDFs
   as comparison) and the facts an evaluator needs, every token with a digit, survive.

Writes out/tenders/<tender>/rfp.md and <vendor>-technical.md for the LLM step, and prints a report.
Usage: python test_tenders.py [--no-convert]
"""
import json
import re
import sys
from pathlib import Path

import convert  # converters and truststore setup
from tenders_data import TENDERS

HERE = Path(__file__).parent
SRC = HERE / "samples" / "tenders"
OUT = HERE / "out" / "tenders"
VERDICTS = {"met", "partial", "not_met", "unclear"}
failures = []


def check(cond, msg):
    if not cond:
        failures.append(msg)
    return cond


# ---------- 1. answer keys ----------

def test_answer_keys():
    for t in TENDERS:
        key = json.loads((SRC / t["id"] / "answer-key.json").read_text(encoding="utf-8"))
        ids = set(key["requirements"])
        clean = 0
        for vid, o in key["offers"].items():
            check(set(o["expected"]) == ids, f"{t['id']}/{vid}: expected IDs {sorted(o['expected'])} != RFP IDs {sorted(ids)}")
            check(set(o["expected"].values()) <= VERDICTS, f"{t['id']}/{vid}: unknown verdict")
            check((SRC / t["id"] / o["technical"]).exists(), f"{t['id']}/{vid}: technical file missing")
            check((SRC / t["id"] / o["financial"]).exists(), f"{t['id']}/{vid}: financial file missing")
            clean += all(v == "met" for v in o["expected"].values()) and not o["flags"]
        check(clean == 1, f"{t['id']}: {clean} fully compliant offers, want exactly 1")
    print("1. answer keys:", "ok" if not failures else "FAIL")


# ---------- 2. deterministic price checks ----------

def num(s):
    return float(s.replace(",", "")) if re.fullmatch(r"[\d,]+(\.\d+)?", s.strip()) else None


def boq_lines(rfp):
    tables = [b[1] for b in rfp if b[0] == "t" and len(b[1][0]) == 3 and b[1][0][1] in ("الوحدة", "Unit")]
    return {row[0]: row for row in tables[-1][1:]}


def price_findings(t, offer):
    """Rules: line total = quantity x unit price, subtotal = sum of lines, VAT = 15%, total = subtotal + VAT,
    every BoQ line priced, quantities match the BoQ, unit matches the BoQ."""
    table = next(b[1] for b in offer["fin"] if b[0] == "t")
    lines = [r for r in table[1:] if r[1]]
    totals = {r[0]: num(r[3]) for r in table[1:] if not r[1]}
    found = []
    line_sum = 0
    for name, qty, unit, total in lines:
        q, u, tot = num(qty), num(unit), num(total)
        if q is not None and u is not None and tot is not None:
            line_sum += tot
            if abs(q * u - tot) > 0.5:
                found.append(f"arithmetic: {name} {qty} x {unit} written {total}")
    sub = next(v for k, v in totals.items() if k.startswith(("المجموع", "Subtotal")))
    vat = next(v for k, v in totals.items() if k.startswith(("ضريبة", "VAT")))
    grand = next(v for k, v in totals.items() if k.startswith(("الإجمالي", "Total")))
    if abs(sub - line_sum) > 0.5:
        found.append(f"arithmetic: subtotal written {sub:,.0f}, lines add to {line_sum:,.0f}")
    if abs(sub * 0.15 - vat) > 0.5 or abs(sub + vat - grand) > 0.5:
        found.append("arithmetic: VAT or total")
    boq = boq_lines(t["rfp"])
    priced = {r[0]: r for r in lines}
    for item, (_, unit, qty) in boq.items():
        match = next((p for n, p in priced.items() if n.startswith(item.split(",")[0].split(" (")[0][:12])), None)
        if match is None:
            found.append(f"missing line: {item}")
            continue
        if unit.startswith("user per") and unit not in match[0]:
            found.append(f"unit: {match[0]} vs BoQ {unit}")
        if qty.isdigit() and num(match[1]) not in (None, float(qty)) and "شهر" not in unit:
            found.append(f"quantity: {match[0]} {match[1]} vs BoQ {qty}")
        if num(match[3]) is None:
            found.append(f"not priced: {match[0]} ({match[3]})")
    return found


def test_prices():
    before = len(failures)
    for t in TENDERS:
        for o in t["offers"]:
            found = price_findings(t, o)
            planted = [f for f in o["flags"] if f.startswith("financial:") and "below" not in f and "half" not in f]
            print(f"   {t['id']:20} {o['id']:16} planted {len(planted)} found {len(found)}: {'; '.join(found) or '-'}")
            check(len(found) >= len(planted), f"{t['id']}/{o['id']}: price checker found {found}, planted {planted}")
            check(bool(found) == bool(planted), f"{t['id']}/{o['id']}: false positive or miss {found}")
    print("2. price checks:", "ok" if len(failures) == before else "FAIL")


# ---------- 3. conversion ----------

def facts(blocks):
    toks = []
    for b in blocks:
        texts = [b[1]] if b[0] in ("h", "p") else ([c for row in b[1] for c in row] if b[0] == "t" else [])
        for text in texts:
            toks += [w.strip(".,:;()%") for w in text.split() if re.search(r"\d", w)]
    return [t for t in toks if t]


def md_tokens(md):
    return {w.strip(".,:;()%|*#") for w in re.split(r"[\s|]+", md)}


def pipeline(path):
    if path.suffix == ".docx":
        return {"markitdown": convert.markitdown}
    return {"docling": convert.docling, "pdfpig-logical": convert.pdfpig_logical}


def test_conversion():
    rows = []
    for t in TENDERS:
        folder = OUT / t["id"]
        folder.mkdir(parents=True, exist_ok=True)
        rfp = next(p for ext in (".docx", ".pdf") if (p := SRC / t["id"] / f"rfp{ext}").exists())
        md, _ = convert.markitdown(rfp) if rfp.suffix == ".docx" else convert.docling(rfp)
        (folder / "rfp.md").write_text(md, encoding="utf-8")
        for o in t["offers"]:
            for part, blocks in (("technical", o["tech"]), ("financial", o["fin"])):
                path = next((SRC / t["id"] / "offers" / o["id"]).glob(f"{part}.*"))
                want = facts(blocks)
                for tool, fn in pipeline(path).items():
                    md, _ = fn(path)
                    got = md_tokens(md)
                    recall = sum(f in got for f in want) / len(want)
                    missing = sorted({f for f in want if f not in got})[:4]
                    rows.append((t["id"], o["id"], part, o["format"], tool, recall, missing))
                    if tool != "pdfpig-logical" and part == "technical":
                        (folder / f"{o['id']}-technical.md").write_text(md, encoding="utf-8")
                    print(f"   {t['id']:20} {o['id']:16} {part:9} {o['format']:7} {tool:14} facts {recall:4.0%}  missing {missing}", flush=True)
    print("3. conversion: done")
    return rows


def main():
    test_answer_keys()
    test_prices()
    rows = [] if "--no-convert" in sys.argv else test_conversion()
    if rows:
        (HERE / "out" / "tenders" / "conversion.json").write_text(json.dumps(rows, ensure_ascii=False, indent=1), encoding="utf-8")
    print("\nFAILURES:\n  " + "\n  ".join(failures) if failures else "\nall checks passed")
    sys.exit(1 if failures else 0)


if __name__ == "__main__":
    main()
