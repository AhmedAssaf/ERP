"""Map Oracle E-Business Suite (R12) or Oracle Fusion Cloud table exports to the review input format.

Usage:
    python map_oracle.py <oracle_folder> <out_folder> [utc_offset_hours]
    python review.py <out_folder> <report_folder>

Reads one CSV or Excel file per table, named after it (PO_HEADERS_ALL.csv, ...), as SQL Developer,
Toad or a BI Publisher report exports them: upper-case column names, dates as DD-MON-RR or ISO.
EBS stores local server time, so the default offset is 0. Fusion stores UTC: pass 3 for Riyadh.
See README.md for the tables, the two flattened bank-account queries, and the Fusion differences.
"""
import sys
from pathlib import Path

import pandas as pd

APPROVED = {"APPROVED"}
KEEP_TYPES = {"STANDARD"}
APPROVE_ACTIONS = {"APPROVE", "APPROVE AND FORWARD"}
SUBMIT_ACTIONS = {"SUBMIT"}
INVOICE_TYPES = {"STANDARD", "PREPAYMENT", "MIXED"}   # credit and debit memos are dropped


def read(folder, *tables, required=(), optional=False):
    """Read the first table found among the given names (EBS and Fusion names differ)."""
    for table in tables:
        for ext in (".csv", ".xlsx", ".xls", ".txt"):
            f = folder / f"{table}{ext}"
            if f.exists():
                if ext.startswith(".xls"):
                    df = pd.read_excel(f, dtype=str)
                else:
                    df = pd.read_csv(f, dtype=str, sep=None, engine="python", encoding="utf-8-sig")
                df.columns = [str(c).strip().upper() for c in df.columns]
                df = df.apply(lambda c: c.str.strip() if c.dtype == object else c).fillna("")
                missing = [c for c in required if c not in df.columns]
                if missing:
                    raise SystemExit(f"{f.name}: missing columns {missing}; found {list(df.columns)}")
                df.attrs["table"] = table
                return df
    if optional:
        return None
    raise SystemExit(f"Missing {' or '.join(tables)} (.csv or .xlsx) in {folder}")


def num(v):
    s = str(v).strip().replace(",", "")
    return float(s) if s not in ("", "nan") else float("nan")


def when(v, offset=None):
    s = str(v).strip()
    if not s:
        return pd.NaT
    t = pd.NaT
    for fmt in ("%Y-%m-%d %H:%M:%S", "%Y-%m-%d", "%d-%b-%y", "%d-%b-%Y", "%d-%b-%y %H:%M:%S",
                "%d-%b-%Y %H:%M:%S", "%d/%m/%Y", "%d/%m/%Y %H:%M:%S", "%Y-%m-%dT%H:%M:%S"):
        try:
            t = pd.to_datetime(s, format=fmt)
            break
        except ValueError:
            continue
    if pd.isna(t):
        t = pd.to_datetime(s, errors="coerce")
    return t + offset if offset is not None and pd.notna(t) else t


