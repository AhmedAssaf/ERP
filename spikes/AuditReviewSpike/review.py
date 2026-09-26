"""Procurement audit review: eight red-flag rules over one ERP export.

Usage:
    python review.py [input_folder] [output_folder]

Defaults: input sample/, output output/. Input is five CSV files; see README.md.
Writes findings.csv and report.html (print the report to PDF from a browser).
Staff bank accounts are compared as SHA-256 hashes and never written to any output.
"""
import hashlib
import html
import sys
from datetime import datetime
from pathlib import Path

import pandas as pd

HERE = Path(__file__).parent
SPLIT_WINDOW_DAYS = 14
SPLIT_BAND = 0.80          # a PO counts toward a split when it is 80% to 100% of a limit
SINGLE_SOURCE_SHARE = 0.90
SINGLE_SOURCE_MIN_POS = 5
DUPLICATE_WINDOW_DAYS = 3
NEW_VENDOR_DAYS = 7
PRICE_OUTLIER_FACTOR = 1.5
PRICE_MIN_OBSERVATIONS = 5
OFFICE_START, OFFICE_END = 7, 20
WEEKEND = {4, 5}           # Friday, Saturday
ROUND_UNIT, ROUND_MIN = 1_000, 5_000
PATTERN_MIN = 3

RULES = {
    "R1": "Split orders under an approval limit",
    "R2": "Single source in a category",
    "R3": "Duplicate invoice",
    "R4": "Vendor bank account matches a staff account",
    "R5": "New vendor paid within days",
    "R6": "Unit price far above the median",
    "R7": "Approval control weakness",
    "R8": "Round or sequential invoices",
}


def sha(value):
    return hashlib.sha256(str(value).strip().upper().replace(" ", "").encode()).hexdigest()


def mask(iban):
    iban = str(iban)
    return iban[:4] + "*" * (len(iban) - 8) + iban[-4:]


def load(folder):
    read = lambda n: pd.read_csv(folder / n, dtype=str)
    pos = read("purchase_orders.csv")
    inv = read("invoices.csv")
    ven = read("vendors.csv")
    staff = read("staff.csv") if (folder / "staff.csv").exists() else pd.DataFrame(columns=["staff_id", "bank_iban"])
    limits = read("approval_limits.csv")
    for c in ("quantity", "unit_price", "amount"):
        pos[c] = pos[c].astype(float)
    inv["amount"] = inv["amount"].astype(float)
    limits["limit_sar"] = limits["limit_sar"].astype(float)
    pos["po_date"] = pd.to_datetime(pos["po_date"])
    pos["approved_at"] = pd.to_datetime(pos["approved_at"])
    inv["invoice_date"] = pd.to_datetime(inv["invoice_date"])
    inv["paid_date"] = pd.to_datetime(inv["paid_date"])
    ven["created_date"] = pd.to_datetime(ven["created_date"])
    # Hash staff accounts at load time; the raw values are dropped here.
    staff = staff.assign(iban_hash=staff["bank_iban"].map(sha)).drop(columns=["bank_iban"])
    return pos, inv, ven, staff, limits


def po_totals(pos):
    """One row per PO: approval limits and approvals apply to the whole order, not a line."""
    return (pos.groupby("po_id", as_index=False)
            .agg(po_date=("po_date", "first"), vendor_id=("vendor_id", "first"),
                 requester_id=("requester_id", "first"), approver_id=("approver_id", "first"),
                 approved_at=("approved_at", "first"), amount=("amount", "sum")))


def finding(rule, reference, vendor, amount, evidence):
    return {"rule": rule, "title": RULES[rule], "reference": reference, "vendor_id": vendor,
            "amount_sar": round(float(amount), 2), "evidence": evidence}


def r1_split(pos, limits):
    out = []
    pos = po_totals(pos)
    for limit in sorted(limits["limit_sar"]):
        band = pos[(pos["amount"] >= limit * SPLIT_BAND) & (pos["amount"] < limit)]
        for (vendor, requester), g in band.groupby(["vendor_id", "requester_id"]):
            g = g.sort_values("po_date")
            used = set()
            for i, row in g.iterrows():
                if i in used:
                    continue
                window = g[(g["po_date"] >= row["po_date"]) &
                           (g["po_date"] <= row["po_date"] + pd.Timedelta(days=SPLIT_WINDOW_DAYS))]
                if len(window) >= 2 and window["amount"].sum() >= limit:
                    used.update(window.index)
                    out.append(finding("R1", ";".join(window["po_id"]), vendor, window["amount"].sum(),
                                       f"{len(window)} POs by {requester} within {SPLIT_WINDOW_DAYS} days, "
                                       f"each just under the SAR {limit:,.0f} limit, together "
                                       f"SAR {window['amount'].sum():,.0f}"))
    return out


