"""Shared helpers for the ERP mappers: reading exports, parsing numbers and dates, writing the review input."""
import re

import pandas as pd

ISO_FORMATS = ("%Y-%m-%d %H:%M:%S", "%Y-%m-%d %H:%M", "%Y-%m-%d", "%Y-%m-%dT%H:%M:%S", "%Y-%m-%dT%H:%M:%SZ",
               "%d-%b-%y", "%d-%b-%Y", "%d %b %Y", "%Y%m%d")
_TIMES = ("", " %H:%M:%S", " %H:%M", " %I:%M:%S %p", " %I:%M %p")
# Slash and dot dates are tried in one order only, so a column never mixes day-first and month-first.
DAY_FIRST = ISO_FORMATS + tuple(d + t for d in ("%d/%m/%Y", "%d.%m.%Y", "%d-%m-%Y") for t in _TIMES)
MONTH_FIRST = ISO_FORMATS + tuple(d + t for d in ("%m/%d/%Y", "%m.%d.%Y", "%m-%d-%Y") for t in _TIMES)
PLACEHOLDER_LIMITS = "level,limit_sar\nLevel 1,10000\nLevel 2,50000\nLevel 3,250000\n"


def read(folder, names, required=(), optional=False, rename=None, upper=False, fill=(), warn=None):
    """Read the first export found among `names` (.csv, .xlsx, .xls, .txt), with columns stripped.

    rename maps any of several header spellings to one internal name: {"internal": ["Label", "field"]}.
    fill lists columns to fill down, for exports that write child rows with blank parent columns.
    An optional file missing a required column is ignored with a warning instead of stopping the run.
    """
    names = [names] if isinstance(names, str) else names
    for name in names:
        for ext in (".csv", ".xlsx", ".xls", ".txt"):
            f = folder / f"{name}{ext}"
            if not f.exists():
                continue
            if ext.startswith(".xls"):
                df = pd.read_excel(f, dtype=str)
            else:
                df = pd.read_csv(f, dtype=str, sep=None, engine="python", encoding="utf-8-sig")
            df.columns = [str(c).strip().upper() if upper else str(c).strip() for c in df.columns]
            if rename:
                hits = {}
                for internal, spellings in rename.items():
                    hit = next((c for c in spellings if c in df.columns), None)
                    if hit and hit not in hits:
                        hits[hit] = internal
                df = df.rename(columns=hits)
            df = df.apply(lambda c: c.str.strip() if c.dtype == object else c)
            cols = [c for c in fill if c in df.columns]
            if cols:
                df[cols] = df[cols].replace("", pd.NA).ffill()
            df = df.fillna("")
            missing = [c for c in required if c not in df.columns]
            if missing:
                hint = {m: rename[m][:3] for m in missing} if rename else missing
                if optional:
                    if warn is not None:
                        warn.append(f"{f.name} ignored: missing columns {hint}")
                    return None
                raise SystemExit(f"{f.name}: missing columns {hint}; found {list(df.columns)}")
            df.attrs["file"] = f.name
            return df
    if optional:
        return None
    raise SystemExit(f"Missing {' or '.join(names)} (.csv or .xlsx) in {folder}")


def num(v):
    """Parse 1,234.56 / 1.234,56 / (1,234.56) / 1234.56- / SAR 1,234.56 into a float."""
    s = str(v).strip().replace(" ", "").replace(" ", "")
    if s in ("", "nan", "-"):
        return float("nan")
    neg = s.startswith("(") and s.endswith(")") or s.endswith("-") or s.startswith("-")
    s = re.sub(r"[^\d.,]", "", s)
    if not s:
        return float("nan")
    if s.count(".") > 1 and "," not in s:      # 1.234.567: dots are thousands separators
        s = s.replace(".", "")
    if s.count(",") > 1 and "." not in s:      # 1,234,567
        s = s.replace(",", "")
    if re.fullmatch(r"[\d.]*,\d{1,3}", s) and not re.fullmatch(r"\d{1,3}(,\d{3})+", s):
        s = s.replace(".", "").replace(",", ".")
    else:
        s = s.replace(",", "")
    return -float(s) if neg else float(s)


