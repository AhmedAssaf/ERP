"""Turn the fictional sample into a realistic Oracle EBS (R12) table export, to test map_oracle.py.

Writes oracle_sample/ as CSV files named after the tables, the way SQL Developer exports query
results: upper-case column names, internal IDs (PO_HEADER_ID, VENDOR_ID) next to the numbers people
see (SEGMENT1), DD-MON-RR dates, and approval history as PO_ACTION_HISTORY rows (SUBMIT, FORWARD,
APPROVE). Includes an incomplete PO, a cancelled PO, a cancelled line, a cancelled invoice, a
credit memo, and a voided payment re-issued later. The answer key is translated to Oracle numbers.
"""
from pathlib import Path

import pandas as pd

HERE = Path(__file__).parent
SRC, OUT = HERE / "sample", HERE / "oracle_sample"

pos = pd.read_csv(SRC / "purchase_orders.csv", dtype=str)
inv = pd.read_csv(SRC / "invoices.csv", dtype=str)
ven = pd.read_csv(SRC / "vendors.csv", dtype=str)
staff = pd.read_csv(SRC / "staff.csv", dtype=str)
key = pd.read_csv(SRC / "answer_key.csv", dtype=str)
OUT.mkdir(exist_ok=True)

rr = lambda iso: pd.Timestamp(iso).strftime("%d-%b-%y").upper()        # 02-JUL-26
ts = lambda iso: pd.Timestamp(iso).strftime("%Y-%m-%d %H:%M:%S")      # NLS set as the README asks
header_id = {p: 110000 + i for i, p in enumerate(pos["po_id"])}
segment1 = {p: str(20000 + int(p.split("-")[1])) for p in pos["po_id"]}
vendor_id = {v: 3000 + int(v[1:]) for v in ven["vendor_id"]}
supplier_no = {v: f"S{int(v[1:]):05d}" for v in ven["vendor_id"]}
person = lambda staff_id: 5000 + int(staff_id[1:])
cats = {c: 400 + i for i, c in enumerate(sorted(pos["category"].unique()))}
items = {it: 700000 + i for i, it in enumerate(sorted(pos["item"].unique()))}

