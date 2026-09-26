"""Turn the fictional sample into realistic Microsoft Dynamics exports, to test map_dynamics.py.

Writes two folders:
- dynamics_fo_sample/: Dynamics 365 Finance and Operations. Data management entity exports (upper-case
  field names, ISO dates, UTC datetimes, price units other than 1, cancelled orders and lines), plus the
  Invoice journal and Vendor transactions inquiries exported to Excel with their English labels, and a
  flattened workflow history (submission, delegation, approval).
- dynamics_bc_sample/: Dynamics 365 Business Central. List pages exported to Excel with English captions.
  Fully invoiced orders are deleted in Business Central and survive only in Purchase Order Archives, so the
  orders are there (some in two versions), with one open, unreleased order on the live list; Posted Approval
  Entries with an earlier rejection; a cancelled posted invoice; Vendor Ledger Entries with Closed at Date.
The answer keys are translated to each edition's numbers.
"""
from pathlib import Path

import pandas as pd

HERE = Path(__file__).parent
SRC = HERE / "sample"
UTC = pd.Timedelta(hours=3)

pos = pd.read_csv(SRC / "purchase_orders.csv", dtype=str)
inv = pd.read_csv(SRC / "invoices.csv", dtype=str)
ven = pd.read_csv(SRC / "vendors.csv", dtype=str)
staff = pd.read_csv(SRC / "staff.csv", dtype=str)
key = pd.read_csv(SRC / "answer_key.csv", dtype=str)
limits = (SRC / "approval_limits.csv").read_text(encoding="utf-8")


def translate(po_map, vendor_map, out):
    k = key.copy()
    k["reference"] = k["reference"].map(lambda r: ";".join(po_map.get(x) or vendor_map.get(x) or x for x in r.split(";")))
    k.to_csv(out / "answer_key.csv", index=False)
    (out / "approval_limits.csv").write_text(limits, encoding="utf-8")


# ---------- Dynamics 365 Finance and Operations ----------
fo = HERE / "dynamics_fo_sample"
fo.mkdir(exist_ok=True)
po_no = {p: f"PO26-{int(p.split('-')[1]):06d}" for p in pos["po_id"]}
acct = {v: f"SA-{int(v[1:]):06d}" for v in ven["vendor_id"]}
user = lambda s: f"u{s[1:]}"
iso = lambda d: pd.Timestamp(d).strftime("%Y-%m-%dT00:00:00")
utc = lambda t: (pd.Timestamp(t) - UTC).strftime("%Y-%m-%dT%H:%M:%SZ")
items = {it: f"A{1000 + i}" for i, it in enumerate(sorted(pos["item"].unique()))}

heads, lines, flow = [], [], []
for i, p in pos.iterrows():
    n, t = po_no[p["po_id"]], pd.Timestamp(p["approved_at"])
    heads.append({"PURCHASEORDERNUMBER": n, "ORDERVENDORACCOUNTNUMBER": acct[p["vendor_id"]],
                  "ACCOUNTINGDATE": iso(p["po_date"]), "PURCHASEORDERSTATUS": "Invoiced",
                  "DOCUMENTAPPROVALSTATUS": "Confirmed", "ORDERERPERSONNELNUMBER": f"000{p['requester_id'][1:]}"})
    price = float(p["unit_price"])
    unit = 100 if price < 50 else 1
    lines.append({"PURCHASEORDERNUMBER": n, "LINENUMBER": 1, "ITEMNUMBER": items[p["item"]], "LINEDESCRIPTION": p["item"],
                  "PROCUREMENTCATEGORYNAME": p["category"], "ORDEREDPURCHASEQUANTITY": p["quantity"],
                  "PURCHASEPRICE": round(price * unit, 2), "PURCHASEPRICEQUANTITY": unit, "LINEAMOUNT": p["amount"],
                  "PURCHASEORDERLINESTATUS": "Invoiced"})
    if i % 75 == 0:
        lines.append(lines[-1] | {"LINENUMBER": 2, "ORDEREDPURCHASEQUANTITY": 500, "LINEAMOUNT": 250000,
                                  "PURCHASEORDERLINESTATUS": "Canceled"})
    flow.append({"DOCUMENT": n, "TRACKINGTYPE": "Submission", "USERID": user(p["requester_id"]),
                 "CREATEDDATETIME": utc(t - pd.Timedelta(hours=3))})
    if i % 9 == 0:
        flow.append({"DOCUMENT": n, "TRACKINGTYPE": "Delegation", "USERID": "u999",
                     "CREATEDDATETIME": utc(t - pd.Timedelta(hours=1))})
    flow.append({"DOCUMENT": n, "TRACKINGTYPE": "Approval", "USERID": user(p["approver_id"]), "CREATEDDATETIME": utc(t)})