def when(v, offset=None, dayfirst=True):
    """Parse a date or datetime in the common ERP formats; shift by offset (a Timedelta) if given.

    Dates before 1901 are ERP placeholders for "no date" (Dynamics writes 1900-01-01) and return NaT.
    """
    s = str(v).strip()
    if not s or set(s) <= set("0./-: "):
        return pd.NaT
    t = pd.NaT
    for fmt in DAY_FIRST if dayfirst else MONTH_FIRST:
        try:
            t = pd.to_datetime(s, format=fmt)
            break
        except ValueError:
            continue
    if pd.isna(t):
        t = pd.to_datetime(s, errors="coerce", dayfirst=dayfirst)
    if pd.notna(t) and getattr(t, "tzinfo", None) is not None:
        t = t.tz_convert(None)
    if pd.notna(t) and t.year < 1901:
        return pd.NaT
    return t + offset if offset is not None and pd.notna(t) else t


def date_order(*columns):
    """Decide day-first or month-first from all the date columns of one export set, pooled.

    Excel exports follow the user's locale, so 08/01/2026 is 1 August in the US and 8 January elsewhere.
    Any value whose first part is over 12 proves day-first; any whose second part is over 12 proves
    month-first. Returns (dayfirst, proven). With no proof, day-first is assumed (Saudi and European
    settings) and proven is False, so the mapper can warn.
    """
    firsts, seconds = [], []
    for col in columns:
        for v in col:
            m = re.match(r"^\s*(\d{1,2})[/.-](\d{1,2})[/.-]\d{2,4}", str(v))
            if m:
                firsts.append(int(m.group(1)))
                seconds.append(int(m.group(2)))
    day, month = any(f > 12 for f in firsts), any(x > 12 for x in seconds)
    if month and not day:
        return False, True
    return True, day or not firsts


def dates(series, offset=None, dayfirst=None):
    """Parse a whole column; the order comes from dayfirst, or from the column itself when not given."""
    if dayfirst is None:
        dayfirst, _ = date_order(series)
    return series.map(lambda v: when(v, offset, dayfirst=dayfirst))


def has_time(t):
    return pd.notna(t) and (t.hour, t.minute, t.second) != (0, 0, 0)


def ymd(series):
    return pd.to_datetime(series).dt.strftime("%Y-%m-%d").fillna("")


def ymdhm(series):
    return pd.to_datetime(series).dt.strftime("%Y-%m-%d %H:%M").fillna("")


def approval_limits(src, warn, hint):
    f = src / "approval_limits.csv"
    if f.exists():
        return f.read_text(encoding="utf-8")
    warn.append("No approval_limits.csv: wrote placeholder limits 10,000 / 50,000 / 250,000; replace them with "
                f"the company's authority matrix ({hint}) before running the review")
    return PLACEHOLDER_LIMITS


def write(src, out, title, po, invoices, vendors, staff, limits, notes, warn):
    """Write the five review CSVs and mapping_report.txt, and print the report."""
    out.mkdir(parents=True, exist_ok=True)
    po.to_csv(out / "purchase_orders.csv", index=False)
    invoices.to_csv(out / "invoices.csv", index=False)
    vendors.to_csv(out / "vendors.csv", index=False)
    if staff is not None:
        staff.to_csv(out / "staff.csv", index=False)
    elif (out / "staff.csv").exists():
        (out / "staff.csv").unlink()
    (out / "approval_limits.csv").write_text(limits, encoding="utf-8")
    if (src / "answer_key.csv").exists():  # test data only
        (out / "answer_key.csv").write_text((src / "answer_key.csv").read_text(encoding="utf-8"), encoding="utf-8")
    report = [f"{title}: {src} -> {out}",
              f"purchase_orders.csv: {len(po)} lines on {po['po_id'].nunique()} POs",
              f"invoices.csv: {len(invoices)}", f"vendors.csv: {len(vendors)}",
              f"staff.csv: {0 if staff is None else len(staff)}", "", "Notes:", *[f"- {n}" for n in notes],
              "", "Warnings:", *([f"- {w}" for w in warn] or ["- none"])]
    (out / "mapping_report.txt").write_text("\n".join(report) + "\n", encoding="utf-8")
    print("\n".join(report))


PO_COLUMNS = ["po_id", "po_date", "vendor_id", "category", "item", "quantity", "unit_price", "amount",
              "requester_id", "approver_id", "approved_at"]
INVOICE_COLUMNS = ["invoice_no", "vendor_id", "po_id", "invoice_date", "amount", "paid_date"]
VENDOR_COLUMNS = ["vendor_id", "name", "cr_number", "bank_iban", "created_date"]
