"""Map Microsoft Dynamics 365 exports to the review input format.

Usage:
    python map_dynamics.py <folder> <out_folder> [fo|bc] [utc_offset_hours]
    python review.py <out_folder> <report_folder>

Two editions, detected from the file names when not given:
- fo: Dynamics 365 Finance and Operations (and AX 2012 with the same fields). Data management exports of
  the purchase order entities, the vendor entities, the Invoice journal and Vendor transactions inquiries
  exported to Excel, and a flattened workflow history. Datetimes are UTC: default offset +3 (Riyadh).
- bc: Dynamics 365 Business Central (and NAV). List pages exported to Excel with English captions.
  Fully invoiced orders are deleted in Business Central, so Purchase Order Archives are read too.
  Excel shows local time: default offset 0.
See README.md for the files to ask for.
"""
import sys
from pathlib import Path

import pandas as pd

from mapcommon import (INVOICE_COLUMNS, PO_COLUMNS, VENDOR_COLUMNS, approval_limits, date_order, dates, has_time, num,
                       read, write, ymd, ymdhm)

# ---------- Finance and Operations ----------
FO = {
    "header": {"po": ["PURCHASEORDERNUMBER", "PURCHID", "PURCHASE ORDER"],
               "vendor": ["ORDERVENDORACCOUNTNUMBER", "ORDERACCOUNT", "VENDOR ACCOUNT"],
               "date": ["ACCOUNTINGDATE", "ORDERDATE", "CREATEDDATETIME", "CREATED DATE AND TIME"],
               "status": ["PURCHASEORDERSTATUS", "PURCHSTATUS", "STATUS"],
               "approval": ["DOCUMENTAPPROVALSTATUS", "DOCUMENTSTATE", "APPROVAL STATUS"],
               "orderer": ["ORDERERPERSONNELNUMBER", "REQUESTERPERSONNELNUMBER", "ORDERER"]},
    "line": {"po": ["PURCHASEORDERNUMBER", "PURCHID", "PURCHASE ORDER"],
             "item": ["ITEMNUMBER", "ITEMID", "ITEM NUMBER"], "text": ["LINEDESCRIPTION", "NAME", "TEXT"],
             "category": ["PROCUREMENTCATEGORYNAME", "PROCUREMENTCATEGORY", "PROCUREMENT CATEGORY"],
             "qty": ["ORDEREDPURCHASEQUANTITY", "PURCHQTY", "QUANTITY"],
             "price": ["PURCHASEPRICE", "PURCHPRICE", "UNIT PRICE"],
             "unit": ["PURCHASEPRICEQUANTITY", "PRICEUNIT", "PRICE UNIT"],
             "amount": ["LINEAMOUNT", "NET AMOUNT"], "status": ["PURCHASEORDERLINESTATUS", "PURCHSTATUS", "LINE STATUS"]},
    "flow": {"po": ["DOCUMENT", "PURCHASEORDERNUMBER", "PURCHID"], "type": ["TRACKINGTYPE", "TRACKING TYPE"],
             "user": ["USERID", "USER"], "time": ["CREATEDDATETIME", "DATE AND TIME"]},
    "vendor": {"id": ["VENDORACCOUNTNUMBER", "ACCOUNTNUM", "VENDOR ACCOUNT"],
               "name": ["VENDORORGANIZATIONNAME", "VENDORPARTYNAME", "NAME"],
               "tax": ["TAXEXEMPTNUMBER", "VATNUM", "TAX EXEMPT NUMBER"],
               "created": ["CREATEDDATETIME", "CREATEDDATETIME1", "CREATED DATE AND TIME"]},
    "bank": {"id": ["VENDORACCOUNTNUMBER", "VENDACCOUNT", "VENDOR ACCOUNT"], "iban": ["IBAN"],
             "account": ["BANKACCOUNTNUMBER", "ACCOUNTNUM", "BANK ACCOUNT NUMBER"]},
    "journal": {"invoice": ["INVOICE", "INVOICEID"], "vendor": ["INVOICE ACCOUNT", "INVOICEACCOUNT", "VENDOR ACCOUNT"],
                "po": ["PURCHASE ORDER", "PURCHID"], "date": ["INVOICE DATE", "INVOICEDATE", "DATE"],
                "amount": ["INVOICE AMOUNT", "INVOICEAMOUNT", "AMOUNT"]},
    "trans": {"vendor": ["VENDOR ACCOUNT", "ACCOUNTNUM"], "invoice": ["INVOICE", "INVOICEID"],
              "type": ["TRANSACTION TYPE", "TRANSTYPE"], "date": ["DATE", "TRANSDATE"],
              "amount": ["AMOUNT IN TRANSACTION CURRENCY", "AMOUNTCUR", "AMOUNT"], "closed": ["CLOSED", "CLOSEDDATE"]},
    "staff": {"id": ["PERSONNELNUMBER", "PERSONNEL NUMBER", "WORKER"], "iban": ["IBAN"],
              "account": ["BANKACCOUNTNUMBER", "BANK ACCOUNT NUMBER"]},
}
FO_DEAD = {"CANCELED", "CANCELLED"}
FO_APPROVED = {"APPROVED", "CONFIRMED", "FINALIZED"}

