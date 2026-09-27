"""Turn the fictional sample into realistic Odoo 16/17 exports, to test map_odoo.py end to end.

Writes odoo_sample/ with the file names Odoo's Export gives (purchase.order.xlsx, ...), English
field labels, many2one fields as display names, datetimes in UTC (Odoo stores them in UTC), and
one2many fields exported as extra rows with blank parent columns. Includes a cancelled PO, a
reversed bill, a vendor with two bank accounts, and one payment settling two bills. The answer
key is translated to Odoo references so the review can still score itself.
"""
from pathlib import Path

import pandas as pd

HERE = Path(__file__).parent
SRC, OUT = HERE / "sample", HERE / "odoo_sample"
UTC_OFFSET = pd.Timedelta(hours=3)  # Asia/Riyadh

pos = pd.read_csv(SRC / "purchase_orders.csv", dtype=str)
inv = pd.read_csv(SRC / "invoices.csv", dtype=str)
ven = pd.read_csv(SRC / "vendors.csv", dtype=str)
staff = pd.read_csv(SRC / "staff.csv", dtype=str)
key = pd.read_csv(SRC / "answer_key.csv", dtype=str)
OUT.mkdir(exist_ok=True)

ref = {p: f"P{int(p.split('-')[1]):05d}" for p in pos["po_id"]}
vname = dict(zip(ven["vendor_id"], ven["name"]))
user = lambda staff_id: f"User {staff_id[1:]}"
code = {it: 100000 + i + 1 for i, it in enumerate(sorted(pos["item"].unique()))}
utc = lambda local: (pd.Timestamp(local) - UTC_OFFSET).strftime("%Y-%m-%d %H:%M:%S")

# purchase.order headers, with the database ID used by chatter tracking
header, lines, tracking = [], [], []
split_done = False
for i, p in pos.iterrows():
    db_id = 7000 + i
    header.append({"ID": db_id, "Order Reference": ref[p["po_id"]], "Vendor": vname[p["vendor_id"]],
                   "Order Date": utc(p["po_date"] + " 09:00"), "Confirmation Date": utc(p["approved_at"]),
                   "Created by": user(p["requester_id"]), "Buyer": user(p["requester_id"]),
                   "Status": "Purchase Order", "Total": float(p["amount"])})
    qty, price, amount = float(p["quantity"]), float(p["unit_price"]), float(p["amount"])
    parts = [(qty, amount)]
    if not split_done and qty >= 10 and p["category"] == "IT hardware":
        a = int(qty // 2)
        parts = [(a, round(a * price, 2)), (qty - a, round(amount - round(a * price, 2), 2))]
        split_done = True
    for q, amt in parts:
        lines.append({"Order Reference": ref[p["po_id"]], "Product": f"[{code[p['item']]}] {p['item']}",
                      "Product/Product Category": f"All / {p['category']}", "Description": p["item"],
                      "Quantity": q, "Unit Price": price, "Subtotal": amt})
    tracking.append({"Message/Related Document Model": "purchase.order", "Message/Related Document ID": db_id,
                     "Field": "Status", "Old Value Char": "RFQ", "New Value Char": "Purchase Order",
                     "Created on": utc(p["approved_at"]), "Created by": user(p["approver_id"])})
    if i % 40 == 0:  # a tracked change that is not a confirmation; the mapper must ignore it
        tracking.append({"Message/Related Document Model": "purchase.order", "Message/Related Document ID": db_id,
                         "Field": "Receipt Date", "Old Value Char": "", "New Value Char": "",
                         "Created on": utc(pd.Timestamp(p["approved_at"]) + pd.Timedelta(days=2)),
                         "Created by": "User 999"})
header.append({"ID": 9999, "Order Reference": "P09999", "Vendor": vname["V001"], "Order Date": utc("2026-08-01 09:00"),
               "Confirmation Date": "", "Created by": "User 001", "Buyer": "User 001", "Status": "Cancelled",
               "Total": 99999.0})
lines.append({"Order Reference": "P09999", "Product": "[999999] Cancelled item", "Product/Product Category": "All / Other",
              "Description": "Cancelled item", "Quantity": 1, "Unit Price": 99999.0, "Subtotal": 99999.0})

# account.move vendor bills and account.payment
bills, payments = [], []
for i, v in inv.reset_index(drop=True).iterrows():
    number = f"BILL/2026/{i + 1:05d}"
    bills.append({"Number": number, "Vendor": vname[v["vendor_id"]], "Bill Reference": v["invoice_no"],
                  "Bill Date": v["invoice_date"], "Source Document": ref[v["po_id"]], "Total": float(v["amount"]),
                  "Status": "Posted", "Payment Status": "Paid", "Type": "Vendor Bill"})
    payments.append({"Date": v["paid_date"], "Vendor": vname[v["vendor_id"]], "Amount": float(v["amount"]),
                     "Status": "Paid", "Reconciled Bills": number})
bills.append(dict(bills[0]) | {"Number": "BILL/2026/99999", "Bill Reference": "REV-1", "Payment Status": "Reversed"})
# One payment settling two bills of the same vendor: the second bill becomes a continuation row.
same = next(j for j in range(11, len(payments)) if payments[j]["Vendor"] == payments[10]["Vendor"])
second = payments.pop(same)
payments[10]["Amount"] += second["Amount"]
payments.insert(11, {"Date": "", "Vendor": "", "Amount": "", "Status": "", "Reconciled Bills": second["Reconciled Bills"]})

# res.partner vendors: bank accounts as continuation rows; one vendor gets a second account
partners = []
for _, v in ven.iterrows():
    partners.append({"Display Name": v["name"], "Company ID": v["cr_number"], "Tax ID": "3" + v["cr_number"] + "00003",
                     "Created on": v["created_date"] + " 08:00:00", "Bank Accounts/Account Number": v["bank_iban"]})
    if v["vendor_id"] == "V010":
        partners.append({"Display Name": "", "Company ID": "", "Tax ID": "", "Created on": "",
                         "Bank Accounts/Account Number": "SA0000000000000000000010"})

# hr.employee
employees = [{"Badge ID": s["staff_id"], "Employee Name": s["name"], "Bank Account Number": s["bank_iban"]}
             for _, s in staff.iterrows()]

pd.DataFrame(header).to_excel(OUT / "purchase.order.xlsx", index=False)
pd.DataFrame(lines).to_excel(OUT / "purchase.order.line.xlsx", index=False)
pd.DataFrame(tracking).to_csv(OUT / "mail.tracking.value.csv", index=False)
pd.DataFrame(bills).to_csv(OUT / "account.move.csv", index=False)
pd.DataFrame(payments).to_csv(OUT / "account.payment.csv", index=False)
pd.DataFrame(partners).to_csv(OUT / "res.partner.csv", index=False)
pd.DataFrame(employees).to_csv(OUT / "hr.employee.csv", index=False)
(OUT / "approval_limits.csv").write_text((SRC / "approval_limits.csv").read_text(encoding="utf-8"), encoding="utf-8")


def odoo_ref(r):
    return ";".join(ref.get(x) or vname.get(x) or x for x in r.split(";"))


key["reference"] = key["reference"].map(odoo_ref)
key.to_csv(OUT / "answer_key.csv", index=False)
print(f"Wrote Odoo-style export to {OUT}: {len(header)} POs (1 cancelled), {len(lines)} lines, "
      f"{len(bills)} bills (1 reversed), {len(payments)} payment rows, {len(tracking)} tracking values")