heads.append({"PURCHASEORDERNUMBER": "PO26-999999", "ORDERVENDORACCOUNTNUMBER": acct["V001"], "ACCOUNTINGDATE": iso("2026-08-02"),
              "PURCHASEORDERSTATUS": "Canceled", "DOCUMENTAPPROVALSTATUS": "Approved", "ORDERERPERSONNELNUMBER": "000001"})
lines.append({"PURCHASEORDERNUMBER": "PO26-999999", "LINENUMBER": 1, "ITEMNUMBER": "A9999", "LINEDESCRIPTION": "Cancelled",
              "PROCUREMENTCATEGORYNAME": "Other", "ORDEREDPURCHASEQUANTITY": 1, "PURCHASEPRICE": 77777,
              "PURCHASEPRICEQUANTITY": 1, "LINEAMOUNT": 77777, "PURCHASEORDERLINESTATUS": "Canceled"})

journal = [{"Invoice": v["invoice_no"], "Invoice account": acct[v["vendor_id"]], "Purchase order": po_no[v["po_id"]],
            "Invoice date": pd.Timestamp(v["invoice_date"]).strftime("%m/%d/%Y"), "Invoice amount": f"{float(v['amount']):,.2f}"}
           for _, v in inv.iterrows()]
trans = []
for _, v in inv.iterrows():
    trans.append({"Vendor account": acct[v["vendor_id"]], "Invoice": v["invoice_no"], "Transaction type": "Invoice",
                  "Date": pd.Timestamp(v["invoice_date"]).strftime("%m/%d/%Y"), "Amount in transaction currency": f"-{float(v['amount']):,.2f}",
                  "Closed": pd.Timestamp(v["paid_date"]).strftime("%m/%d/%Y")})
    trans.append({"Vendor account": acct[v["vendor_id"]], "Invoice": "", "Transaction type": "Payment",
                  "Date": pd.Timestamp(v["paid_date"]).strftime("%m/%d/%Y"), "Amount in transaction currency": f"{float(v['amount']):,.2f}",
                  "Closed": pd.Timestamp(v["paid_date"]).strftime("%m/%d/%Y")})

pd.DataFrame(heads).to_csv(fo / "PurchPurchaseOrderHeaderV2Entity.csv", index=False)
pd.DataFrame(lines).to_csv(fo / "PurchPurchaseOrderLineV2Entity.csv", index=False)
pd.DataFrame(flow).to_csv(fo / "WorkflowHistory.csv", index=False)
pd.DataFrame([{"VENDORACCOUNTNUMBER": acct[v["vendor_id"]], "VENDORORGANIZATIONNAME": v["name"],
               "TAXEXEMPTNUMBER": "3" + v["cr_number"] + "00003", "CREATEDDATETIME": iso(v["created_date"])}
              for _, v in ven.iterrows()]).to_csv(fo / "VendVendorV2Entity.csv", index=False)
pd.DataFrame([{"VENDORACCOUNTNUMBER": acct[v["vendor_id"]], "BANKACCOUNTID": "MAIN", "IBAN": v["bank_iban"],
               "BANKACCOUNTNUMBER": v["bank_iban"][-12:]} for _, v in ven.iterrows()]).to_csv(fo / "VendVendorBankAccountEntity.csv", index=False)
pd.DataFrame(journal).to_excel(fo / "VendorInvoiceJournal.xlsx", index=False)
pd.DataFrame(trans).to_excel(fo / "VendorTransactions.xlsx", index=False)
pd.DataFrame([{"PERSONNELNUMBER": f"000{s['staff_id'][1:]}", "IBAN": s["bank_iban"]} for _, s in staff.iterrows()]) \
    .to_csv(fo / "StaffBankAccounts.csv", index=False)
translate(po_no, acct, fo)