# ---------- Business Central ----------
BC = {
    "header": {"po": ["No."], "version": ["Version No."], "vendor": ["Buy-from Vendor No.", "Pay-to Vendor No."],
               "date": ["Order Date", "Document Date", "Posting Date"], "status": ["Status"],
               "user": ["Assigned User ID", "Purchaser Code"]},
    "line": {"po": ["Document No."], "version": ["Version No."], "type": ["Type"], "item": ["No."],
             "text": ["Description"], "category": ["Item Category Code"], "qty": ["Quantity"],
             "price": ["Direct Unit Cost Excl. VAT", "Direct Unit Cost"], "amount": ["Line Amount Excl. VAT", "Line Amount"]},
    "approval": {"table": ["Table ID"], "po": ["Document No."], "sender": ["Sender ID"], "approver": ["Approver ID"],
                 "status": ["Status"], "time": ["Last Date-Time Modified", "Date-Time Sent for Approval"]},
    "category": {"code": ["Code"], "name": ["Description"]},
    "vendor": {"id": ["No."], "name": ["Name"], "tax": ["VAT Registration No."],
               "created": ["System Created At", "SystemCreatedAt", "Created At", "Created On"]},
    "bank": {"id": ["Vendor No."], "iban": ["IBAN"], "account": ["Bank Account No."]},
    "posted": {"doc": ["No."], "vendor": ["Buy-from Vendor No.", "Pay-to Vendor No."],
               "invoice": ["Vendor Invoice No."], "date": ["Document Date", "Posting Date"],
               "amount": ["Amount Including VAT", "Amount"], "po": ["Order No."], "cancelled": ["Cancelled"]},
    "ledger": {"type": ["Document Type"], "doc": ["Document No."], "open": ["Open"], "closed": ["Closed at Date"]},
    "staff": {"id": ["No."], "iban": ["IBAN"], "account": ["Bank Account No."]},
}
BC_LINE_TYPES = {"ITEM", "G/L ACCOUNT", "FIXED ASSET", "RESOURCE", "CHARGE (ITEM)", "ALLOCATION ACCOUNT"}


def item_labels(lines, is_item):
    """Item number plus the first description seen for it; lines without an item keep their own description.

    Item numbers are shared by unrelated lines only when blank (Finance and Operations category lines) or
    when they are G/L account numbers (Business Central), so those must not borrow another line's text.
    """
    if "item" not in lines:
        return lines.get("text", pd.Series("", index=lines.index))
    text = lines.get("text", pd.Series("", index=lines.index))
    items = lines["item"].where(is_item & lines["item"].ne(""), "")
    first = text[items.ne("")].groupby(items[items.ne("")]).first().to_dict()
    return pd.Series([f"{i} {first.get(i, '')}".strip() if i else t for i, t in zip(items, text)], index=lines.index)


def iban_map(banks, key="id"):
    out = {}
    if banks is None:
        return out
    col = "iban" if "iban" in banks.columns else "account"
    for v, g in banks.groupby(key):
        out[v] = ";".join(sorted({x for x in g[col] if x}))
    return out


