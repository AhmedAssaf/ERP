"""End-to-end check of every mapper: regenerate each sample, map it, run the review, compare with the answer key.

Usage: python test_mappers.py        (exit code 1 if any case falls short)

Each case expects the planted red flags its ERP's standard exports can show, and an exact number of findings,
so a new false alarm fails the test too. ERPNext and Zoho Books have no approval history export, so the five
approval findings (R7) cannot surface; Zoho Books also has no bank accounts in its exports, so the
conflict-of-interest finding (R4) cannot either, and without a requester the split-order rule (R1) raises
four extra questions on the sample (three ordinary orders to one vendor within 14 days).
"""
import contextlib
import io
import subprocess
import sys
from pathlib import Path

import review

HERE = Path(__file__).parent
OUT = HERE / "output" / "test"
PY = sys.executable

GENERATORS = ["generate_sample.py", "to_sap.py", "to_odoo.py", "to_oracle.py", "to_dynamics.py", "to_generic.py"]
CASES = [  # name, mapper command (None = the sample itself), planted red flags found, total findings
    ("sample (no mapping)", None, 21, 21),
    ("SAP", ["map_sap.py", "sap_sample"], 21, 21),
    ("Odoo", ["map_odoo.py", "odoo_sample"], 21, 21),
    ("Oracle EBS", ["map_oracle.py", "oracle_sample"], 21, 21),
    ("Dynamics 365 F&O", ["map_dynamics.py", "dynamics_fo_sample"], 21, 21),
    ("Dynamics 365 Business Central", ["map_dynamics.py", "dynamics_bc_sample"], 21, 21),
    ("NetSuite (profile)", ["map_generic.py", "profiles/netsuite.json", "netsuite_sample"], 21, 21),
    ("ERPNext (profile)", ["map_generic.py", "profiles/erpnext.json", "erpnext_sample"], 16, 16),
    ("Zoho Books (profile)", ["map_generic.py", "profiles/zoho_books.json", "zoho_books_sample"], 15, 19),
]


def check_helpers():
    """Regression checks for parsing cases the sample data does not exercise."""
    import tempfile

    import pandas as pd

    from mapcommon import date_order, dates, num, read, when
    d = list(dates(pd.Series(["05/03/2026 2:10 pm", "20/03/2026 9:00 am"])))
    assert (d[0].month, d[0].hour, d[1].month) == (3, 14, 3), d            # 12-hour times stay day-first
    assert date_order(pd.Series(["3/15/2026"]), pd.Series(["3/5/2026"])) == (False, True)
    assert date_order(pd.Series(["3/5/2026"]))[1] is False                 # no proof: caller must warn
    assert pd.isna(when("1900-01-01T00:00:00"))                            # Dynamics "no date"
    for text, value in (("1.234.567", 1234567), ("1,234,567", 1234567), ("1,234.56", 1234.56),
                        ("1.234,56", 1234.56), ("(500.00)", -500), ("100,00-", -100), ("SAR 2,500", 2500)):
        assert num(text) == value, (text, num(text))
    with tempfile.TemporaryDirectory() as tmp:                             # optional file, missing column: warn
        pd.DataFrame({"Document No.": ["X"]}).to_csv(Path(tmp) / "Vendor Ledger Entries.csv", index=False)
        warn = []
        assert read(Path(tmp), "Vendor Ledger Entries", required=["Closed at Date"], optional=True, warn=warn) is None
        assert warn and "ignored" in warn[0]
    print("helper checks: ok")


def main():
    check_helpers()
    for g in GENERATORS:
        subprocess.run([PY, g], cwd=HERE, check=True, capture_output=True)
    rows, failed = [], False
    for name, cmd, expected, findings in CASES:
        slug = name.split(" (")[0].lower().replace(" ", "_").replace("&", "and")
        mapped = HERE / "sample" if cmd is None else OUT / slug / "mapped"
        warnings = ""
        if cmd:
            r = subprocess.run([PY, *cmd, str(mapped)], cwd=HERE, capture_output=True, text=True)
            if r.returncode:
                rows.append((name, "-", "-", f"mapper failed: {r.stderr.strip().splitlines()[-1]}"))
                failed = True
                continue
            report = (mapped / "mapping_report.txt").read_text(encoding="utf-8")
            warnings = str(report.split("Warnings:")[1].count("\n- ") - report.split("Warnings:")[1].count("- none"))
        with contextlib.redirect_stdout(io.StringIO()):
            res = review.run(mapped, OUT / slug / "report")
        ok = res["hit"] == expected and res["findings"] == findings
        failed |= not ok
        rows.append((name, f"{res['hit']} of {res['planted']} (expect {expected})", f"{res['findings']} (expect {findings})",
                     ("ok" if ok else "FAIL") + (f", {warnings} warnings" if warnings else "")))
    width = [max(len(r[i]) for r in rows + [("ERP", "Planted found", "Findings", "Result")]) for i in range(4)]
    for r in [("ERP", "Planted found", "Findings", "Result")] + rows:
        print("  ".join(c.ljust(w) for c, w in zip(r, width)))
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