def r2_single_source(pos):
    out = []
    for cat, g in pos.groupby("category"):
        spend = g.groupby("vendor_id")["amount"].sum().sort_values(ascending=False)
        share = spend.iloc[0] / spend.sum()
        orders = g["po_id"].nunique()
        if orders >= SINGLE_SOURCE_MIN_POS and share >= SINGLE_SOURCE_SHARE:
            out.append(finding("R2", cat, spend.index[0], spend.iloc[0],
                               f"{share:.0%} of {orders} POs' spend in {cat} went to one vendor; "
                               f"{len(spend) - 1} other vendor{'' if len(spend) == 2 else 's'} used"))
    return out


def r3_duplicates(inv):
    out, seen = [], set()
    same_no = inv[inv.duplicated(["vendor_id", "invoice_no"], keep=False)]
    for (vendor, no), g in same_no.groupby(["vendor_id", "invoice_no"]):
        seen.update(g.index)
        out.append(finding("R3", no, vendor, g["amount"].iloc[1:].sum(),
                           f"Invoice {no} paid {len(g)} times, SAR {g['amount'].iloc[0]:,.2f} each"))
    rest = inv.drop(index=list(seen)).sort_values("invoice_date")
    for (vendor, amount), g in rest.groupby(["vendor_id", "amount"]):
        if len(g) < 2:
            continue
        dates = g["invoice_date"].tolist()
        for a, b in zip(range(len(g)), range(1, len(g))):
            if (dates[b] - dates[a]).days <= DUPLICATE_WINDOW_DAYS:
                pair = g.iloc[[a, b]]
                out.append(finding("R3", ";".join(pair["invoice_no"]), vendor, amount,
                                   f"Two invoices for the same amount SAR {amount:,.2f} "
                                   f"{(dates[b] - dates[a]).days} day(s) apart"))
    return out


def r4_bank_conflict(ven, inv, staff):
    out = []
    staff_hashes = dict(zip(staff["iban_hash"], staff["staff_id"]))
    for _, v in ven.iterrows():
        for account in str(v["bank_iban"]).split(";"):
            sid = staff_hashes.get(sha(account)) if account.strip() else None
            if sid:
                paid = inv.loc[inv["vendor_id"] == v["vendor_id"], "amount"].sum()
                out.append(finding("R4", v["vendor_id"], v["vendor_id"], paid,
                                   f"Vendor account {mask(account.strip())} matches the account of staff member "
                                   f"{sid}; total paid SAR {paid:,.0f}"))
    return out


def r5_new_vendor(ven, inv):
    out = []
    first_paid = inv.groupby("vendor_id")["paid_date"].min()
    for _, v in ven.iterrows():
        paid = first_paid.get(v["vendor_id"])
        if pd.notna(paid) and pd.notna(v["created_date"]) and (paid - v["created_date"]).days <= NEW_VENDOR_DAYS:
            amount = inv.loc[inv["vendor_id"] == v["vendor_id"], "amount"].sum()
            out.append(finding("R5", v["vendor_id"], v["vendor_id"], amount,
                               f"Created {v['created_date']:%Y-%m-%d}, first paid {paid:%Y-%m-%d} "
                               f"({(paid - v['created_date']).days} days)"))
    return out


def r6_price_outliers(pos):
    out = []
    for item, g in pos.groupby("item"):
        if len(g) < PRICE_MIN_OBSERVATIONS:
            continue
        median = g["unit_price"].median()
        for _, row in g[g["unit_price"] >= median * PRICE_OUTLIER_FACTOR].iterrows():
            over = (row["unit_price"] - median) * row["quantity"]
            out.append(finding("R6", row["po_id"], row["vendor_id"], over,
                               f"{item}: SAR {row['unit_price']:,.2f} vs median SAR {median:,.2f} "
                               f"({row['unit_price'] / median:.1f}x); estimated overpayment shown"))
    return out


