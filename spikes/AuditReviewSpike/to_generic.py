"""Turn the fictional sample into realistic NetSuite, ERPNext and Zoho Books exports, to test map_generic.py.

Writes netsuite_sample/, erpnext_sample/ and zoho_books_sample/ in the shapes the profiles in profiles/
expect, with each product's quirks: NetSuite US dates, thousands separators, "Purchase Order #..." links and
System Notes approvals; ERPNext child-table columns with blank parent cells on continuation rows; Zoho Books
header repeated on every line and one payment applied to two bills. Each includes rows the profile's
filters must drop (unapproved or draft orders, cancelled or void bills). Answer keys are translated.
"""
from pathlib import Path

import pandas as pd

HERE = Path(__file__).parent
SRC = HERE / "sample"
pos = pd.read_csv(SRC / "purchase_orders.csv", dtype=str)
inv = pd.read_csv(SRC / "invoices.csv", dtype=str).reset_index(drop=True)
ven = pd.read_csv(SRC / "vendors.csv", dtype=str)
staff = pd.read_csv(SRC / "staff.csv", dtype=str)
key = pd.read_csv(SRC / "answer_key.csv", dtype=str)
limits = (SRC / "approval_limits.csv").read_text(encoding="utf-8")
vname = dict(zip(ven["vendor_id"], ven["name"]))
n_of = lambda p: int(p.split("-")[1])


def finish(out, po_map, rows_by_file):
    out.mkdir(exist_ok=True)
    for name, rows in rows_by_file.items():
        pd.DataFrame(rows).to_csv(out / f"{name}.csv", index=False)
    k = key.copy()
    k["reference"] = k["reference"].map(lambda r: ";".join(po_map.get(x) or vname.get(x) or x for x in r.split(";")))
    k.to_csv(out / "answer_key.csv", index=False)
    (out / "approval_limits.csv").write_text(limits, encoding="utf-8")


# ---------- NetSuite ----------
us = lambda d: f"{pd.Timestamp(d).month}/{pd.Timestamp(d).day}/{pd.Timestamp(d).year}"
us_t = lambda t: f"{us(t)} {pd.Timestamp(t).strftime('%I:%M %p').lstrip('0').lower()}"
money = lambda x: f"{float(x):,.2f}"
ns_po = {p: f"PO{n_of(p)}" for p in pos["po_id"]}
lines, approvals = [], []
for _, p in pos.iterrows():
    lines.append({"Document Number": ns_po[p["po_id"]], "Date": us(p["po_date"]), "Name": vname[p["vendor_id"]],
                  "Item": p["item"], "Class": p["category"], "Quantity": p["quantity"], "Item Rate": money(p["unit_price"]),
                  "Amount": money(p["amount"]), "Approval Status": "Approved", "Created By": f"Employee {p['requester_id']}"})
    approvals.append({"Document Number": ns_po[p["po_id"]], "System Notes : Field": "Approval Status",
                      "System Notes : New Value": "Pending Approval", "System Notes : Set by": f"Employee {p['requester_id']}",
                      "System Notes : Date": us_t(pd.Timestamp(p["approved_at"]) - pd.Timedelta(hours=2))})
    approvals.append({"Document Number": ns_po[p["po_id"]], "System Notes : Field": "Approval Status",
                      "System Notes : New Value": "Approved", "System Notes : Set by": f"Employee {p['approver_id']}",
                      "System Notes : Date": us_t(p["approved_at"])})
lines.append(lines[0] | {"Document Number": "PO99999", "Amount": "99,999.00", "Approval Status": "Pending Supervisor Approval"})
bills, payments = [], []
for i, v in inv.iterrows():
    bills.append({"Document Number": f"VB{i + 1}", "Reference No.": v["invoice_no"], "Name": vname[v["vendor_id"]],
                  "Date": us(v["invoice_date"]), "Amount": money(v["amount"]),
                  "Created From": f"Purchase Order #{ns_po[v['po_id']]}", "Status": "Paid In Full"})
    payments.append({"Date": us(v["paid_date"]), "Applied To Transaction": f"Bill #VB{i + 1}"})
bills.append(bills[0] | {"Document Number": "VB99999", "Reference No.": "CXL-1", "Status": "Cancelled"})
finish(HERE / "netsuite_sample", ns_po, {
    "PO Lines": lines, "PO Approvals": approvals, "Vendor Bills": bills, "Bill Payments": payments,
    "Vendors": [{"Name": v["name"], "Tax Number": "3" + v["cr_number"] + "00003", "Date Created": us(v["created_date"])}
                for _, v in ven.iterrows()],
    "Vendor Bank Details": [{"Parent Vendor": v["name"], "IBAN": v["bank_iban"]} for _, v in ven.iterrows()],
    "Employee Bank Details": [{"Parent Employee": f"Employee {s['staff_id']}", "IBAN": s["bank_iban"]} for _, s in staff.iterrows()],
})

