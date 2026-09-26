"""Turn the fictional sample into a realistic SAP table export, to test map_sap.py end to end.

Reads sample/ and writes sap_sample/ as tab-delimited .txt files named after SAP tables, the way
SE16N or an ALV "unconverted" download produces them: technical column names, dd.mm.yyyy dates,
German number format (1.234,56 and a trailing minus for negatives), leading zeros in keys, price
units other than 1, one PO with two lines, one reversed invoice pair, and change documents that
are not releases. The answer key is translated to SAP keys so the review can still score itself.
"""
import csv
from pathlib import Path

import pandas as pd

HERE = Path(__file__).parent
SRC, OUT = HERE / "sample", HERE / "sap_sample"
GJAHR = "2026"
BUKRS = "1000"


def de(x, decimals=2):
    """German number format: 1.234,56 with a trailing minus for negatives."""
    s = f"{abs(x):,.{decimals}f}".replace(",", "X").replace(".", ",").replace("X", ".")
    return s + ("-" if x < 0 else "")


def d(iso):
    return "" if pd.isna(iso) or iso == "" else pd.Timestamp(iso).strftime("%d.%m.%Y")


def write(name, rows, cols):
    with open(OUT / f"{name}.txt", "w", newline="", encoding="utf-8") as f:
        w = csv.writer(f, delimiter="\t")
        w.writerow(cols)
        w.writerows([[r[c] for c in cols] for r in rows])


pos = pd.read_csv(SRC / "purchase_orders.csv", dtype=str)
inv = pd.read_csv(SRC / "invoices.csv", dtype=str)
ven = pd.read_csv(SRC / "vendors.csv", dtype=str)
staff = pd.read_csv(SRC / "staff.csv", dtype=str)
key = pd.read_csv(SRC / "answer_key.csv", dtype=str)
OUT.mkdir(exist_ok=True)

lifnr = {v: f"{100000 + int(v[1:]):010d}" for v in ven["vendor_id"]}
ebeln = {p: str(4500000000 + int(p.split("-")[1])) for p in pos["po_id"]}
user = lambda staff_id: f"USR{staff_id[1:]}"
cats = sorted(pos["category"].unique())
matkl = {c: f"MG{i + 1:02d}" for i, c in enumerate(cats)}
items = sorted(pos["item"].unique())
matnr = {it: f"{100000 + i + 1:018d}" for i, it in enumerate(items)}

