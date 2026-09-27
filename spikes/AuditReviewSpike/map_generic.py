"""Map any ERP's flat exports to the review input format, driven by a JSON profile.

Usage:
    python map_generic.py <profile.json> <export_folder> <out_folder>
    python review.py <out_folder> <report_folder>

A profile names the export files and says which column feeds which review field. Profiles for NetSuite,
ERPNext and Zoho Books are in profiles/, with profiles/template.json explaining every key. Onboarding a
new ERP means copying the template and filling in its column names, not writing code.

Column specs, wherever a column is expected:
    "Col"                               the column (a list tries each name in turn)
    null                                leave empty
    {"const": "x"}                      a fixed value
    {"column": "Col", "after": "#"}     text after the last "#" (for "Purchase Order #PO123")
    {"column": "Col", "last_part": "/"} last segment (for "All / Office supplies")
    {"multiply": ["Qty", "Rate"]}       product of two columns (when there is no line amount)
    {"divide": ["Price", "Price unit"]} first column divided by the second (price per 1 unit)

Profile options: "date_order" ("dmy", "mdy" or "auto"), "utc_offset_hours", and on invoices "unique_by",
"invoice_amounts": "negative" (ledgers that show a vendor invoice as a negative amount), and a paid-date
lookup whose "key" and "match" may be {"all": [spec, spec]} when invoice numbers are unique only per vendor.
"""
import json
import sys
from pathlib import Path

import pandas as pd

from mapcommon import (INVOICE_COLUMNS, PO_COLUMNS, VENDOR_COLUMNS, approval_limits, date_order, dates, has_time, num,
                       read, write, ymd, ymdhm)


def pick(df, spec):
    """Evaluate a column spec against a frame; returns a Series of strings or numbers."""
    if spec is None:
        return pd.Series("", index=df.index)
    if isinstance(spec, (str, list)):
        names = [spec] if isinstance(spec, str) else spec
        col = next((c for c in names if c in df.columns), None)
        if col is None:
            raise KeyError(f"none of {names} in {df.attrs.get('file', 'export')}; found {list(df.columns)}")
        return df[col]
    if "const" in spec:
        return pd.Series(spec["const"], index=df.index)
    if "multiply" in spec:
        a, b = spec["multiply"]
        return pick(df, a).map(num) * pick(df, b).map(num)
    if "divide" in spec:
        a, b = spec["divide"]
        d = pick(df, b).map(num).fillna(1).replace(0, 1)
        return pick(df, a).map(num) / d
    s = pick(df, spec["column"])
    if "after" in spec:
        s = s.map(lambda v: str(v).split(spec["after"])[-1].strip() if v else "")
    if "last_part" in spec:
        s = s.map(lambda v: str(v).split(spec["last_part"])[-1].strip() if v else "")
    return s


def load(src, sources, name, warn, optional=False):
    cfg = sources[name]
    df = read(src, cfg["file"], optional=optional or cfg.get("optional", False), fill=cfg.get("fill_down", []), warn=warn)
    if df is None:
        return None
    before = len(df)
    for how, rules in (("keep", cfg.get("keep", {})), ("drop", cfg.get("drop", {}))):
        for col, values in rules.items():
            if col not in df:
                warn.append(f"{df.attrs['file']}: filter column '{col}' not found, so its {how} filter was not applied")
                continue
            hit = df[col].str.lower().isin([v.lower() for v in values])
            df = df[hit] if how == "keep" else df[~hit]
    df.attrs["dropped"] = before - len(df)
    return df


def keys(df, spec):
    """A lookup key: one column spec, or {"all": [spec, spec]} for a key made of several columns."""
    parts = spec["all"] if isinstance(spec, dict) and "all" in spec else [spec]
    return [pick(df, p) for p in parts]