# ---------- ERPNext ----------
ep = {p: f"PUR-ORD-2026-{n_of(p):05d}" for p in pos["po_id"]}
rows, split = [], False
for _, p in pos.iterrows():
    head = {"ID": ep[p["po_id"]], "Supplier": vname[p["vendor_id"]], "Date": p["po_date"], "Status": "Completed",
            "Owner": f"{p['requester_id'].lower()}@company.sa"}
    qty, price = float(p["quantity"]), float(p["unit_price"])
    parts = [qty]
    if not split and qty >= 10 and p["category"] == "Facility services":
        parts, split = [qty // 2, qty - qty // 2], True
    for j, q in enumerate(parts):
        rows.append((head if j == 0 else {k: "" for k in head}) | {
            "Item Code (Purchase Order Item)": p["item"], "Item Group (Purchase Order Item)": p["category"],
            "Quantity (Purchase Order Item)": q, "Rate (Purchase Order Item)": price,
            "Amount (Purchase Order Item)": round(q * price, 2)})
rows.append(rows[0] | {"ID": "PUR-ORD-2026-99999", "Status": "Draft", "Amount (Purchase Order Item)": 55555})
pinv, pay = [], []
for i, v in inv.iterrows():
    pid = f"ACC-PINV-2026-{i + 1:05d}"
    pinv.append({"ID": pid, "Supplier": vname[v["vendor_id"]], "Supplier Invoice No": v["invoice_no"],
                 "Supplier Invoice Date": v["invoice_date"], "Grand Total": v["amount"], "Status": "Paid",
                 "Purchase Order (Purchase Invoice Item)": ep[v["po_id"]]})
    if i % 100 == 0:  # a second item row: parent cells blank
        pinv.append({"ID": "", "Supplier": "", "Supplier Invoice No": "", "Supplier Invoice Date": "", "Grand Total": "",
                     "Status": "", "Purchase Order (Purchase Invoice Item)": ep[v["po_id"]]})
    pay.append({"Posting Date": v["paid_date"], "Status": "Submitted", "Reference Name (Payment Entry Reference)": pid})
pinv.append(pinv[0] | {"ID": "ACC-PINV-2026-99999", "Supplier Invoice No": "CXL-1", "Status": "Cancelled"})
banks = [{"Party Type": "Supplier", "Party": v["name"], "IBAN": v["bank_iban"]} for _, v in ven.iterrows()] + \
        [{"Party Type": "Employee", "Party": f"HR-EMP-{s['staff_id'][1:]}", "IBAN": s["bank_iban"]} for _, s in staff.iterrows()]
finish(HERE / "erpnext_sample", ep, {
    "Purchase Order": rows, "Purchase Invoice": pinv, "Payment Entry": pay, "Bank Account": banks,
    "Supplier": [{"ID": v["name"], "Supplier Name": v["name"], "Tax Id": "3" + v["cr_number"] + "00003",
                  "Created On": v["created_date"] + " 08:00:00"} for _, v in ven.iterrows()],
})

# ---------- Zoho Books ----------
zp = {p: f"PO-{n_of(p):05d}" for p in pos["po_id"]}
zpo = [{"Purchase Order Date": p["po_date"], "Purchase Order Number": zp[p["po_id"]], "Purchase Order Status": "Billed",
        "Vendor Name": vname[p["vendor_id"]], "Item Name": p["item"], "Account": p["category"],
        "QuantityOrdered": p["quantity"], "Rate": p["unit_price"], "Item Total": p["amount"]} for _, p in pos.iterrows()]
zpo.append(zpo[0] | {"Purchase Order Number": "PO-99999", "Purchase Order Status": "Draft", "Item Total": 44444})
zbill, zpay = [], []
for i, v in inv.iterrows():
    zbill.append({"Bill ID": 460000 + i, "Bill Date": v["invoice_date"], "Bill Number": v["invoice_no"], "Bill Status": "Paid",
                  "Vendor Name": vname[v["vendor_id"]], "Total": v["amount"], "Purchase Order Number": zp[v["po_id"]]})
    if i % 100 == 0:  # line-item export repeats the bill on every line
        zbill.append(zbill[-1])
zbill.append(zbill[0] | {"Bill ID": 469999, "Bill Number": "VOID-1", "Bill Status": "Void"})
for i in range(0, len(inv) - 1, 2):
    a, b = inv.iloc[i], inv.iloc[i + 1]
    if a["vendor_id"] == b["vendor_id"] and a["paid_date"] == b["paid_date"]:
        zpay.append({"Date": a["paid_date"], "Vendor Name": vname[a["vendor_id"]], "Bill Number": f"{a['invoice_no']},{b['invoice_no']}"})
    else:
        zpay.append({"Date": a["paid_date"], "Vendor Name": vname[a["vendor_id"]], "Bill Number": a["invoice_no"]})
        zpay.append({"Date": b["paid_date"], "Vendor Name": vname[b["vendor_id"]], "Bill Number": b["invoice_no"]})
if len(inv) % 2:
    zpay.append({"Date": inv.iloc[-1]["paid_date"], "Vendor Name": vname[inv.iloc[-1]["vendor_id"]], "Bill Number": inv.iloc[-1]["invoice_no"]})
finish(HERE / "zoho_books_sample", zp, {
    "Purchase_Order": zpo, "Bill": zbill, "Vendor_Payment": zpay,
    "Contacts": [{"Contact Type": "vendor", "Display Name": v["name"], "Tax Registration Number": "3" + v["cr_number"] + "00003",
                  "Created Time": v["created_date"] + " 08:00:00"} for _, v in ven.iterrows()],
})
print("Wrote netsuite_sample, erpnext_sample, zoho_books_sample")