# EKKO header, EKPO lines, CDHDR and CDPOS release change documents
ekko, ekpo, cdhdr, cdpos = [], [], [], []
changenr = 1000
split_done = False
for _, p in pos.iterrows():
    n = ebeln[p["po_id"]]
    ekko.append({"BUKRS": BUKRS, "EBELN": n, "BSART": "NB", "LIFNR": lifnr[p["vendor_id"]],
                 "BEDAT": d(p["po_date"]), "ERNAM": user(p["requester_id"])})
    qty, price, amount = float(p["quantity"]), float(p["unit_price"]), float(p["amount"])
    peinh = 100 if price < 50 else 1
    lines = [(qty, amount)]
    if not split_done and qty >= 10 and p["category"] == "Office supplies":
        a = int(qty // 2)
        lines = [(a, round(a * price, 2)), (qty - a, round(amount - round(a * price, 2), 2))]
        split_done = True
    for i, (q, amt) in enumerate(lines):
        ekpo.append({"EBELN": n, "EBELP": f"{(i + 1) * 10:05d}", "LOEKZ": "", "MATNR": matnr[p["item"]],
                     "TXZ01": p["item"], "MATKL": matkl[p["category"]], "MENGE": de(q, 3),
                     "NETPR": de(price * peinh), "PEINH": str(peinh), "NETWR": de(amt)})
    t = pd.Timestamp(p["approved_at"])
    changenr += 1
    cdhdr.append({"OBJECTCLAS": "EINKBELEG", "OBJECTID": n, "CHANGENR": f"{changenr:010d}",
                  "USERNAME": user(p["approver_id"]), "UDATE": t.strftime("%d.%m.%Y"),
                  "UTIME": t.strftime("%H:%M:%S"), "TCODE": "ME29N"})
    cdpos.append({"OBJECTCLAS": "EINKBELEG", "OBJECTID": n, "CHANGENR": f"{changenr:010d}",
                  "TABNAME": "EKKO", "FNAME": "FRGKE", "VALUE_NEW": "R"})
    if int(n) % 25 == 0:  # a later edit that is not a release; the mapper must ignore it
        changenr += 1
        later = t + pd.Timedelta(days=1, hours=14)
        cdhdr.append({"OBJECTCLAS": "EINKBELEG", "OBJECTID": n, "CHANGENR": f"{changenr:010d}",
                      "USERNAME": "USR999", "UDATE": later.strftime("%d.%m.%Y"),
                      "UTIME": later.strftime("%H:%M:%S"), "TCODE": "ME22N"})
        cdpos.append({"OBJECTCLAS": "EINKBELEG", "OBJECTID": n, "CHANGENR": f"{changenr:010d}",
                      "TABNAME": "EKPO", "FNAME": "EINDT", "VALUE_NEW": ""})

# RBKP invoice headers, RSEG invoice lines, BKPF accounting headers, BSAK cleared vendor items
rbkp, rseg, bkpf, bsak = [], [], [], []
extra = [dict(inv.iloc[0]) | {"invoice_no": "INV-REV-1", "reversal": "1"}]  # reversed pair, must be dropped
for i, v in enumerate(list(inv.to_dict("records")) + extra):
    belnr, fi = str(5105600000 + i), str(5100000000 + i)
    stblg = str(5105699999) if v.get("reversal") else ""
    rbkp.append({"BELNR": belnr, "GJAHR": GJAHR, "BUKRS": BUKRS, "LIFNR": lifnr[v["vendor_id"]],
                 "XBLNR": v["invoice_no"], "BLDAT": d(v["invoice_date"]), "RMWWR": de(float(v["amount"])),
                 "WAERS": "SAR", "STBLG": stblg})
    rseg.append({"BELNR": belnr, "GJAHR": GJAHR, "BUZEI": "000001", "EBELN": ebeln[v["po_id"]],
                 "EBELP": "00010", "WRBTR": de(float(v["amount"]))})
    bkpf.append({"BUKRS": BUKRS, "BELNR": fi, "GJAHR": GJAHR, "BLART": "RE", "AWTYP": "RMRP",
                 "AWKEY": belnr + GJAHR})
    if not v.get("reversal"):
        bsak.append({"BUKRS": BUKRS, "LIFNR": lifnr[v["vendor_id"]], "BELNR": fi, "GJAHR": GJAHR,
                     "AUGDT": d(v["paid_date"]), "XBLNR": v["invoice_no"]})

# LFA1 vendor master, LFBK vendor bank details, TIBAN IBANs
lfa1, lfbk, tiban = [], [], []
for _, v in ven.iterrows():
    l = lifnr[v["vendor_id"]]
    lfa1.append({"LIFNR": l, "NAME1": v["name"], "STCD1": v["cr_number"], "ERDAT": d(v["created_date"])})
    bankn = v["bank_iban"][-12:]
    lfbk.append({"LIFNR": l, "BANKS": "SA", "BANKL": v["bank_iban"][4:6], "BANKN": bankn})
    tiban.append({"BANKS": "SA", "BANKL": v["bank_iban"][4:6], "BANKN": bankn, "IBAN": v["bank_iban"]})

# PA0009 HR bank details (optional, only for the conflict-of-interest check)
pa0009 = [{"PERNR": f"{1000 + int(s['staff_id'][1:]):08d}", "SUBTY": "0", "IBAN": s["bank_iban"]}
          for _, s in staff.iterrows()]

write("EKKO", ekko, ["BUKRS", "EBELN", "BSART", "LIFNR", "BEDAT", "ERNAM"])
write("EKPO", ekpo, ["EBELN", "EBELP", "LOEKZ", "MATNR", "TXZ01", "MATKL", "MENGE", "NETPR", "PEINH", "NETWR"])
write("T023T", [{"SPRAS": "E", "MATKL": m, "WGBEZ": c} for c, m in matkl.items()], ["SPRAS", "MATKL", "WGBEZ"])
write("CDHDR", cdhdr, ["OBJECTCLAS", "OBJECTID", "CHANGENR", "USERNAME", "UDATE", "UTIME", "TCODE"])
write("CDPOS", cdpos, ["OBJECTCLAS", "OBJECTID", "CHANGENR", "TABNAME", "FNAME", "VALUE_NEW"])
write("RBKP", rbkp, ["BELNR", "GJAHR", "BUKRS", "LIFNR", "XBLNR", "BLDAT", "RMWWR", "WAERS", "STBLG"])
write("RSEG", rseg, ["BELNR", "GJAHR", "BUZEI", "EBELN", "EBELP", "WRBTR"])
write("BKPF", bkpf, ["BUKRS", "BELNR", "GJAHR", "BLART", "AWTYP", "AWKEY"])
write("BSAK", bsak, ["BUKRS", "LIFNR", "BELNR", "GJAHR", "AUGDT", "XBLNR"])
write("LFA1", lfa1, ["LIFNR", "NAME1", "STCD1", "ERDAT"])
write("LFBK", lfbk, ["LIFNR", "BANKS", "BANKL", "BANKN"])
write("TIBAN", tiban, ["BANKS", "BANKL", "BANKN", "IBAN"])
write("PA0009", pa0009, ["PERNR", "SUBTY", "IBAN"])
(OUT / "approval_limits.csv").write_text((SRC / "approval_limits.csv").read_text(encoding="utf-8"), encoding="utf-8")


def sap_ref(ref):
    parts = []
    for r in ref.split(";"):
        if r in ebeln:
            parts.append(ebeln[r])
        elif r in lifnr:
            parts.append(lifnr[r].lstrip("0"))
        else:
            parts.append(r)
    return ";".join(parts)


key["reference"] = key["reference"].map(sap_ref)
key.to_csv(OUT / "answer_key.csv", index=False)
print(f"Wrote SAP-style export to {OUT}: {len(ekko)} POs, {len(ekpo)} PO lines, {len(rbkp)} invoices "
      f"(1 reversed), {len(cdhdr)} change documents")
