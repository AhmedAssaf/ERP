"""Score LLM review output against the answer keys (spike W-22).

Reads out/llm/<tender>/<vendor>.json (the offer-review-v1 JSON shape) and samples/tenders/<tender>/answer-key.json.
Exact: verdict equals the key. Safe: the model never called a requirement met when the key says it is not;
that error matters most, because a human may trust a "met".
"""
import json
from pathlib import Path

HERE = Path(__file__).parent
rows, exact, safe, total = [], 0, 0, 0
for key_file in sorted((HERE / "samples" / "tenders").glob("*/answer-key.json")):
    key = json.loads(key_file.read_text(encoding="utf-8"))
    for vid, offer in key["offers"].items():
        f = HERE / "out" / "llm" / key["tender"] / f"{vid}.json"
        if not f.exists():
            rows.append(f"| {key['tender']} | {vid} | {offer['format']} | no output | | |")
            continue
        got = {r["id"]: r["verdict"] for r in json.loads(f.read_text(encoding="utf-8"))["requirements"]}
        e = sum(got.get(i) == v for i, v in offer["expected"].items())
        wrong_met = [i for i, v in offer["expected"].items() if v != "met" and got.get(i) == "met"]
        missed = [f"{i} {v}->{got.get(i)}" for i, v in offer["expected"].items() if got.get(i) != v]
        n = len(offer["expected"])
        exact, safe, total = exact + e, safe + n - len(wrong_met), total + n
        rows.append(f"| {key['tender']} | {vid} | {offer['format']} | {e}/{n} | {', '.join(wrong_met) or 'none'} | {'; '.join(missed) or '-'} |")

print("| Tender | Offer | Format | Exact | Wrongly called met | Differences (key -> model) |")
print("|---|---|---|---|---|---|")
print("\n".join(rows))
if total:
    print(f"\nExact agreement {exact}/{total} ({exact / total:.0%}); never wrongly 'met' on {safe}/{total} ({safe / total:.0%})")
