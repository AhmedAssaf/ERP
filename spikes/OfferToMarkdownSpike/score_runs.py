"""Score repeated LLM runs of one prompt against the answer keys (spike W-22).

Usage: python score_runs.py llm-v3 llm-v3-r2 llm-v3-r3
Per run: exact agreement. Across runs: spread, how often all runs agree (stability), and the score of the
majority verdict per requirement. A tie takes the most cautious verdict, so a split never becomes "met".
"""
import json
import sys
from collections import Counter
from pathlib import Path

HERE = Path(__file__).parent
RUNS = sys.argv[1:]
CAUTION = ["not_met", "unclear", "partial", "met"]  # tie-break order, most cautious first


def load(run, tender, vid):
    text = (HERE / "out" / run / tender / f"{vid}.json").read_text(encoding="utf-8").strip()
    data, _ = json.JSONDecoder().raw_decode(text)
    return {r["id"]: r["verdict"] for r in data["requirements"]}


def majority(votes):
    counts = Counter(votes)
    top = max(counts.values())
    return min((v for v, c in counts.items() if c == top), key=CAUTION.index)


per_run = {r: [0, 0] for r in RUNS}
maj = {"all": [0, 0, 0], "docx and Word PDF": [0, 0, 0], "scanned": [0, 0, 0]}  # exact, total, wrongly met
stable = [0, 0]
rows = []
for key_file in sorted((HERE / "samples" / "tenders").glob("*/answer-key.json")):
    key = json.loads(key_file.read_text(encoding="utf-8"))
    for vid, offer in key["offers"].items():
        runs = [load(r, key["tender"], vid) for r in RUNS]
        kind = "scanned" if offer["format"] == "scanned" else "docx and Word PDF"
        offer_exact, flips = 0, []
        for rid, want in offer["expected"].items():
            votes = [g.get(rid) for g in runs]
            for r, v in zip(RUNS, votes):
                per_run[r][0] += v == want
                per_run[r][1] += 1
            m = majority(votes)
            for bucket in ("all", kind):
                maj[bucket][0] += m == want
                maj[bucket][1] += 1
                maj[bucket][2] += m == "met" and want != "met"
            offer_exact += m == want
            stable[0] += len(set(votes)) == 1
            stable[1] += 1
            if len(set(votes)) > 1:
                flips.append(f"{rid} {'/'.join(votes)}")
        rows.append(f"| {key['tender']} | {vid} | {offer['format']} | {offer_exact}/{len(offer['expected'])} | {'; '.join(flips) or '-'} |")

print("| Tender | Offer | Format | Majority exact | Requirements where runs disagreed |")
print("|---|---|---|---|---|")
print("\n".join(rows))
scores = [a / b for a, b in per_run.values()]
print("\nPer run: " + ", ".join(f"{r} {a}/{b} ({a / b:.0%})" for r, (a, b) in per_run.items())
      + f"; spread {min(scores):.0%} to {max(scores):.0%}")
print(f"All runs agree on {stable[0]}/{stable[1]} requirements ({stable[0] / stable[1]:.0%})")
for bucket, (e, n, wm) in maj.items():
    print(f"Majority, {bucket}: {e}/{n} ({e / n:.0%}), wrongly met {wm}")