def run(profile_path, src, out):
    prof = json.loads(Path(profile_path).read_text(encoding="utf-8"))
    sources = prof["sources"]
    offset = pd.Timedelta(hours=prof.get("utc_offset_hours", 0))
    notes, warn = [], []
    po_spec, inv_spec = prof["purchase_orders"], prof["invoices"]
    ap, lk, vspec = prof.get("approvals"), inv_spec.get("paid_date_lookup"), prof.get("vendors")

    lines = load(src, sources, po_spec["source"], warn)
    hist = load(src, sources, ap["source"], warn, optional=True) if ap else None
    bills = load(src, sources, inv_spec["source"], warn)
    pay = load(src, sources, lk["source"], warn, optional=True) if lk else None
    ven = load(src, sources, vspec["source"], warn, optional=True) if vspec else None
    for df, what in ((lines, "order lines"), (bills, "invoices")):
        notes.append(f"{df.attrs['file']} ({what}): {len(df) + df.attrs['dropped']} rows, {df.attrs['dropped']} dropped by the profile's filters")

    # Day or month first: from the profile, or from every date column of this export set together.
    raw = [pick(lines, po_spec["columns"].get("po_date")), pick(bills, inv_spec["columns"].get("invoice_date"))]
    if hist is not None:
        raw.append(pick(hist, ap["time"]))
    if pay is not None:
        raw.append(pick(pay, lk["date"]))
    if ven is not None:
        raw.append(pick(ven, vspec["columns"].get("created_date")))
    order = prof.get("date_order", "auto").lower()
    if order in ("dmy", "mdy"):
        dayfirst = order == "dmy"
    else:
        dayfirst, proven = date_order(*raw)
        if not proven:
            warn.append("Dates never show whether day or month comes first: day-first assumed; set date_order in the profile")

    # Purchase order lines
    c = po_spec["columns"]
    po = pd.DataFrame({k: pick(lines, c.get(k)) for k in PO_COLUMNS})
    po["po_date"] = ymd(dates(po["po_date"], offset, dayfirst))
    for k in ("quantity", "unit_price", "amount"):
        po[k] = pd.to_numeric(po[k].map(num) if po[k].dtype == object else po[k], errors="coerce")
    po["amount"] = po["amount"].where(po["amount"].notna(), po["quantity"] * po["unit_price"]).round(2)
    po["category"] = po["category"].replace("", "Uncategorised")

    # Approvals: the latest approval row per order, user and time from the same row
    if hist is not None:
        hist = hist.assign(_po=pick(hist, ap["po"]), _user=pick(hist, ap["user"]),
                           _t=dates(pick(hist, ap["time"]), offset, dayfirst)).sort_values("_t")
        last = hist.groupby("_po").tail(1).set_index("_po")
        po["approver_id"] = po["po_id"].map(last["_user"]).fillna("")
        po["approved_at"] = ymdhm(po["po_id"].map(last["_t"]))
        notes.append(f"Approvals: {len(last)} orders have an approval record")
    elif c.get("approved_at"):
        plain = dates(po["approved_at"], None, dayfirst)       # judge "has a time" before shifting
        timed = plain.map(has_time)
        po["approved_at"] = ymdhm((plain + offset).where(timed))
        if not timed.any():
            warn.append("Approval dates have no time: approval time left empty, so the night and weekend check skips them")
    else:
        warn.append("No approval history in this profile or folder: rule R7 cannot run")
    if not c.get("requester_id"):
        warn.append("No requester column: the self-approval part of rule R7 cannot run, and split orders need three orders")

    # Invoices: credit notes left out; paid date looked up from the payments export if the profile has one
    if inv_spec.get("unique_by"):
        bills = bills.drop_duplicates(inv_spec["unique_by"])
    c = inv_spec["columns"]
    amounts = pick(bills, c.get("amount")).map(num)
    if inv_spec.get("invoice_amounts") == "negative":   # some ledgers show a vendor invoice as a negative amount
        amounts = -amounts
    credits = amounts <= 0
    if credits.any():
        notes.append(f"{int(credits.sum())} credit notes or zero amounts left out of the invoice rules")
    bills, amounts = bills[~credits], amounts[~credits]
    invoices = pd.DataFrame({k: pick(bills, c.get(k)) for k in INVOICE_COLUMNS})
    invoices["invoice_date"] = ymd(dates(invoices["invoice_date"], None, dayfirst))
    invoices["amount"] = amounts
    if pay is not None:
        pay_keys, days = keys(pay, lk["key"]), dates(pick(pay, lk["date"]), None, dayfirst)
        paid = {}
        for row, d in zip(zip(*pay_keys), days):
            for part in [x.strip() for x in str(row[0]).split(lk.get("split", "\u0000"))]:
                if part and pd.notna(d):
                    k = (part, *row[1:])
                    paid[k] = max(paid.get(k, d), d)
        match = keys(bills, lk["match"])
        invoices["paid_date"] = ymd(pd.Series([paid.get(tuple(r)) for r in zip(*match)], index=bills.index,
                                              dtype="datetime64[ns]"))
    elif c.get("paid_date"):
        invoices["paid_date"] = ymd(dates(invoices["paid_date"], None, dayfirst))
    else:
        warn.append("No payment dates: rule R5 cannot run")

    # Vendors, bank accounts, staff
    if ven is not None:
        c = vspec["columns"]
        vendors = pd.DataFrame({k: pick(ven, c.get(k)) for k in VENDOR_COLUMNS})
        vendors["created_date"] = ymd(dates(vendors["created_date"], None, dayfirst))
    else:
        warn.append("No vendor export: vendor list built from the orders and invoices, without created dates (rule R5 off)")
        ids = sorted(set(po["vendor_id"]) | set(invoices["vendor_id"]))
        vendors = pd.DataFrame({"vendor_id": ids, "name": ids, "cr_number": "", "bank_iban": "", "created_date": ""})
    bank = prof.get("vendor_banks")
    banks = load(src, sources, bank["source"], warn, optional=True) if bank else None
    if banks is not None:
        g = pd.DataFrame({"k": pick(banks, bank["key"]), "v": pick(banks, bank["iban"])})
        ibans = {k: ";".join(sorted({x for x in grp["v"] if x})) for k, grp in g.groupby("k")}
        vendors["bank_iban"] = vendors["vendor_id"].map(ibans).fillna(vendors["bank_iban"])
    if not vendors["bank_iban"].ne("").any():
        warn.append("No vendor bank accounts: rule R4 cannot run")
    missing = (set(po["vendor_id"]) | set(invoices["vendor_id"])) - set(vendors["vendor_id"])
    if missing:
        warn.append(f"{len(missing)} vendors on orders or invoices are not in the vendor export (for example {sorted(missing)[0]})")
    st = prof.get("staff")
    people = load(src, sources, st["source"], warn, optional=True) if st else None
    staff = None
    if people is not None:
        staff = pd.DataFrame({"staff_id": pick(people, st["columns"]["staff_id"]), "name": "", "department": "",
                              "bank_iban": pick(people, st["columns"]["bank_iban"])}).query("bank_iban != ''").drop_duplicates()
    else:
        warn.append("No staff bank accounts: rule R4 skipped")

    limits = approval_limits(src, warn, prof.get("limits_hint", "the approval policy"))
    write(src, out, f"{prof['name']} (profile {Path(profile_path).name}, datetimes {prof.get('utc_offset_hours', 0):+g} h)",
          po[PO_COLUMNS], invoices[INVOICE_COLUMNS], vendors[VENDOR_COLUMNS], staff, limits, notes, warn)


if __name__ == "__main__":
    if len(sys.argv) != 4:
        raise SystemExit(__doc__)
    run(sys.argv[1], Path(sys.argv[2]), Path(sys.argv[3]))
