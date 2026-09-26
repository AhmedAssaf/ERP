"""Score LLM review output against the answer keys (spike W-22).

Usage: python score_llm.py [results folder under out/, default llm]
Reads out/<folder>/<tender>/<vendor>.json (the offer-review-v1 JSON shape) and samples/tenders/<tender>/answer-key.json.
Exact: verdict equals the key. Safe: the model never called a requirement met when the key says it is not;
that error matters most, because a human may trust a "met".
"""
import json
import sys
from pathlib import Path

HERE = Path(__file__).parent
RESULTS = HERE / "out" / (sys.argv[1] if len(sys.argv) > 1 else "llm")
rows, exact, safe, total, malformed = [], 0, 0, 0, []
by_format = {}
for key_file in sorted((HERE / "samples" / "tenders").glob("*/answer-key.json")):
    key = json.loads(key_file.read_text(encoding="utf-8"))
    for vid, offer in key["offers"].items():
        f = RESULTS / key["tender"] / f"{vid}.json"
        if not f.exists():
            rows.append(f"| {key['tender']} | {vid} | {offer['format']} | no output | | |")
            continue
        text = f.read_text(encoding="utf-8")
        data, end = json.JSONDecoder().raw_decode(text.strip())
        if text.strip()[end:].strip():  # the product must reject this; structured output prevents it
            malformed.append(f"{key['tender']}/{vid}")
        got = {r["id"]: r["verdict"] for r in data["requirements"]}
        e = sum(got.get(i) == v for i, v in offer["expected"].items())
        wrong_met = [i for i, v in offer["expected"].items() if v != "met" and got.get(i) == "met"]
        missed = [f"{i} {v}->{got.get(i)}" for i, v in offer["expected"].items() if got.get(i) != v]
        n = len(offer["expected"])
        exact, safe, total = exact + e, safe + n - len(wrong_met), total + n
        kind = "scanned" if offer["format"] == "scanned" else "docx and Word PDF"
        agg = by_format.setdefault(kind, [0, 0])
        agg[0], agg[1] = agg[0] + e, agg[1] + n
        rows.append(f"| {key['tender']} | {vid} | {offer['format']} | {e}/{n} | {', '.join(wrong_met) or 'none'} | {'; '.join(missed) or '-'} |")

print("| Tender | Offer | Format | Exact | Wrongly called met | Differences (key -> model) |")
print("|---|---|---|---|---|---|")
print("\n".join(rows))
if total:
    print(f"\nExact agreement {exact}/{total} ({exact / total:.0%}); never wrongly 'met' on {safe}/{total} ({safe / total:.0%})")
if total:
    print("By format: " + "; ".join(f"{k} {a}/{b} ({a / b:.0%})" for k, (a, b) in by_format.items()))
    print("Malformed JSON (extra text after the object): " + (", ".join(malformed) or "none"))