headers, lines, history = [], [], []
line_id, seq_split = 900000, False
for i, p in pos.iterrows():
    h = header_id[p["po_id"]]
    t = pd.Timestamp(p["approved_at"])
    headers.append({"PO_HEADER_ID": h, "SEGMENT1": segment1[p["po_id"]], "TYPE_LOOKUP_CODE": "STANDARD",
                    "VENDOR_ID": vendor_id[p["vendor_id"]], "AGENT_ID": person(p["requester_id"]),
                    "CREATION_DATE": rr(p["po_date"]), "AUTHORIZATION_STATUS": "APPROVED",
                    "APPROVED_DATE": rr(t), "CANCEL_FLAG": "N"})
    qty, price = float(p["quantity"]), float(p["unit_price"])
    parts = [qty]
    if not seq_split and qty >= 10 and p["category"] == "Catering":
        parts = [qty // 2, qty - qty // 2]
        seq_split = True
    for n, q in enumerate(parts, 1):
        line_id += 1
        lines.append({"PO_LINE_ID": line_id, "PO_HEADER_ID": h, "LINE_NUM": n, "ITEM_ID": items[p["item"]],
                      "ITEM_DESCRIPTION": p["item"], "CATEGORY_ID": cats[p["category"]], "QUANTITY": q,
                      "UNIT_PRICE": price, "AMOUNT": "", "CANCEL_FLAG": "N"})
    if i % 90 == 0:  # a cancelled extra line that must not count
        line_id += 1
        lines.append({"PO_LINE_ID": line_id, "PO_HEADER_ID": h, "LINE_NUM": 9, "ITEM_ID": items[p["item"]],
                      "ITEM_DESCRIPTION": p["item"], "CATEGORY_ID": cats[p["category"]], "QUANTITY": 1000,
                      "UNIT_PRICE": price * 5, "AMOUNT": "", "CANCEL_FLAG": "Y"})
    submitted = min(t - pd.Timedelta(hours=2), pd.Timestamp(p["po_date"] + " 09:00"))
    history.append({"OBJECT_ID": h, "OBJECT_TYPE_CODE": "PO", "SEQUENCE_NUM": 0, "ACTION_CODE": "SUBMIT",
                    "EMPLOYEE_ID": person(p["requester_id"]), "ACTION_DATE": ts(submitted)})
    if i % 7 == 0:  # forwarded to someone else first; only the last APPROVE counts
        history.append({"OBJECT_ID": h, "OBJECT_TYPE_CODE": "PO", "SEQUENCE_NUM": 1, "ACTION_CODE": "FORWARD",
                        "EMPLOYEE_ID": 5999, "ACTION_DATE": ts(submitted + pd.Timedelta(minutes=30))})
    history.append({"OBJECT_ID": h, "OBJECT_TYPE_CODE": "PO", "SEQUENCE_NUM": 2, "ACTION_CODE": "APPROVE",
                    "EMPLOYEE_ID": person(p["approver_id"]), "ACTION_DATE": ts(t)})
for extra, status, cancel in ((199998, "INCOMPLETE", "N"), (199999, "APPROVED", "Y")):
    headers.append({"PO_HEADER_ID": extra, "SEGMENT1": str(extra), "TYPE_LOOKUP_CODE": "STANDARD",
                    "VENDOR_ID": vendor_id["V001"], "AGENT_ID": 5001, "CREATION_DATE": rr("2026-08-03"),
                    "AUTHORIZATION_STATUS": status, "APPROVED_DATE": "", "CANCEL_FLAG": cancel})
    lines.append({"PO_LINE_ID": extra, "PO_HEADER_ID": extra, "LINE_NUM": 1, "ITEM_ID": 799999,
                  "ITEM_DESCRIPTION": "Not a real order", "CATEGORY_ID": 400, "QUANTITY": 1, "UNIT_PRICE": 88888,
                  "AMOUNT": "", "CANCEL_FLAG": "N"})

invoices, inv_lines, inv_pay, checks = [], [], [], []
for i, v in inv.reset_index(drop=True).iterrows():
    iid, cid = 600000 + i, 800000 + i
    invoices.append({"INVOICE_ID": iid, "INVOICE_NUM": v["invoice_no"], "VENDOR_ID": vendor_id[v["vendor_id"]],
                     "INVOICE_DATE": rr(v["invoice_date"]), "INVOICE_AMOUNT": float(v["amount"]),
                     "INVOICE_TYPE_LOOKUP_CODE": "STANDARD", "CANCELLED_DATE": ""})
    inv_lines.append({"INVOICE_ID": iid, "LINE_NUMBER": 1, "PO_HEADER_ID": header_id[v["po_id"]]})
    inv_pay.append({"INVOICE_ID": iid, "CHECK_ID": cid, "REVERSAL_FLAG": "N"})
    checks.append({"CHECK_ID": cid, "CHECK_DATE": rr(v["paid_date"]), "STATUS_LOOKUP_CODE": "NEGOTIABLE", "VOID_DATE": ""})
    if i == 5:  # an earlier payment that was voided; the paid date is the valid one
        inv_pay.append({"INVOICE_ID": iid, "CHECK_ID": 899999, "REVERSAL_FLAG": "N"})
        checks.append({"CHECK_ID": 899999, "CHECK_DATE": rr(pd.Timestamp(v["paid_date"]) + pd.Timedelta(days=9)),
                       "STATUS_LOOKUP_CODE": "VOIDED", "VOID_DATE": rr(pd.Timestamp(v["paid_date"]) + pd.Timedelta(days=10))})
base = invoices[0]
invoices.append(base | {"INVOICE_ID": 699998, "INVOICE_NUM": "CXL-1", "CANCELLED_DATE": rr("2026-08-10")})
invoices.append(base | {"INVOICE_ID": 699999, "INVOICE_NUM": "CM-1", "INVOICE_AMOUNT": -500.0,
                        "INVOICE_TYPE_LOOKUP_CODE": "CREDIT"})

suppliers = [{"VENDOR_ID": vendor_id[v["vendor_id"]], "SEGMENT1": supplier_no[v["vendor_id"]], "VENDOR_NAME": v["name"],
              "VAT_REGISTRATION_NUM": "3" + v["cr_number"] + "00003", "CREATION_DATE": rr(v["created_date"])}
             for _, v in ven.iterrows()]
banks = [{"VENDOR_ID": vendor_id[v["vendor_id"]], "IBAN": v["bank_iban"]} for _, v in ven.iterrows()]
people = [{"PERSON_ID": person(s["staff_id"]), "IBAN": s["bank_iban"]} for _, s in staff.iterrows()]

for name, rows in (("PO_HEADERS_ALL", headers), ("PO_LINES_ALL", lines), ("PO_ACTION_HISTORY", history),
                   ("MTL_CATEGORIES_KFV", [{"CATEGORY_ID": c, "CONCATENATED_SEGMENTS": f"PURCHASING.{n}"} for n, c in cats.items()]),
                   ("AP_SUPPLIERS", suppliers), ("SUPPLIER_BANK_ACCOUNTS", banks), ("AP_INVOICES_ALL", invoices),
                   ("AP_INVOICE_LINES_ALL", inv_lines), ("AP_INVOICE_PAYMENTS_ALL", inv_pay), ("AP_CHECKS_ALL", checks),
                   ("STAFF_BANK_ACCOUNTS", people)):
    pd.DataFrame(rows).to_csv(OUT / f"{name}.csv", index=False)
(OUT / "approval_limits.csv").write_text((SRC / "approval_limits.csv").read_text(encoding="utf-8"), encoding="utf-8")

vname_to_no = supplier_no
key["reference"] = key["reference"].map(
    lambda r: ";".join(segment1.get(x) or vname_to_no.get(x) or x for x in r.split(";")))
key.to_csv(OUT / "answer_key.csv", index=False)
print(f"Wrote Oracle-style export to {OUT}: {len(headers)} POs (1 incomplete, 1 cancelled), {len(lines)} lines, "
      f"{len(invoices)} invoices (1 cancelled, 1 credit memo), {len(checks)} payments (1 voided)")