def staff_frame(people):
    if people is None:
        return None
    col = "iban" if "iban" in people.columns else "account"
    return pd.DataFrame({"staff_id": people["id"], "name": "", "department": "", "bank_iban": people[col]}) \
        .query("bank_iban != ''").drop_duplicates()


def fo(src, offset, notes, warn):
    head = read(src, ["PurchPurchaseOrderHeaderV2Entity", "PurchPurchaseOrderHeaderEntity", "PurchaseOrderHeaders"],
                required=["po", "vendor", "date"], rename=FO["header"], upper=True)
    keep = pd.Series(True, index=head.index)
    if "status" in head:
        keep &= ~head["status"].str.upper().isin(FO_DEAD)
    if "approval" in head:
        keep &= head["approval"].str.upper().isin(FO_APPROVED)
    notes.append(f"{head.attrs['file']}: {len(head)} orders, {int((~keep).sum())} dropped (cancelled or not approved)")
    head = head[keep]
    lines = read(src, ["PurchPurchaseOrderLineV2Entity", "PurchPurchaseOrderLineEntity", "PurchaseOrderLines"],
                 required=["po", "qty", "price"], rename=FO["line"], upper=True)
    if "status" in lines:
        dead = lines["status"].str.upper().isin(FO_DEAD)
        notes.append(f"{lines.attrs['file']}: {len(lines)} lines, {int(dead.sum())} cancelled lines dropped")
        lines = lines[~dead]
    lines = lines.merge(head, on="po", how="inner", suffixes=("", "_h"))

    flow = read(src, ["WorkflowHistory", "WorkflowTracking"], required=["po", "type", "user", "time"],
                rename=FO["flow"], upper=True, optional=True, warn=warn)
    approver, approved, submitter = {}, {}, {}
    if flow is not None:
        flow = flow.assign(t=dates(flow["time"], offset)).sort_values("t")
        for _, f in flow[flow["type"].str.upper().isin({"APPROVAL", "APPROVE"})].iterrows():
            approver[f["po"]], approved[f["po"]] = f["user"], f["t"]
        for _, f in flow[flow["type"].str.upper().isin({"SUBMISSION", "SUBMIT"})].iloc[::-1].iterrows():
            submitter[f["po"]] = f["user"]
        notes.append(f"Workflow history: approvals for {len(set(approver) & set(head['po']))} orders")
    else:
        warn.append("No WorkflowHistory: approver and approval time unknown; rule R7 cannot run")
    requester = lines["po"].map(submitter)
    if "orderer" in lines:
        requester = requester.fillna(lines["orderer"])
        if not submitter:
            warn.append("Requester taken from the orderer's personnel number, not the workflow user")

    unit = lines["unit"].map(num).fillna(1).replace(0, 1) if "unit" in lines else 1
    qty, price = lines["qty"].map(num), lines["price"].map(num) / unit
    amount = lines["amount"].map(num) if "amount" in lines else qty * price
    po = pd.DataFrame({
        "po_id": lines["po"], "po_date": ymd(dates(lines["date"], offset)), "vendor_id": lines["vendor"],
        "category": lines["category"] if "category" in lines else "Uncategorised",
        "item": item_labels(lines, pd.Series(True, index=lines.index)),
        "quantity": qty, "unit_price": price.round(4), "amount": amount.round(2),
        "requester_id": requester.fillna(""), "approver_id": lines["po"].map(approver).fillna(""),
        "approved_at": ymdhm(lines["po"].map(approved)),
    }, columns=PO_COLUMNS)

    journal = read(src, ["VendorInvoiceJournal", "VendInvoiceJourEntity"], required=["invoice", "vendor", "date", "amount"],
                   rename=FO["journal"], upper=True, optional=True, warn=warn)
    trans = read(src, ["VendorTransactions", "VendTrans"], required=["vendor", "invoice", "type"],
                 rename=FO["trans"], upper=True, optional=True, warn=warn)
    # The two inquiries come from the same user's locale: decide day or month first from both together.
    dayfirst, proven = date_order(*[df[c] for df in (journal, trans) if df is not None for c in ("date", "closed") if c in df])
    if not proven:
        warn.append("Inquiry dates never show whether day or month comes first: day-first assumed; check one invoice date")
    paid = {}
    if trans is not None and "closed" in trans:
        inv_rows = trans[trans["type"].str.upper().eq("INVOICE") & trans["invoice"].ne("")]
        closed = dates(inv_rows["closed"], dayfirst=dayfirst)
        for v, i, c in zip(inv_rows["vendor"], inv_rows["invoice"], closed):
            if pd.notna(c):
                paid[(v, i)] = max(paid.get((v, i), c), c)
    else:
        warn.append("No VendorTransactions with Closed: payment dates empty; rule R5 cannot run")
    if journal is not None:
        amounts = journal["amount"].map(num)
        credits = amounts <= 0
        if credits.any():
            notes.append(f"{int(credits.sum())} credit notes (zero or negative amounts) left out of the invoice rules")
        journal, amounts = journal[~credits], amounts[~credits]
        invoices = pd.DataFrame({"invoice_no": journal["invoice"], "vendor_id": journal["vendor"],
                                 "po_id": journal.get("po", ""), "invoice_date": ymd(dates(journal["date"], dayfirst=dayfirst)),
                                 "amount": amounts,
                                 "paid_date": ymd(pd.Series([paid.get((v, i)) for v, i in zip(journal["vendor"], journal["invoice"])],
                                                            index=journal.index, dtype="datetime64[ns]"))},
                                columns=INVOICE_COLUMNS)
    elif trans is not None:
        warn.append("No VendorInvoiceJournal: invoices taken from vendor transactions, without the purchase order")
        # In vendor transactions an invoice is a credit to the vendor (negative); positive rows are credit notes.
        t = trans[trans["type"].str.upper().eq("INVOICE") & (trans.get("amount", "").map(num) < 0)]
        invoices = pd.DataFrame({"invoice_no": t["invoice"], "vendor_id": t["vendor"], "po_id": "",
                                 "invoice_date": ymd(dates(t["date"], dayfirst=dayfirst)), "amount": t["amount"].map(num).abs(),
                                 "paid_date": ymd(dates(t["closed"], dayfirst=dayfirst)) if "closed" in t else ""},
                                columns=INVOICE_COLUMNS)
    else:
        raise SystemExit("Need VendorInvoiceJournal or VendorTransactions for invoices")

    ven = read(src, ["VendVendorV2Entity", "VendVendorEntity", "Vendors"], required=["id", "name"], rename=FO["vendor"], upper=True)
    if "created" not in ven:
        warn.append("No created date on vendors (add CREATEDDATETIME to the export): rule R5 cannot run")
    banks = read(src, ["VendVendorBankAccountEntity", "VendorBankAccounts"], required=["id"], rename=FO["bank"],
                 upper=True, optional=True, warn=warn)
    ibans = iban_map(banks)
    if not ibans:
        warn.append("No VendVendorBankAccountEntity: rule R4 cannot run")
    vendors = pd.DataFrame({"vendor_id": ven["id"], "name": ven["name"], "cr_number": ven.get("tax", ""),
                            "bank_iban": ven["id"].map(ibans).fillna(""),
                            "created_date": ymd(dates(ven["created"])) if "created" in ven else ""}, columns=VENDOR_COLUMNS)
    people = read(src, ["StaffBankAccounts", "HcmWorkerBankAccountEntity"], required=["id"], rename=FO["staff"],
                  upper=True, optional=True, warn=warn)
    if people is None:
        warn.append("No StaffBankAccounts: staff accounts not compared (rule R4 skipped)")
    return po, invoices, vendors, staff_frame(people)