def r7_approvals(pos):
    out = []
    for _, row in po_totals(pos).iterrows():
        reasons = []
        if pd.notna(row["approver_id"]) and row["requester_id"] == row["approver_id"]:
            reasons.append("requester approved own PO")
        t = row["approved_at"]
        if pd.notna(t) and (t.hour < OFFICE_START or t.hour >= OFFICE_END):
            reasons.append(f"approved at {t:%H:%M}")
        if pd.notna(t) and t.weekday() in WEEKEND:
            reasons.append(f"approved on {t:%A}")
        if reasons:
            text = f"{'; '.join(reasons)} by {row['approver_id']}"
            out.append(finding("R7", row["po_id"], row["vendor_id"], row["amount"], text[0].upper() + text[1:]))
    return out


def r8_patterns(inv):
    out = []
    for vendor, g in inv.groupby("vendor_id"):
        rnd = g[(g["amount"] % ROUND_UNIT == 0) & (g["amount"] >= ROUND_MIN)]
        nums = pd.to_numeric(g["invoice_no"].str.extract(r"(\d+)$")[0], errors="coerce").dropna()
        nums = sorted(set(nums.astype(int)))
        run = best = 1
        for a, b in zip(nums, nums[1:]):
            run = run + 1 if b == a + 1 else 1
            best = max(best, run)
        if len(rnd) >= PATTERN_MIN or best >= PATTERN_MIN:
            parts = []
            if len(rnd) >= PATTERN_MIN:
                parts.append(f"{len(rnd)} round-number invoices")
            if best >= PATTERN_MIN:
                parts.append(f"{best} invoice numbers in a row (few other customers?)")
            out.append(finding("R8", vendor, vendor, rnd["amount"].sum(), "; ".join(parts)))
    return out


def run(inp, out_dir):
    pos, inv, ven, staff, limits = load(inp)
    findings = (r1_split(pos, limits) + r2_single_source(pos) + r3_duplicates(inv) +
                r4_bank_conflict(ven, inv, staff) + r5_new_vendor(ven, inv) + r6_price_outliers(pos) +
                r7_approvals(pos) + r8_patterns(inv))
    df = pd.DataFrame(findings, columns=["rule", "title", "reference", "vendor_id", "amount_sar", "evidence"])
    names = dict(zip(ven["vendor_id"], ven["name"]))
    df["vendor_name"] = df["vendor_id"].map(names)
    df = df.sort_values(["amount_sar"], ascending=False).reset_index(drop=True)
    df.insert(0, "finding", [f"F-{i + 1:03d}" for i in range(len(df))])
    out_dir.mkdir(exist_ok=True)
    df.to_csv(out_dir / "findings.csv", index=False, encoding="utf-8-sig")
    stats = {"pos": pos["po_id"].nunique(), "spend": pos["amount"].sum(), "invoices": len(inv), "vendors": len(ven),
             "from": pos["po_date"].min(), "to": pos["po_date"].max()}
    (out_dir / "report.html").write_text(report(df, stats), encoding="utf-8")
    check = inp / "answer_key.csv"
    if check.exists():
        score(df, pd.read_csv(check, dtype=str))
    print(f"{len(df)} findings, SAR {df['amount_sar'].sum():,.0f} involved -> {out_dir / 'report.html'}")


def score(df, key):
    """Sample data only: did every planted anomaly surface?"""
    hit = 0
    for _, k in key.iterrows():
        rule = k["rule"].split()[0]
        refs = set(k["reference"].split(";"))
        rows = df[df["rule"] == rule]
        found = any(refs & set(str(r).split(";")) or str(v) in refs or k["reference"] in str(r)
                    for r, v in zip(rows["reference"], rows["vendor_id"]))
        hit += found
        if not found:
            print(f"  MISSED {k['rule']}: {k['reference']}")
    print(f"Answer key: {hit} of {len(key)} planted anomalies found")


