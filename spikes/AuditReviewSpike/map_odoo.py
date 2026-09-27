"""Map Odoo (16, 17, 18) exports to the review input format.

Usage:
    python map_odoo.py <odoo_folder> <out_folder> [utc_offset_hours]
    python review.py <out_folder> <report_folder>

Reads the files Odoo's list-view Export produces, named after the model (purchase.order.xlsx,
purchase.order.line.xlsx, account.move.xlsx, ...; .csv also works). Accepts English field labels
or technical names ("Import-compatible export"). Export with the user language set to English.
Odoo exports datetimes in UTC; they are shifted by utc_offset_hours (default 3, Riyadh) so the
approval-time rule sees local time. See README.md for which fields to export.
"""
import re
import sys
from pathlib import Path

import pandas as pd

# Field labels (English UI) and technical names, per model, mapped to short internal names.
FIELDS = {
    "purchase.order": {
        "id": ["ID", "id", ".id"], "ref": ["Order Reference", "Reference", "name"],
        "vendor": ["Vendor", "Partner", "partner_id"], "ordered": ["Order Date", "Order Deadline", "date_order"],
        "confirmed": ["Confirmation Date", "date_approve"], "creator": ["Created by", "create_uid"],
        "buyer": ["Buyer", "Purchase Representative", "Representative", "user_id"], "state": ["Status", "state"],
    },
    "purchase.order.line": {
        "ref": ["Order Reference", "Order", "order_id"], "product": ["Product", "product_id"],
        "category": ["Product/Product Category", "Product Category", "product_id/categ_id"],
        "text": ["Description", "name"], "qty": ["Quantity", "product_qty"],
        "price": ["Unit Price", "price_unit"], "subtotal": ["Subtotal", "price_subtotal"],
    },
    "account.move": {
        "number": ["Number", "name"], "vendor": ["Vendor", "Partner", "partner_id"],
        "ref": ["Bill Reference", "Reference", "ref"], "date": ["Bill Date", "Invoice/Bill Date", "invoice_date"],
        "origin": ["Source Document", "Source", "invoice_origin"],
        "total": ["Total Signed", "Total in Currency Signed", "Total", "amount_total_signed", "amount_total"],
        "state": ["Status", "state"], "paystate": ["Payment Status", "payment_state"], "type": ["Type", "move_type"],
    },
    "account.payment": {
        "date": ["Date", "date"], "state": ["Status", "state"],
        "bills": ["Reconciled Bills", "reconciled_bill_ids"],
    },
    "res.partner": {
        "name": ["Display Name", "Name", "display_name", "name"], "cr": ["Company ID", "company_registry"],
        "vat": ["Tax ID", "vat"], "created": ["Created on", "create_date"],
        "bank": ["Bank Accounts/Account Number", "bank_ids/acc_number", "Account Number"],
    },
    "hr.employee": {
        "id": ["Badge ID", "barcode", "ID", "id"], "name": ["Employee Name", "Name", "name"],
        "bank": ["Bank Account Number", "Bank Account Number/Account Number", "bank_account_id/acc_number",
                 "bank_account_id"],
    },
    "mail.tracking.value": {
        "model": ["Message/Related Document Model", "mail_message_id/model"],
        "res_id": ["Message/Related Document ID", "mail_message_id/res_id"],
        "field": ["Field", "field_id", "field"], "new": ["New Value Char", "new_value_char"],
        "when": ["Created on", "create_date"], "who": ["Created by", "create_uid"],
    },
}
CONFIRMED = {"purchase order", "purchase", "locked", "done"}       # PO states that count
POSTED = {"posted"}
DROP_PAYMENT = {"reversed", "invoicing app legacy"}
BILL_TYPES = {"vendor bill", "in_invoice"}
DEAD_PAYMENTS = {"draft", "cancelled", "cancel", "rejected"}


def read(folder, model, required, optional=False, fill=()):
    for ext in (".xlsx", ".xls", ".csv"):
        f = folder / f"{model}{ext}"
        if f.exists():
            df = pd.read_excel(f, dtype=str) if ext.startswith(".xls") else pd.read_csv(f, dtype=str, encoding="utf-8-sig")
            df.columns = [str(c).strip() for c in df.columns]
            names = {}
            for short, labels in FIELDS[model].items():
                hit = next((c for c in labels if c in df.columns), None)
                if hit:
                    names[hit] = short
            df = df.rename(columns=names)
            df = df.apply(lambda c: c.str.strip() if c.dtype == object else c)
            missing = [c for c in required if c not in df.columns]
            if missing:
                wanted = {m: FIELDS[model][m][:2] for m in missing}
                raise SystemExit(f"{f.name}: missing fields {wanted}; found {list(df.columns)}")
            # One2many fields export as extra rows with the parent columns blank: fill them down.
            cols = [c for c in fill if c in df.columns]
            if cols:
                df[cols] = df[cols].replace("", pd.NA).ffill()
            return df.fillna("")
    if optional:
        return None
    raise SystemExit(f"Missing {model}.xlsx or {model}.csv in {folder}")


