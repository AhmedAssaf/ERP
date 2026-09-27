"""Map an SAP ECC or S/4HANA table export to the review input format.

Usage:
    python map_sap.py <sap_folder> <out_folder>
    python review.py <out_folder> <report_folder>

Reads one file per SAP table from <sap_folder> (.txt tab-delimited, .csv, or .xlsx, named after the
table, for example EKKO.txt) and writes the five review CSVs plus mapping_report.txt. Accepts technical
column names or the common English ALV headers, dd.mm.yyyy or ISO dates, and 1.234,56 or 1,234.56
numbers with a trailing minus. See README.md for which tables to download and why.
"""
import re
import sys
from pathlib import Path

import pandas as pd

# English ALV headers seen in SE16N and standard reports, mapped to technical names, per table.
ALIASES = {
    "EKKO": {"Purchasing Document": "EBELN", "Purch.Doc.": "EBELN", "Vendor": "LIFNR", "Supplier": "LIFNR",
             "Document Date": "BEDAT", "Created By": "ERNAM", "Company Code": "BUKRS"},
    "EKPO": {"Purchasing Document": "EBELN", "Item": "EBELP", "Deletion Indicator": "LOEKZ", "Material": "MATNR",
             "Short Text": "TXZ01", "Material Group": "MATKL", "PO Quantity": "MENGE", "Order Quantity": "MENGE",
             "Net Price": "NETPR", "Price Unit": "PEINH", "Net Order Value": "NETWR"},
    "T023T": {"Material Group": "MATKL", "Material Group Desc.": "WGBEZ", "Language Key": "SPRAS"},
    "CDHDR": {"Object": "OBJECTCLAS", "Object Value": "OBJECTID", "Document Number": "CHANGENR",
              "User Name": "USERNAME", "Date": "UDATE", "Time": "UTIME", "Transaction Code": "TCODE"},
    "CDPOS": {"Object": "OBJECTCLAS", "Object Value": "OBJECTID", "Document Number": "CHANGENR",
              "Field Name": "FNAME", "New Value": "VALUE_NEW"},
    "RBKP": {"Invoice Document No.": "BELNR", "Fiscal Year": "GJAHR", "Invoicing Party": "LIFNR",
             "Reference": "XBLNR", "Document Date": "BLDAT", "Gross Invoice Amount": "RMWWR",
             "Reversed with": "STBLG", "Company Code": "BUKRS"},
    "RSEG": {"Invoice Document No.": "BELNR", "Fiscal Year": "GJAHR", "Purchasing Document": "EBELN",
             "Item": "EBELP", "Amount": "WRBTR"},
    "BKPF": {"Company Code": "BUKRS", "Document Number": "BELNR", "Fiscal Year": "GJAHR",
             "Reference Transaction": "AWTYP", "Reference Key": "AWKEY"},
    "BSAK": {"Company Code": "BUKRS", "Vendor": "LIFNR", "Supplier": "LIFNR", "Document Number": "BELNR",
             "Fiscal Year": "GJAHR", "Clearing Date": "AUGDT", "Clearing": "AUGDT", "Reference": "XBLNR"},
    "LFA1": {"Vendor": "LIFNR", "Supplier": "LIFNR", "Name": "NAME1", "Name 1": "NAME1", "Tax Number 1": "STCD1",
             "Created On": "ERDAT"},
    "LFBK": {"Vendor": "LIFNR", "Supplier": "LIFNR", "Bank Country": "BANKS", "Bank Key": "BANKL",
             "Bank Account": "BANKN", "IBAN": "IBAN"},
    "TIBAN": {"Bank Country": "BANKS", "Bank Key": "BANKL", "Bank Account": "BANKN", "IBAN": "IBAN"},
    "PA0009": {"Personnel Number": "PERNR", "Personnel No.": "PERNR", "IBAN": "IBAN", "Bank Account": "BANKN"},
}
RELEASE_FIELDS = {"FRGKE", "FRGZU"}  # PO release indicator and release status
DELETED = {"L"}                       # EKPO-LOEKZ: L = deleted line