def run(src, out, offset_hours=0.0):
    offset = pd.Timedelta(hours=offset_hours)
    notes, warn = [], []

    # Suppliers: EBS AP_SUPPLIERS has the name; Fusion POZ_SUPPLIERS needs HZ_PARTIES for it.
    sup = read(src, "AP_SUPPLIERS", "POZ_SUPPLIERS", required=["VENDOR_ID", "SEGMENT1"])
    sup_table = sup.attrs["table"]
    if "VENDOR_NAME" not in sup.columns:
        parties = read(src, "HZ_PARTIES", required=["PARTY_ID", "PARTY_NAME"], optional=True)
        if parties is None or "PARTY_ID" not in sup.columns:
            raise SystemExit("POZ_SUPPLIERS has no VENDOR_NAME: add HZ_PARTIES (PARTY_ID, PARTY_NAME) and PARTY_ID")
        sup = sup.merge(parties[["PARTY_ID", "PARTY_NAME"]], on="PARTY_ID", how="left").rename(
            columns={"PARTY_NAME": "VENDOR_NAME"})
    supplier_no = dict(zip(sup["VENDOR_ID"], sup["SEGMENT1"]))

    # Purchase orders: approved standard orders, not cancelled; lines not cancelled.
    head = read(src, "PO_HEADERS_ALL", required=["PO_HEADER_ID", "SEGMENT1", "VENDOR_ID", "CREATION_DATE"])
    keep = pd.Series(True, index=head.index)
    if "AUTHORIZATION_STATUS" in head:
        keep &= head["AUTHORIZATION_STATUS"].str.upper().isin(APPROVED)
    if "CANCEL_FLAG" in head:
        keep &= head["CANCEL_FLAG"].str.upper().ne("Y")
    if "TYPE_LOOKUP_CODE" in head:
        other = ~head["TYPE_LOOKUP_CODE"].str.upper().isin(KEEP_TYPES)
        if other.any():
            notes.append(f"{int(other.sum())} blanket, contract or planned agreements left out (only STANDARD orders)")
        keep &= ~other
    notes.append(f"PO_HEADERS_ALL: {len(head)} documents, {int((~keep).sum())} dropped (not approved, cancelled, or not STANDARD)")
    po_number_all = dict(zip(head["PO_HEADER_ID"], head["SEGMENT1"]))
    head = head[keep]
    lines = read(src, "PO_LINES_ALL", required=["PO_HEADER_ID", "QUANTITY", "UNIT_PRICE"])
    if "CANCEL_FLAG" in lines:
        cancelled = lines["CANCEL_FLAG"].str.upper().eq("Y")
        notes.append(f"PO_LINES_ALL: {len(lines)} lines, {int(cancelled.sum())} cancelled lines dropped")
        lines = lines[~cancelled]
    lines = lines.merge(head, on="PO_HEADER_ID", how="inner", suffixes=("", "_H"))

    cats = read(src, "MTL_CATEGORIES_KFV", "MTL_CATEGORIES_TL", "EGP_CATEGORIES_TL", "CATEGORIES",
                required=["CATEGORY_ID"], optional=True)
    names = {}
    if cats is not None:
        col = next((c for c in ("CONCATENATED_SEGMENTS", "CATEGORY_NAME", "DESCRIPTION") if c in cats.columns), None)
        if col:
            names = {i: n.split(".")[-1].strip() for i, n in zip(cats["CATEGORY_ID"], cats[col])}
    if not names:
        warn.append("No category names (MTL_CATEGORIES_KFV or EGP_CATEGORIES_TL): categories are IDs")

    # Approvals: PO_ACTION_HISTORY; last APPROVE is the approver, first SUBMIT the requester.
    hist = read(src, "PO_ACTION_HISTORY", required=["OBJECT_ID", "ACTION_CODE", "EMPLOYEE_ID", "ACTION_DATE"],
                optional=True)
    approver, approved, submitter = {}, {}, {}
    if hist is not None:
        if "OBJECT_TYPE_CODE" in hist:
            hist = hist[hist["OBJECT_TYPE_CODE"].str.upper().isin({"PO", ""})]
        hist = hist.assign(_t=hist["ACTION_DATE"].map(lambda v: when(v, offset)),
                           _s=pd.to_numeric(hist.get("SEQUENCE_NUM", 0), errors="coerce")).sort_values(["_t", "_s"])
        for _, h in hist[hist["ACTION_CODE"].str.upper().isin(APPROVE_ACTIONS)].iterrows():
            approver[h["OBJECT_ID"]], approved[h["OBJECT_ID"]] = h["EMPLOYEE_ID"], h["_t"]
        for _, h in hist[hist["ACTION_CODE"].str.upper().isin(SUBMIT_ACTIONS)].iloc[::-1].iterrows():
            submitter[h["OBJECT_ID"]] = h["EMPLOYEE_ID"]
        notes.append(f"PO_ACTION_HISTORY: approvals found for {len(set(approver) & set(head['PO_HEADER_ID']))} POs")
        if any(pd.notna(t) and t.hour == 0 and t.minute == 0 for t in approved.values()):
            warn.append("Some ACTION_DATE values have no time: export with NLS_DATE_FORMAT = 'YYYY-MM-DD HH24:MI:SS' "
                        "or the night and weekend approval check is unreliable")
    else:
        warn.append("No PO_ACTION_HISTORY: approver unknown; approval time taken from APPROVED_DATE (often date only)")
    hid = lines["PO_HEADER_ID"]
    fallback_time = lines["APPROVED_DATE"].map(lambda v: when(v, offset)) if "APPROVED_DATE" in lines else pd.NaT
    approved_at = hid.map(approved)
    if isinstance(fallback_time, pd.Series):
        # APPROVED_DATE is often exported without a time; midnight would look like a night approval.
        # Judge "has a time" on the raw value: after the UTC shift, midnight would look like 03:00.
        raw = lines["APPROVED_DATE"].map(when)
        timed = raw.map(lambda t: pd.notna(t) and (t.hour, t.minute, t.second) != (0, 0, 0))
        missing = approved_at.isna()
        approved_at = approved_at.where(~missing, fallback_time.where(timed))
        if (missing & ~timed).any():
            warn.append(f"{hid[missing & ~timed].nunique()} POs have an approval date but no time: approval "
                        "time left empty, so the night and weekend check skips them")
    requester = hid.map(submitter)
    if "AGENT_ID" in lines:
        requester = requester.where(requester.notna(), lines["AGENT_ID"])

    has_item = "ITEM_ID" in lines.columns
    first_text = lines.drop_duplicates("ITEM_ID").set_index("ITEM_ID")["ITEM_DESCRIPTION"].to_dict() \
        if has_item and "ITEM_DESCRIPTION" in lines else {}
    item = [f"{i} {first_text.get(i, '')}".strip() if has_item and i else d
            for i, d in zip(lines["ITEM_ID"] if has_item else [""] * len(lines), lines.get("ITEM_DESCRIPTION", ""))]
    qty, price = lines["QUANTITY"].map(num), lines["UNIT_PRICE"].map(num)
    amount = lines["AMOUNT"].map(num) if "AMOUNT" in lines else pd.Series(float("nan"), index=lines.index)
    amount = amount.where(amount.notna(), qty * price).round(2)
    po = pd.DataFrame({
        "po_id": lines["SEGMENT1"],
        "po_date": lines["CREATION_DATE"].map(lambda v: when(v, offset)).dt.strftime("%Y-%m-%d"),
        "vendor_id": lines["VENDOR_ID"].map(supplier_no).fillna(lines["VENDOR_ID"]),
        "category": lines["CATEGORY_ID"].map(lambda c: names.get(c, c or "Uncategorised")) if "CATEGORY_ID" in lines else "Uncategorised",
        "item": item,
        "quantity": qty,
        "unit_price": price.where(price.notna(), amount / qty),
        "amount": amount,
        "requester_id": requester.fillna(""),
        "approver_id": hid.map(approver).fillna(""),
        "approved_at": pd.to_datetime(approved_at).dt.strftime("%Y-%m-%d %H:%M").fillna(""),
    })

    # Invoices: standard invoices, not cancelled; PO from invoice lines; paid date from non-void payments.
    invs = read(src, "AP_INVOICES_ALL", required=["INVOICE_ID", "INVOICE_NUM", "VENDOR_ID", "INVOICE_DATE", "INVOICE_AMOUNT"])
    keep = pd.Series(True, index=invs.index)
    if "INVOICE_TYPE_LOOKUP_CODE" in invs:
        keep &= invs["INVOICE_TYPE_LOOKUP_CODE"].str.upper().isin(INVOICE_TYPES)
    if "CANCELLED_DATE" in invs:
        keep &= invs["CANCELLED_DATE"].eq("")
    notes.append(f"AP_INVOICES_ALL: {len(invs)} invoices, {int((~keep).sum())} dropped (cancelled, credit or debit memos)")
    invs = invs[keep]
    ilines = read(src, "AP_INVOICE_LINES_ALL", required=["INVOICE_ID", "PO_HEADER_ID"], optional=True)
    po_of = {}
    if ilines is not None:
        for i, h in zip(ilines["INVOICE_ID"], ilines["PO_HEADER_ID"]):
            if h and i not in po_of:
                po_of[i] = po_number_all.get(h, "")
    if "PO_HEADER_ID" in invs:
        for i, h in zip(invs["INVOICE_ID"], invs["PO_HEADER_ID"]):
            if h and not po_of.get(i):
                po_of[i] = po_number_all.get(h, "")
    ipay = read(src, "AP_INVOICE_PAYMENTS_ALL", required=["INVOICE_ID", "CHECK_ID"], optional=True)
    chk = read(src, "AP_CHECKS_ALL", required=["CHECK_ID", "CHECK_DATE"], optional=True)
    paid = {}
    if ipay is not None and chk is not None:
        good = chk
        if "VOID_DATE" in chk:
            good = good[good["VOID_DATE"].eq("")]
        if "STATUS_LOOKUP_CODE" in chk:
            good = good[~good["STATUS_LOOKUP_CODE"].str.upper().isin({"VOIDED", "SPOILED", "OVERFLOW"})]
        if "REVERSAL_FLAG" in ipay:
            ipay = ipay[ipay["REVERSAL_FLAG"].str.upper().ne("Y")]
        pairs = ipay.merge(good[["CHECK_ID", "CHECK_DATE"]], on="CHECK_ID")
        for i, d in zip(pairs["INVOICE_ID"], pairs["CHECK_DATE"]):
            t = when(d)
            if pd.notna(t) and (i not in paid or t > paid[i]):
                paid[i] = t
        notes.append(f"Payments: {len(chk) - len(good)} voided or spoiled payments ignored")
    else:
        warn.append("No AP_INVOICE_PAYMENTS_ALL and AP_CHECKS_ALL: payment dates empty; rule R5 cannot run")
    invoices = pd.DataFrame({
        "invoice_no": invs["INVOICE_NUM"],
        "vendor_id": invs["VENDOR_ID"].map(supplier_no).fillna(invs["VENDOR_ID"]),
        "po_id": invs["INVOICE_ID"].map(po_of).fillna(""),
        "invoice_date": invs["INVOICE_DATE"].map(when).dt.strftime("%Y-%m-%d"),
        "amount": invs["INVOICE_AMOUNT"].map(num),
        "paid_date": invs["INVOICE_ID"].map(paid).dt.strftime("%Y-%m-%d").fillna("") if paid else "",
    })

    # Vendors with bank accounts from the flattened query (several accounts joined by ";").
    banks = read(src, "SUPPLIER_BANK_ACCOUNTS", required=["VENDOR_ID"], optional=True)
    ibans = {}
    if banks is not None:
        col = "IBAN" if "IBAN" in banks.columns else "BANK_ACCOUNT_NUM"
        for v, g in banks.groupby("VENDOR_ID"):
            ibans[v] = ";".join(sorted({x for x in g[col] if x}))
    else:
        warn.append("No SUPPLIER_BANK_ACCOUNTS: rule R4 (vendor bank = staff bank) cannot run")
    vendors = pd.DataFrame({
        "vendor_id": sup["SEGMENT1"],
        "name": sup["VENDOR_NAME"],
        "cr_number": sup.get("VAT_REGISTRATION_NUM", ""),
        "bank_iban": sup["VENDOR_ID"].map(ibans).fillna(""),
        "created_date": sup.get("CREATION_DATE", pd.Series("", index=sup.index)).map(when).dt.strftime("%Y-%m-%d").fillna(""),
    })

    people = read(src, "STAFF_BANK_ACCOUNTS", required=["PERSON_ID"], optional=True)
    staff = None
    if people is not None:
        col = "IBAN" if "IBAN" in people.columns else "BANK_ACCOUNT_NUM"
        staff = pd.DataFrame({"staff_id": people["PERSON_ID"], "name": "", "department": "",
                              "bank_iban": people[col]}).query("bank_iban != ''").drop_duplicates()
    else:
        warn.append("No STAFF_BANK_ACCOUNTS: staff accounts not compared (rule R4 skipped)")

    limits_src = src / "approval_limits.csv"
    if limits_src.exists():
        limits = limits_src.read_text(encoding="utf-8")
    else:
        warn.append("No approval_limits.csv: wrote placeholder limits 10,000 / 50,000 / 250,000; replace them "
                    "with the company's approval hierarchy amounts (EBS approval groups, Fusion approval rules)")
        limits = "level,limit_sar\nLevel 1,10000\nLevel 2,50000\nLevel 3,250000\n"

    out.mkdir(parents=True, exist_ok=True)
    po.to_csv(out / "purchase_orders.csv", index=False)
    invoices.to_csv(out / "invoices.csv", index=False)
    vendors.to_csv(out / "vendors.csv", index=False)
    if staff is not None:
        staff.to_csv(out / "staff.csv", index=False)
    (out / "approval_limits.csv").write_text(limits, encoding="utf-8")
    if (src / "answer_key.csv").exists():  # test data only
        (out / "answer_key.csv").write_text((src / "answer_key.csv").read_text(encoding="utf-8"), encoding="utf-8")

    report = [f"Mapped {src} ({sup_table}) -> {out}, datetimes shifted by {offset_hours:+g} h",
              f"purchase_orders.csv: {len(po)} lines on {po['po_id'].nunique()} POs",
              f"invoices.csv: {len(invoices)}", f"vendors.csv: {len(vendors)}",
              f"staff.csv: {0 if staff is None else len(staff)}", "", "Notes:", *[f"- {n}" for n in notes],
              "", "Warnings:", *([f"- {w}" for w in warn] or ["- none"])]
    (out / "mapping_report.txt").write_text("\n".join(report) + "\n", encoding="utf-8")
    print("\n".join(report))


if __name__ == "__main__":
    if len(sys.argv) not in (3, 4):
        raise SystemExit(__doc__)
    run(Path(sys.argv[1]), Path(sys.argv[2]), float(sys.argv[3]) if len(sys.argv) == 4 else 0.0)