def num(v):
    s = str(v).strip().replace(" ", "").replace(" ", "")
    if s in ("", "nan"):
        return float("nan")
    s = re.sub(r"[^\d.,\-]", "", s)               # drop currency symbols
    if re.fullmatch(r"-?[\d.]*,\d{1,2}", s):
        s = s.replace(".", "").replace(",", ".")
    else:
        s = s.replace(",", "")
    return float(s)


def when(v, offset=None):
    t = pd.to_datetime(str(v), errors="coerce")
    return t + offset if offset is not None and pd.notna(t) else t


def run(src, out, offset_hours=3.0):
    offset = pd.Timedelta(hours=offset_hours)
    notes, warn = [], []

    head = read(src, "purchase.order", ["ref", "vendor", "ordered", "state"])
    confirmed = head["state"].str.lower().isin(CONFIRMED)
    notes.append(f"purchase.order: {len(head)} orders, {int((~confirmed).sum())} not confirmed "
                 f"(RFQ, to approve, cancelled) dropped")
    head = head[confirmed]
    lines = read(src, "purchase.order.line", ["ref", "qty", "price", "subtotal"], fill=["ref"])
    lines = lines.merge(head, on="ref", how="inner", suffixes=("", "_h"))

    # Approver: who moved the PO to "Purchase Order" in the chatter; otherwise only the time is known.
    track = read(src, "mail.tracking.value", ["res_id", "field", "new", "when", "who"], optional=True)
    approvals = pd.DataFrame(columns=["ref", "approver", "approved"])
    if track is not None and "id" in head.columns:
        t = track[(track.get("model", "purchase.order") == "purchase.order")
                  & track["field"].str.lower().isin({"status", "state"})
                  & track["new"].str.lower().isin(CONFIRMED)]
        t = t.assign(approved=t["when"].map(lambda v: when(v, offset))).sort_values("approved")
        t = t.groupby("res_id", as_index=False).last()
        approvals = t.merge(head[["id", "ref"]], left_on="res_id", right_on="id")[["ref", "who", "approved"]]
        approvals = approvals.rename(columns={"who": "approver"})
        notes.append(f"mail.tracking.value: confirmations found for {len(approvals)} POs")
    else:
        warn.append("No mail.tracking.value (or no ID column in purchase.order): approver unknown, "
                    "so 'requester approved own PO' cannot run; approval time taken from Confirmation Date")
    lines = lines.merge(approvals, on="ref", how="left")
    if "confirmed" in lines.columns:
        fallback = lines["confirmed"].map(lambda v: when(v, offset))
        lines["approved"] = lines["approved"].where(lines["approved"].notna(), fallback)
    requester = lines["creator"] if "creator" in lines.columns else lines.get("buyer", "")
    if "creator" not in lines.columns:
        warn.append("No 'Created by' on purchase.order: requester taken from Buyer")

    has_product = "product" in lines.columns
    item = [p if has_product and p else t for p, t in zip(lines["product"] if has_product else [""] * len(lines),
                                                          lines.get("text", [""] * len(lines)))]
    category = lines["category"].map(lambda c: c.split("/")[-1].strip() if c else "Uncategorised") \
        if "category" in lines.columns else "Uncategorised"
    if "category" not in lines.columns:
        warn.append("No 'Product/Product Category' on purchase.order.line: rule R2 sees one category")
    po = pd.DataFrame({
        "po_id": lines["ref"],
        "po_date": lines["ordered"].map(lambda v: when(v, offset)).dt.strftime("%Y-%m-%d"),
        "vendor_id": lines["vendor"],
        "category": category,
        "item": item,
        "quantity": lines["qty"].map(num),
        "unit_price": lines["price"].map(num),
        "amount": lines["subtotal"].map(num),
        "requester_id": requester,
        "approver_id": lines["approver"].fillna(""),
        "approved_at": pd.to_datetime(lines["approved"]).dt.strftime("%Y-%m-%d %H:%M").fillna(""),
    })

    # Vendor bills: posted, not reversed, bills only (no credit notes); payment date from account.payment.
    moves = read(src, "account.move", ["number", "vendor", "date", "total"])
    keep = pd.Series(True, index=moves.index)
    if "type" in moves:
        keep &= moves["type"].str.lower().isin(BILL_TYPES)
    if "state" in moves:
        keep &= moves["state"].str.lower().isin(POSTED)
    if "paystate" in moves:
        keep &= ~moves["paystate"].str.lower().isin(DROP_PAYMENT)
    notes.append(f"account.move: {len(moves)} rows, {int((~keep).sum())} dropped (not posted, reversed, or credit notes)")
    moves = moves[keep]
    pay = read(src, "account.payment", ["date", "bills"], optional=True, fill=["date", "state"])
    paid = {}
    if pay is not None:
        if "state" in pay:
            pay = pay[~pay["state"].str.lower().isin(DEAD_PAYMENTS)]
        for d, bills in zip(pay["date"], pay["bills"]):
            for b in re.split(r",\s*", bills):
                if b:
                    paid[b] = max(paid.get(b, ""), d)
    else:
        warn.append("No account.payment: payment dates empty; rule R5 (new vendor paid fast) cannot run")
    invoices = pd.DataFrame({
        "invoice_no": [r or n for r, n in zip(moves.get("ref", moves["number"]), moves["number"])],
        "vendor_id": moves["vendor"],
        "po_id": moves.get("origin", "").map(lambda o: re.split(r",\s*", o)[0] if o else ""),
        "invoice_date": moves["date"].map(when).dt.strftime("%Y-%m-%d"),
        "amount": moves["total"].map(num).abs(),
        "paid_date": moves["number"].map(paid).fillna("").map(when).dt.strftime("%Y-%m-%d").fillna(""),
    })
    unpaid = invoices["paid_date"].eq("").sum()
    if unpaid:
        notes.append(f"{unpaid} bills without a payment date")

    # Vendors: res.partner, several bank accounts as continuation rows joined by ";".
    partners = read(src, "res.partner", ["name"], fill=["name", "cr", "vat", "created"])
    used = set(po["vendor_id"]) | set(invoices["vendor_id"])
    rows = []
    for name, g in partners.groupby("name", sort=False):
        if name not in used:
            continue
        first = g.iloc[0]
        banks = sorted({b for b in g.get("bank", pd.Series(dtype=str)) if b})
        rows.append({"vendor_id": name, "name": name, "cr_number": first.get("cr", "") or first.get("vat", ""),
                     "bank_iban": ";".join(banks),
                     "created_date": when(first.get("created", "")).strftime("%Y-%m-%d")
                     if pd.notna(when(first.get("created", ""))) else ""})
    vendors = pd.DataFrame(rows, columns=["vendor_id", "name", "cr_number", "bank_iban", "created_date"])
    missing_vendors = used - set(vendors["vendor_id"])
    if missing_vendors:
        warn.append(f"{len(missing_vendors)} vendors on POs or bills are not in res.partner export "
                    f"(for example {sorted(missing_vendors)[0]}); names must match exactly")
    if "bank" not in partners.columns:
        warn.append("No 'Bank Accounts/Account Number' on res.partner: rule R4 cannot run")

    emp = read(src, "hr.employee", ["bank"], optional=True, fill=["id", "name"])
    staff = None
    if emp is not None:
        sid = emp["id"] if "id" in emp.columns else emp["name"]
        staff = pd.DataFrame({"staff_id": sid, "name": "", "department": "", "bank_iban": emp["bank"]})
        staff = staff[staff["bank_iban"] != ""].drop_duplicates()
    else:
        warn.append("No hr.employee with bank accounts: staff accounts not compared (rule R4 skipped)")

    limits_src = src / "approval_limits.csv"
    if limits_src.exists():
        limits = limits_src.read_text(encoding="utf-8")
    else:
        warn.append("No approval_limits.csv: wrote placeholder limits 10,000 / 50,000 / 250,000; replace them "
                    "with the company's authority matrix (Odoo's purchase double validation amount is one of them)")
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

    report = [f"Mapped {src} -> {out} (datetimes shifted by {offset_hours:+g} h from UTC)",
              f"purchase_orders.csv: {len(po)} lines on {po['po_id'].nunique()} POs",
              f"invoices.csv: {len(invoices)}", f"vendors.csv: {len(vendors)}",
              f"staff.csv: {0 if staff is None else len(staff)}", "", "Notes:", *[f"- {n}" for n in notes],
              "", "Warnings:", *([f"- {w}" for w in warn] or ["- none"])]
    (out / "mapping_report.txt").write_text("\n".join(report) + "\n", encoding="utf-8")
    print("\n".join(report))


if __name__ == "__main__":
    if len(sys.argv) not in (3, 4):
        raise SystemExit(__doc__)
    run(Path(sys.argv[1]), Path(sys.argv[2]), float(sys.argv[3]) if len(sys.argv) == 4 else 3.0)