def read(folder, table, required, optional=False):
    for ext in (".txt", ".csv", ".xlsx", ".xls"):
        f = folder / f"{table}{ext}"
        if f.exists():
            if ext.startswith(".xls"):
                df = pd.read_excel(f, dtype=str)
            else:
                df = pd.read_csv(f, dtype=str, sep=None, engine="python", encoding="utf-8-sig")
            df.columns = [str(c).strip() for c in df.columns]
            df = df.rename(columns=ALIASES.get(table, {}))
            df = df.apply(lambda c: c.str.strip() if c.dtype == object else c).fillna("")
            missing = [c for c in required if c not in df.columns]
            if missing:
                raise SystemExit(f"{f.name}: missing columns {missing}; found {list(df.columns)}")
            return df
    if optional:
        return None
    raise SystemExit(f"Missing {table} in {folder} (.txt, .csv or .xlsx)")


def num(v):
    s = str(v).strip().replace(" ", "")
    if s in ("", "nan"):
        return float("nan")
    neg = s.endswith("-") or s.startswith("-")
    s = s.strip("-")
    if re.fullmatch(r"[\d.]*,\d+", s):      # 1.234,56
        s = s.replace(".", "").replace(",", ".")
    else:                                    # 1,234.56
        s = s.replace(",", "")
    return -float(s) if neg else float(s)


def date(v):
    s = str(v).strip()
    if not s or set(s) <= set("0./-"):
        return pd.NaT
    for fmt in ("%d.%m.%Y", "%Y-%m-%d", "%Y%m%d", "%d/%m/%Y", "%Y-%m-%d %H:%M:%S"):
        try:
            return pd.to_datetime(s, format=fmt)
        except ValueError:
            continue
    return pd.NaT


def key(v):
    """Strip SAP leading zeros from numeric keys (vendor, material)."""
    s = str(v).strip()
    return s.lstrip("0") or "0" if s.isdigit() else s