def bc(src, offset, notes, warn):
    arch = read(src, ["Purchase Order Archives", "Purchase Header Archive"], required=["po", "version", "vendor", "date"],
                rename=BC["header"], optional=True, warn=warn)
    live = read(src, ["Purchase Orders", "Purchase Header"], required=["po", "vendor", "date"], rename=BC["header"],
                optional=True, warn=warn)
    if arch is None and live is None:
        raise SystemExit("Need Purchase Order Archives (fully invoiced orders) and or Purchase Orders")
    if arch is None:
        warn.append("No Purchase Order Archives: fully invoiced orders are deleted in Business Central, so most "
                    "of the quarter is missing; turn on Archive Orders and export the archives")
    heads = []
    if arch is not None:
        arch = arch.assign(v=arch["version"].map(num)).sort_values("v").groupby("po", as_index=False).last()
        heads.append(arch.assign(source="archive"))
    if live is not None:
        heads.append(live.assign(source="live", version=""))
    head = pd.concat(heads, ignore_index=True)
    head = head.sort_values("source", key=lambda s: s.eq("live")).groupby("po", as_index=False).last()
    released = head["status"].str.upper().eq("RELEASED") if "status" in head else pd.Series(True, index=head.index)
    notes.append(f"Orders: {len(head)} (latest archive version or live), {int((~released).sum())} not released dropped")
    head = head[released]

    lines = []
    al = read(src, ["Purchase Line Archives", "Purchase Line Archive"], required=["po", "version", "qty"],
              rename=BC["line"], optional=True, warn=warn)
    ll = read(src, ["Purchase Lines", "Purchase Line"], required=["po", "qty"], rename=BC["line"], optional=True, warn=warn)
    if al is not None:
        lines.append(al.merge(head[head["source"] == "archive"][["po", "version"]].astype(str), on=["po", "version"]))
    if ll is not None:
        lines.append(ll.merge(head[head["source"] == "live"][["po"]], on="po"))
    lines = pd.concat(lines, ignore_index=True) if lines else pd.DataFrame(columns=["po", "qty"])
    if "type" in lines:
        lines = lines[lines["type"].str.upper().isin(BC_LINE_TYPES)]
    lines = lines.merge(head.drop(columns=["version"], errors="ignore"), on="po", how="inner", suffixes=("", "_h"))

    approvals = [a for a in (read(src, n, required=["po", "approver", "status", "time"], rename=BC["approval"], optional=True, warn=warn)
                             for n in ("Posted Approval Entries", "Approval Entries")) if a is not None]
    approver, approved, sender = {}, {}, {}
    if approvals:
        a = pd.concat(approvals, ignore_index=True)
        if "table" in a:
            a = a[a["table"].isin({"38", "Purchase Header"})]
        a = a[a["status"].str.upper().eq("APPROVED")].assign(t=lambda d: dates(d["time"], offset)).sort_values("t")
        for _, r in a.iterrows():
            approver[r["po"]], approved[r["po"]] = r["approver"], r["t"]
            sender.setdefault(r["po"], r.get("sender", ""))
        notes.append(f"Approval entries: approvals for {len(set(approver) & set(head['po']))} orders")
    else:
        warn.append("No Posted Approval Entries: approver unknown; rule R7 cannot run")
    requester = lines["po"].map(sender).replace("", pd.NA)
    if "user" in lines:
        requester = requester.fillna(lines["user"])

    cats = read(src, ["Item Categories", "Item Category"], required=["code", "name"], rename=BC["category"], optional=True, warn=warn)
    names = dict(zip(cats["code"], cats["name"])) if cats is not None else {}
    if "category" in lines and not names:
        warn.append("No Item Categories export: categories are codes")
    qty, price = lines["qty"].map(num), lines["price"].map(num)
    amount = lines["amount"].map(num) if "amount" in lines else qty * price
    is_item = lines["type"].str.upper().eq("ITEM") if "type" in lines else pd.Series(True, index=lines.index)
    po = pd.DataFrame({
        "po_id": lines["po"], "po_date": ymd(dates(lines["date"], None)), "vendor_id": lines["vendor"],
        "category": lines["category"].map(lambda c: names.get(c, c or "Uncategorised")) if "category" in lines else "Uncategorised",
        "item": item_labels(lines, is_item),
        "quantity": qty, "unit_price": price, "amount": amount.round(2),
        "requester_id": requester.fillna(""), "approver_id": lines["po"].map(approver).fillna(""),
        "approved_at": ymdhm(lines["po"].map(approved)),
    }, columns=PO_COLUMNS)

    posted = read(src, ["Posted Purchase Invoices", "Purch. Inv. Header"], required=["doc", "vendor", "date", "amount"],
                  rename=BC["posted"])
    if "cancelled" in posted:
        cxl = posted["cancelled"].str.upper().isin({"YES", "TRUE", "1"})
        notes.append(f"Posted Purchase Invoices: {len(posted)}, {int(cxl.sum())} cancelled dropped")
        posted = posted[~cxl]
    ledger = read(src, ["Vendor Ledger Entries", "Vendor Ledger Entry"], required=["doc", "closed"], rename=BC["ledger"], optional=True, warn=warn)
    paid = {}
    if ledger is not None:
        rows = ledger[ledger["type"].str.upper().eq("INVOICE")] if "type" in ledger else ledger
        if "open" in rows:
            rows = rows[rows["open"].str.upper().isin({"NO", "FALSE", "0"})]
        paid = dict(zip(rows["doc"], dates(rows["closed"])))
    else:
        warn.append("No Vendor Ledger Entries: payment dates empty; rule R5 cannot run")
    invoices = pd.DataFrame({
        "invoice_no": [i or d for i, d in zip(posted.get("invoice", posted["doc"]), posted["doc"])],
        "vendor_id": posted["vendor"], "po_id": posted.get("po", ""),
        "invoice_date": ymd(dates(posted["date"])), "amount": posted["amount"].map(num).abs(),
        "paid_date": ymd(posted["doc"].map(paid)),
    }, columns=INVOICE_COLUMNS)

    ven = read(src, ["Vendors", "Vendor"], required=["id", "name"], rename=BC["vendor"])
    if "created" not in ven:
        warn.append("No System Created At on Vendors (add the column to the page before exporting): rule R5 cannot run")
    banks = read(src, ["Vendor Bank Accounts", "Vendor Bank Account"], required=["id"], rename=BC["bank"], optional=True, warn=warn)
    ibans = iban_map(banks)
    if not ibans:
        warn.append("No Vendor Bank Accounts: rule R4 cannot run")
    vendors = pd.DataFrame({"vendor_id": ven["id"], "name": ven["name"], "cr_number": ven.get("tax", ""),
                            "bank_iban": ven["id"].map(ibans).fillna(""),
                            "created_date": ymd(dates(ven["created"])) if "created" in ven else ""}, columns=VENDOR_COLUMNS)
    people = read(src, ["Employees", "Employee"], required=["id"], rename=BC["staff"], optional=True, warn=warn)
    if people is None:
        warn.append("No Employees export with IBAN: staff accounts not compared (rule R4 skipped)")
    return po, invoices, vendors, staff_frame(people)