# ---------- Dynamics 365 Business Central ----------
bc = HERE / "dynamics_bc_sample"
bc.mkdir(exist_ok=True)
bc_no = {p: str(106000 + int(p.split("-")[1])) for p in pos["po_id"]}
vno = {v: f"V{int(v[1:]):05d}" for v in ven["vendor_id"]}
day = lambda d: pd.Timestamp(d).to_pydatetime()
arch_h, arch_l, appr = [], [], []
for i, p in pos.iterrows():
    n, t = bc_no[p["po_id"]], pd.Timestamp(p["approved_at"])
    versions = [1, 2] if i % 30 == 0 else [1]
    for version in versions:
        arch_h.append({"No.": n, "Version No.": version, "Buy-from Vendor No.": vno[p["vendor_id"]],
                       "Buy-from Vendor Name": ven.set_index("vendor_id").loc[p["vendor_id"], "name"],
                       "Order Date": day(p["po_date"]), "Status": "Released", "Assigned User ID": user(p["requester_id"]).upper()})
        # Version 1 of a two-version order had a different quantity; only the latest version counts.
        q = float(p["quantity"]) + (5 if version < max(versions) else 0)
        arch_l.append({"Document No.": n, "Version No.": version, "Line No.": 10000, "Type": "Item", "No.": items[p["item"]],
                       "Description": p["item"], "Item Category Code": p["category"].upper().replace(" ", "")[:20],
                       "Quantity": q, "Direct Unit Cost Excl. VAT": float(p["unit_price"]),
                       "Line Amount Excl. VAT": round(q * float(p["unit_price"]), 2)})
    if i % 50 == 0:
        appr.append({"Table ID": 38, "Document Type": "Order", "Document No.": n, "Sequence No.": 1,
                     "Sender ID": user(p["requester_id"]).upper(), "Approver ID": "U999", "Status": "Rejected",
                     "Date-Time Sent for Approval": (t - pd.Timedelta(days=1)).to_pydatetime(),
                     "Last Date-Time Modified": (t - pd.Timedelta(hours=20)).to_pydatetime()})
    appr.append({"Table ID": 38, "Document Type": "Order", "Document No.": n, "Sequence No.": 2,
                 "Sender ID": user(p["requester_id"]).upper(), "Approver ID": user(p["approver_id"]).upper(), "Status": "Approved",
                 "Date-Time Sent for Approval": (t - pd.Timedelta(hours=2)).to_pydatetime(),
                 "Last Date-Time Modified": t.to_pydatetime()})
cats = {c: c.upper().replace(" ", "")[:20] for c in pos["category"].unique()}
live_h = [{"No.": "199999", "Buy-from Vendor No.": vno["V001"], "Buy-from Vendor Name": "x", "Order Date": day("2026-09-20"),
           "Status": "Open", "Assigned User ID": "U001"}]
live_l = [{"Document No.": "199999", "Line No.": 10000, "Type": "Item", "No.": "A9999", "Description": "Not released",
           "Item Category Code": "OTHER", "Quantity": 1, "Direct Unit Cost Excl. VAT": 66666.0, "Line Amount Excl. VAT": 66666.0}]
posted, ledger = [], []
for i, v in inv.reset_index(drop=True).iterrows():
    doc = f"PPI{260000 + i}"
    posted.append({"No.": doc, "Buy-from Vendor No.": vno[v["vendor_id"]], "Vendor Invoice No.": v["invoice_no"],
                   "Document Date": day(v["invoice_date"]), "Amount Including VAT": float(v["amount"]),
                   "Order No.": bc_no[v["po_id"]], "Cancelled": "No"})
    ledger.append({"Posting Date": day(v["invoice_date"]), "Document Type": "Invoice", "Document No.": doc,
                   "Vendor No.": vno[v["vendor_id"]], "Amount": -float(v["amount"]), "Open": "No",
                   "Closed at Date": day(v["paid_date"])})
posted.append(posted[0] | {"No.": "PPI269999", "Vendor Invoice No.": "CXL-1", "Cancelled": "Yes"})
pd.DataFrame(arch_h).to_excel(bc / "Purchase Order Archives.xlsx", index=False)
pd.DataFrame(arch_l).to_excel(bc / "Purchase Line Archives.xlsx", index=False)
pd.DataFrame(live_h).to_excel(bc / "Purchase Orders.xlsx", index=False)
pd.DataFrame(live_l).to_excel(bc / "Purchase Lines.xlsx", index=False)
pd.DataFrame(appr).to_excel(bc / "Posted Approval Entries.xlsx", index=False)
pd.DataFrame([{"No.": vno[v["vendor_id"]], "Name": v["name"], "VAT Registration No.": "3" + v["cr_number"] + "00003",
               "System Created At": day(v["created_date"])} for _, v in ven.iterrows()]).to_excel(bc / "Vendors.xlsx", index=False)
pd.DataFrame([{"Vendor No.": vno[v["vendor_id"]], "Code": "MAIN", "IBAN": v["bank_iban"], "Bank Account No.": v["bank_iban"][-12:]}
              for _, v in ven.iterrows()]).to_excel(bc / "Vendor Bank Accounts.xlsx", index=False)
pd.DataFrame([{"Code": code, "Description": name} for name, code in cats.items()]).to_excel(bc / "Item Categories.xlsx", index=False)
pd.DataFrame(posted).to_excel(bc / "Posted Purchase Invoices.xlsx", index=False)
pd.DataFrame(ledger).to_excel(bc / "Vendor Ledger Entries.xlsx", index=False)
pd.DataFrame([{"No.": f"E{s['staff_id'][1:]}", "IBAN": s["bank_iban"]} for _, s in staff.iterrows()]).to_excel(bc / "Employees.xlsx", index=False)
translate(bc_no, vno, bc)
print(f"Wrote {fo.name} ({len(heads)} POs, {len(flow)} workflow steps) and {bc.name} "
      f"({len(arch_h)} archived order versions, {len(appr)} approval entries)")