def run(src, out):
    notes, warn = [], []

    ekko = read(src, "EKKO", ["EBELN", "LIFNR", "BEDAT", "ERNAM"])
    ekpo = read(src, "EKPO", ["EBELN", "EBELP", "MENGE", "NETPR", "NETWR"])
    lines = ekpo[~ekpo.get("LOEKZ", pd.Series("", index=ekpo.index)).isin(DELETED)]
    notes.append(f"EKPO: {len(ekpo)} lines, {len(ekpo) - len(lines)} deleted lines dropped")
    lines = lines.merge(ekko[["EBELN", "LIFNR", "BEDAT", "ERNAM"]], on="EBELN", how="inner")

    t023t = read(src, "T023T", ["MATKL", "WGBEZ"], optional=True)
    groups = dict(zip(t023t["MATKL"], t023t["WGBEZ"])) if t023t is not None else {}
    if not groups:
        warn.append("No T023T: categories are material group codes, not names")
    has_matnr = "MATNR" in lines.columns
    lines["_item"] = [key(m) if has_matnr and m else t for m, t in
                      zip(lines["MATNR"] if has_matnr else [""] * len(lines), lines.get("TXZ01", ""))]
    text = lines.drop_duplicates("_item").set_index("_item").get("TXZ01", pd.Series(dtype=str)).to_dict()

    # Approvals: the last release change document per PO (CDHDR + CDPOS, object class EINKBELEG).
    cdhdr = read(src, "CDHDR", ["OBJECTID", "CHANGENR", "USERNAME", "UDATE", "UTIME"], optional=True)
    cdpos = read(src, "CDPOS", ["OBJECTID", "CHANGENR", "FNAME"], optional=True)
    approvals = pd.DataFrame(columns=["EBELN", "approver_id", "approved_at"])
    if cdhdr is not None and cdpos is not None:
        rel = cdpos[cdpos["FNAME"].isin(RELEASE_FIELDS)][["OBJECTID", "CHANGENR"]].drop_duplicates()
        h = cdhdr.merge(rel, on=["OBJECTID", "CHANGENR"])
        h["approved_at"] = [date(d) + pd.to_timedelta(t or "00:00:00") if pd.notna(date(d)) else pd.NaT
                            for d, t in zip(h["UDATE"], h["UTIME"])]
        h = h.sort_values("approved_at").groupby("OBJECTID", as_index=False).last()
        approvals = h.rename(columns={"OBJECTID": "EBELN", "USERNAME": "approver_id"})[
            ["EBELN", "approver_id", "approved_at"]]
        notes.append(f"Change documents: {len(rel)} release changes on {len(approvals)} POs")
    else:
        warn.append("No CDHDR or CDPOS: rule R7 (approvals) cannot run; approver and time left empty")
    lines = lines.merge(approvals, on="EBELN", how="left")
    no_release = lines.loc[lines["approver_id"].isna(), "EBELN"].nunique()
    if no_release:
        warn.append(f"{no_release} POs have no release change document (no release strategy or released on creation)")

    peinh = lines["PEINH"].map(num).fillna(1).replace(0, 1) if "PEINH" in lines else 1
    po = pd.DataFrame({
        "po_id": lines["EBELN"],
        "po_date": lines["BEDAT"].map(date).dt.strftime("%Y-%m-%d"),
        "vendor_id": lines["LIFNR"].map(key),
        "category": lines["MATKL"].map(lambda m: groups.get(m, m)) if "MATKL" in lines else "Uncategorised",
        "item": [f"{i} {text.get(i, '')}".strip() if has_matnr and i.isdigit() else i for i in lines["_item"]],
        "quantity": lines["MENGE"].map(num),
        "unit_price": (lines["NETPR"].map(num) / peinh).round(4),
        "amount": lines["NETWR"].map(num),
        "requester_id": lines["ERNAM"],
        "approver_id": lines["approver_id"].fillna(""),
        "approved_at": pd.to_datetime(lines["approved_at"]).dt.strftime("%Y-%m-%d %H:%M").fillna(""),
    })

    # Invoices: RBKP headers, first PO from RSEG, payment date through BKPF (AWKEY) to BSAK (AUGDT).
    rbkp = read(src, "RBKP", ["BELNR", "GJAHR", "LIFNR", "XBLNR", "BLDAT", "RMWWR"])
    reversed_ = rbkp["STBLG"].ne("") if "STBLG" in rbkp else pd.Series(False, index=rbkp.index)
    notes.append(f"RBKP: {len(rbkp)} invoices, {int(reversed_.sum())} reversed or reversal documents dropped")
    rbkp = rbkp[~reversed_]
    rseg = read(src, "RSEG", ["BELNR", "GJAHR", "EBELN"])
    first_po = rseg.drop_duplicates(["BELNR", "GJAHR"])[["BELNR", "GJAHR", "EBELN"]]
    inv = rbkp.merge(first_po, on=["BELNR", "GJAHR"], how="left")
    bkpf = read(src, "BKPF", ["BELNR", "GJAHR", "AWTYP", "AWKEY"], optional=True)
    bsak = read(src, "BSAK", ["BELNR", "GJAHR", "AUGDT"], optional=True)
    if bkpf is not None and bsak is not None:
        link = bkpf[bkpf["AWTYP"] == "RMRP"].rename(columns={"BELNR": "FI_BELNR"})
        link = link.assign(BELNR=link["AWKEY"].str[:10], GJAHR_MM=link["AWKEY"].str[10:14])
        paid = bsak.rename(columns={"BELNR": "FI_BELNR"})[["FI_BELNR", "GJAHR", "AUGDT"]]
        link = link.merge(paid, on=["FI_BELNR", "GJAHR"], how="left")
        inv = inv.merge(link[["BELNR", "GJAHR_MM", "AUGDT"]].rename(columns={"GJAHR_MM": "GJAHR"}),
                        on=["BELNR", "GJAHR"], how="left")
    else:
        inv["AUGDT"] = ""
        warn.append("No BKPF or BSAK: payment dates empty; rule R5 (new vendor paid fast) cannot run")
    unpaid = inv["AUGDT"].fillna("").eq("").sum()
    if unpaid:
        notes.append(f"{unpaid} invoices not yet paid (no clearing date)")
    invoices = pd.DataFrame({
        "invoice_no": [x or b for x, b in zip(inv["XBLNR"], inv["BELNR"])],
        "vendor_id": inv["LIFNR"].map(key),
        "po_id": inv["EBELN"].fillna(""),
        "invoice_date": inv["BLDAT"].map(date).dt.strftime("%Y-%m-%d"),
        "amount": inv["RMWWR"].map(num),
        "paid_date": inv["AUGDT"].fillna("").map(date).dt.strftime("%Y-%m-%d").fillna(""),
    })

    # Vendors: LFA1, bank accounts from LFBK, IBAN from LFBK or TIBAN; several accounts joined by ";".
    lfa1 = read(src, "LFA1", ["LIFNR", "NAME1", "ERDAT"])
    lfbk = read(src, "LFBK", ["LIFNR"], optional=True)
    ibans = {}
    if lfbk is not None:
        if "IBAN" not in lfbk.columns:
            tiban = read(src, "TIBAN", ["BANKS", "BANKL", "BANKN", "IBAN"], optional=True)
            lfbk = lfbk.merge(tiban, on=["BANKS", "BANKL", "BANKN"], how="left") if tiban is not None else lfbk
        if "IBAN" in lfbk.columns:
            for v, g in lfbk.groupby(lfbk["LIFNR"].map(key)):
                ibans[v] = ";".join(sorted({i for i in g["IBAN"].fillna("") if i}))
    if not ibans:
        warn.append("No vendor IBANs (LFBK with TIBAN): rule R4 (vendor bank = staff bank) cannot run")
    vendors = pd.DataFrame({
        "vendor_id": lfa1["LIFNR"].map(key),
        "name": lfa1["NAME1"],
        "cr_number": lfa1.get("STCD1", ""),
        "bank_iban": lfa1["LIFNR"].map(key).map(ibans).fillna(""),
        "created_date": lfa1["ERDAT"].map(date).dt.strftime("%Y-%m-%d"),
    })

    # Staff bank accounts: HR infotype 0009, optional.
    pa0009 = read(src, "PA0009", ["PERNR"], optional=True)
    if pa0009 is not None and "IBAN" in pa0009.columns:
        staff = pd.DataFrame({"staff_id": pa0009["PERNR"], "name": "", "department": "",
                              "bank_iban": pa0009["IBAN"]}).drop_duplicates()
    else:
        staff = None
        warn.append("No PA0009 with IBAN: staff bank accounts not compared (rule R4 skipped)")

    # Approval limits come from the company's authority matrix, not from SAP tables.
    limits_src = src / "approval_limits.csv"
    if not limits_src.exists():
        warn.append("No approval_limits.csv: wrote placeholder limits 10,000 / 50,000 / 250,000; "
                    "replace them with the company's authority matrix before running the review")
        limits = "level,limit_sar\nLevel 1,10000\nLevel 2,50000\nLevel 3,250000\n"
    else:
        limits = limits_src.read_text(encoding="utf-8")

    out.mkdir(parents=True, exist_ok=True)
    po.to_csv(out / "purchase_orders.csv", index=False)
    invoices.to_csv(out / "invoices.csv", index=False)
    vendors.to_csv(out / "vendors.csv", index=False)
    if staff is not None:
        staff.to_csv(out / "staff.csv", index=False)
    (out / "approval_limits.csv").write_text(limits, encoding="utf-8")
    if (src / "answer_key.csv").exists():  # test data only
        (out / "answer_key.csv").write_text((src / "answer_key.csv").read_text(encoding="utf-8"), encoding="utf-8")

    report = [f"Mapped {src} -> {out}",
              f"purchase_orders.csv: {len(po)} lines on {po['po_id'].nunique()} POs",
              f"invoices.csv: {len(invoices)}", f"vendors.csv: {len(vendors)}",
              f"staff.csv: {0 if staff is None else len(staff)}", "", "Notes:", *[f"- {n}" for n in notes],
              "", "Warnings:", *([f"- {w}" for w in warn] or ["- none"])]
    (out / "mapping_report.txt").write_text("\n".join(report) + "\n", encoding="utf-8")
    print("\n".join(report))


if __name__ == "__main__":
    if len(sys.argv) != 3:
        raise SystemExit(__doc__)
    run(Path(sys.argv[1]), Path(sys.argv[2]))