def detect(src):
    names = {p.stem for p in src.iterdir()}
    if names & {"PurchPurchaseOrderHeaderV2Entity", "PurchPurchaseOrderHeaderEntity", "PurchaseOrderHeaders"}:
        return "fo"
    if names & {"Purchase Order Archives", "Purchase Header Archive", "Purchase Orders", "Purchase Header"}:
        return "bc"
    raise SystemExit("Cannot tell the edition from the file names; pass fo or bc")


def run(src, out, edition=None, offset_hours=None):
    edition = edition or detect(src)
    offset_hours = (3.0 if edition == "fo" else 0.0) if offset_hours is None else offset_hours
    notes, warn = [], []
    po, invoices, vendors, staff = (fo if edition == "fo" else bc)(src, pd.Timedelta(hours=offset_hours), notes, warn)
    timed = pd.to_datetime(po["approved_at"].replace("", pd.NA)).map(has_time)
    if len(po) and po["approved_at"].ne("").any() and not timed.any():
        warn.append("Approval times have no time of day: the night and weekend check is unreliable")
    used = set(po["vendor_id"]) | set(invoices["vendor_id"])
    missing = used - set(vendors["vendor_id"])
    if missing:
        warn.append(f"{len(missing)} vendors on orders or invoices are missing from the vendor export")
    limits = approval_limits(src, warn, "Finance and Operations purchasing policies or Business Central approval user setup")
    title = {"fo": "Dynamics 365 Finance and Operations", "bc": "Dynamics 365 Business Central"}[edition]
    write(src, out, f"{title} (datetimes {offset_hours:+g} h)", po, invoices, vendors, staff, limits, notes, warn)


if __name__ == "__main__":
    args = sys.argv[1:]
    if len(args) < 2 or len(args) > 4:
        raise SystemExit(__doc__)
    ed = next((a for a in args[2:] if a in ("fo", "bc")), None)
    off = next((float(a) for a in args[2:] if a not in ("fo", "bc")), None)
    run(Path(args[0]), Path(args[1]), ed, off)