CSS = """
:root{--ink:#1d2433;--muted:#5b6474;--line:#dde2ea;--bg:#ffffff;--card:#f5f7fa;--accent:#0f5c8c;--hi:#b3261e}
@media (prefers-color-scheme: dark){:root{--ink:#e6e9ef;--muted:#a3abb9;--line:#343b48;--bg:#14171c;--card:#1d222a;--accent:#6cb4e4;--hi:#f2837a}}
body{font-family:"IBM Plex Sans Arabic","Segoe UI",Arial,sans-serif;color:var(--ink);background:var(--bg);margin:0;padding:24px 16px;line-height:1.5}
main{max-width:1000px;margin:0 auto}
h1{font-size:1.6rem;margin:0}.ar{font-size:1.1rem;color:var(--muted);margin:0 0 16px}
.cards{display:grid;grid-template-columns:repeat(auto-fit,minmax(170px,1fr));gap:12px;margin:16px 0 24px}
.card{background:var(--card);border:1px solid var(--line);border-radius:8px;padding:12px}
.card b{display:block;font-size:1.4rem;font-variant-numeric:tabular-nums}.card span{color:var(--muted);font-size:.85rem}
table{border-collapse:collapse;width:100%;font-size:.9rem;margin-bottom:24px}
th,td{border-bottom:1px solid var(--line);padding:6px 8px;text-align:start;vertical-align:top}
th{color:var(--muted);font-weight:600}.n{text-align:end;font-variant-numeric:tabular-nums;white-space:nowrap}
.tag{color:var(--accent);font-weight:600;white-space:nowrap}.top td{background:var(--card)}
.note{color:var(--muted);font-size:.85rem}.wrap{overflow-x:auto}
@media print{body{padding:0}.card{break-inside:avoid}tr{break-inside:avoid}}
"""


def report(df, s):
    e = html.escape
    by_rule = (df.groupby(["rule", "title"]).agg(n=("finding", "count"), amount=("amount_sar", "sum"))
               .reset_index())
    rule_rows = "".join(
        f"<tr><td class='tag'>{e(r.rule)}</td><td>{e(r.title)}</td><td class='n'>{r.n}</td>"
        f"<td class='n'>{r.amount:,.0f}</td></tr>" for r in by_rule.itertuples())
    missing = [f"<tr><td class='tag'>{k}</td><td>{e(v)}</td><td class='n'>0</td><td class='n'>0</td></tr>"
               for k, v in RULES.items() if k not in set(df["rule"])]
    rows = "".join(
        f"<tr{' class=top' if i < 5 else ''}><td class='tag'>{e(r.finding)}</td><td class='tag'>{e(r.rule)}</td>"
        f"<td>{e(str(r.vendor_name or ''))}<br><span class='note'>{e(str(r.vendor_id))}</span></td>"
        f"<td>{e(r.evidence)}<br><span class='note'>Ref: {e(str(r.reference))}</span></td>"
        f"<td class='n'>{r.amount_sar:,.0f}</td><td></td></tr>"
        for i, r in enumerate(df.itertuples()))
    return f"""<!doctype html><html lang="en"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1"><title>Procurement Audit Review</title>
<style>{CSS}</style></head><body><main>
<h1>Procurement audit review</h1><p class="ar" lang="ar" dir="rtl">مراجعة تدقيق المشتريات</p>
<p class="note">Period {s['from']:%Y-%m-%d} to {s['to']:%Y-%m-%d}. Generated {datetime.now():%Y-%m-%d %H:%M}.
Every finding is a question for the reviewer, not a conclusion.</p>
<div class="cards">
<div class="card"><b>{s['pos']:,}</b><span>Purchase orders reviewed (100%)</span></div>
<div class="card"><b>SAR {s['spend']:,.0f}</b><span>Spend reviewed</span></div>
<div class="card"><b>{len(df)}</b><span>Findings to review</span></div>
<div class="card"><b>SAR {df['amount_sar'].sum():,.0f}</b><span>Amount involved in findings</span></div>
</div>
<h2>Findings by rule</h2><div class="wrap"><table><thead><tr><th>Rule</th><th>What it checks</th>
<th class="n">Findings</th><th class="n">SAR involved</th></tr></thead><tbody>{rule_rows}{''.join(missing)}</tbody></table></div>
<h2>All findings, largest first</h2><div class="wrap"><table><thead><tr><th>#</th><th>Rule</th><th>Vendor</th>
<th>Evidence</th><th class="n">SAR</th><th>Reviewer note</th></tr></thead><tbody>{rows}</tbody></table></div>
<p class="note">Amount involved: R1 the split POs together; R2 the vendor's spend in the category; R3 the extra
payment; R4 and R5 total paid to the vendor; R6 estimated overpayment against the median; R7 the PO amount;
R8 the round-number invoices. Staff bank accounts were compared as hashes and are not shown.</p>
</main></body></html>"""


if __name__ == "__main__":
    inp = Path(sys.argv[1]) if len(sys.argv) > 1 else HERE / "sample"
    out = Path(sys.argv[2]) if len(sys.argv) > 2 else HERE / "output"
    run(inp, out)
