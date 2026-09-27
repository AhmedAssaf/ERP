"""Generate a fictional quarter of purchasing data with planted red flags.

Writes CSV files to sample/ in the shape review.py expects, plus answer_key.csv
listing every planted anomaly so the review can be checked against it.
All companies, people, and bank accounts are invented.
"""
import csv
import random
from datetime import date, datetime, timedelta
from pathlib import Path

SEED = 2026
OUT = Path(__file__).parent / "sample"
Q_START = date(2026, 7, 1)
Q_END = date(2026, 9, 30)
LIMITS = [("Manager", 10_000), ("Director", 50_000), ("CFO", 250_000)]

rng = random.Random(SEED)
answer_key = []


def iban():
    return "SA" + "".join(str(rng.randint(0, 9)) for _ in range(22))


def workday():
    """Random Sunday-to-Thursday date inside the quarter."""
    while True:
        d = Q_START + timedelta(days=rng.randint(0, (Q_END - Q_START).days))
        if d.weekday() not in (4, 5):  # Friday, Saturday
            return d


def office_time(d):
    return datetime(d.year, d.month, d.day, rng.randint(8, 16), rng.randint(0, 59))


def plant(rule, ref, note):
    answer_key.append({"rule": rule, "reference": ref, "note": note})


# Staff
FIRST = ["Ahmed", "Mohammed", "Khalid", "Fahad", "Saad", "Omar", "Faisal", "Sultan", "Nasser", "Turki",
         "Noura", "Sara", "Reem", "Lama", "Hind", "Maha", "Abdullah", "Majed", "Yousef", "Hamad"]
LAST = ["Al-Harbi", "Al-Otaibi", "Al-Qahtani", "Al-Ghamdi", "Al-Zahrani", "Al-Shehri", "Al-Dosari",
        "Al-Mutairi", "Al-Anazi", "Al-Shammari"]
staff = []
for i in range(1, 41):
    staff.append({"staff_id": f"E{i:03d}", "name": f"{rng.choice(FIRST)} {rng.choice(LAST)}",
                  "department": rng.choice(["Procurement", "Projects", "Finance", "Operations", "IT"]),
                  "bank_iban": iban()})
requesters = [s["staff_id"] for s in staff if s["department"] in ("Projects", "Operations", "IT")]
approvers = [s["staff_id"] for s in staff if s["department"] in ("Procurement", "Finance")]

# Vendors and catalogue
CATEGORIES = {
    "Office supplies": [("A4 paper box", 95), ("Toner cartridge", 420), ("Office chair", 780)],
    "IT hardware": [("Laptop", 4_600), ("Monitor 27in", 1_150), ("Network switch", 3_900)],
    "Building materials": [("Cement bag 50kg", 19), ("Rebar ton", 2_850), ("Ready-mix m3", 265)],
    "Facility services": [("Cleaning visit", 1_350), ("AC maintenance visit", 900), ("Pest control visit", 650)],
    "Catering": [("Staff lunch box", 32), ("Event catering per head", 145), ("Water carton", 14)],
    "Security services": [("Guard shift", 480), ("CCTV maintenance", 2_200), ("Access card batch", 1_700)],
}
WORDS = ["Noor", "Wadi", "Rimal", "Najd", "Sahab", "Jazeera", "Masar", "Afaq", "Bina", "Taqa",
         "Rawabi", "Madar", "Dana", "Qimma", "Sarh", "Nakheel", "Waha", "Shams", "Buruj", "Tamayuz"]
SUFFIX = ["Trading", "Contracting", "Services", "Est.", "Supplies", "Solutions"]
vendors = []
vendor_cats = {}
names_used = set()
for i, cat in enumerate(CATEGORIES):
    for k in range(9):
        while True:
            name = f"{rng.choice(WORDS)} {rng.choice(WORDS)} {rng.choice(SUFFIX)} (fictional)"
            if name not in names_used:
                names_used.add(name)
                break
        vid = f"V{len(vendors) + 1:03d}"
        created = date(2023, 1, 1) + timedelta(days=rng.randint(0, 900))
        vendors.append({"vendor_id": vid, "name": name, "cr_number": str(rng.randint(1010000000, 1019999999)),
                        "bank_iban": iban(), "created_date": created.isoformat()})
        vendor_cats[vid] = cat

pos, invoices = [], []
inv_counter = {}


def next_invoice_no(vid):
    # Normal vendors have gaps between their invoice numbers (they bill other customers too).
    inv_counter[vid] = inv_counter.get(vid, rng.randint(1000, 5000)) + rng.randint(7, 60)
    return f"INV-{inv_counter[vid]}"


def add_po(d, vid, item, qty, unit_price, requester=None, approver=None, approved_at=None,
           invoice_no=None, invoice_amount=None, paid_offset=None):
    po_id = f"PO-{len(pos) + 1:05d}"
    amount = round(qty * unit_price, 2)
    requester = requester or rng.choice(requesters)
    approver = approver or rng.choice([a for a in approvers if a != requester])
    approved_at = approved_at or office_time(d)
    pos.append({"po_id": po_id, "po_date": d.isoformat(), "vendor_id": vid, "category": vendor_cats[vid],
                "item": item, "quantity": qty, "unit_price": unit_price, "amount": amount,
                "requester_id": requester, "approver_id": approver,
                "approved_at": approved_at.strftime("%Y-%m-%d %H:%M")})
    inv_date = d + timedelta(days=rng.randint(3, 20))
    paid = inv_date + timedelta(days=paid_offset if paid_offset is not None else rng.randint(10, 45))
    invoices.append({"invoice_no": invoice_no or next_invoice_no(vid), "vendor_id": vid, "po_id": po_id,
                     "invoice_date": inv_date.isoformat(),
                     "amount": invoice_amount if invoice_amount is not None else amount,
                     "paid_date": paid.isoformat()})
    return po_id


def normal_price(base):
    return round(base * rng.uniform(0.92, 1.08), 2)


def normal_qty(base):
    target = rng.uniform(1_500, 40_000)
    return max(1, int(target / base))


def not_round(q, p):
    return (q * p) % 1000 != 0


# 1. Normal purchasing: spread across several vendors per category
for _ in range(1_100):
    vid = rng.choice(vendors)["vendor_id"]
    item, base = rng.choice(CATEGORIES[vendor_cats[vid]])
    p = normal_price(base)
    q = normal_qty(base)
    if not not_round(q, p):
        p += 0.37
    add_po(workday(), vid, item, q, p)

# Planted: split orders just under an approval limit (rule 1)
for vid_i, limit in ((3, 50_000), (21, 10_000)):
    vid = vendors[vid_i]["vendor_id"]
    item, base = CATEGORIES[vendor_cats[vid]][0]
    d0 = workday()
    req = rng.choice(requesters)
    group = []
    for j in range(3):
        amount_target = limit * rng.uniform(0.88, 0.98)
        p = normal_price(base)
        q = max(1, int(amount_target / p))
        d = d0 + timedelta(days=j * 2)
        while d.weekday() in (4, 5):
            d += timedelta(days=1)
        group.append(add_po(d, vid, item, q, p, requester=req))
    plant("R1 split orders", ";".join(group), f"3 POs to {vid} just under SAR {limit:,}")

# Planted: single source, one vendor takes all Security services spend (rule 2)
sole = next(v["vendor_id"] for v in vendors if vendor_cats[v["vendor_id"]] == "Security services")
for po in pos:
    if po["category"] == "Security services" and po["vendor_id"] != sole:
        po["vendor_id"] = sole
for inv in invoices:
    if inv["vendor_id"] in {v["vendor_id"] for v in vendors if vendor_cats[v["vendor_id"]] == "Security services"}:
        inv["vendor_id"] = sole
plant("R2 single source", sole, "All Security services POs went to one vendor")

# Planted: duplicate invoices (rule 3)
for k in range(3):
    src = rng.choice(invoices[:1000])
    dup = dict(src)
    if k == 0:
        dup["invoice_date"] = (date.fromisoformat(src["invoice_date"]) + timedelta(days=1)).isoformat()
        dup["invoice_no"] = src["invoice_no"] + "A"
    dup["paid_date"] = (date.fromisoformat(src["paid_date"]) + timedelta(days=rng.randint(2, 9))).isoformat()
    invoices.append(dup)
    plant("R3 duplicate invoice", src["invoice_no"], f"Paid twice to {src['vendor_id']}")

# Planted: vendor bank account equals a staff bank account (rule 4)
conflict_vendor = vendors[40]
conflict_staff = next(s for s in staff if s["department"] == "Procurement")
conflict_vendor["bank_iban"] = conflict_staff["bank_iban"]
plant("R4 vendor bank = staff bank", conflict_vendor["vendor_id"], f"Same IBAN as staff {conflict_staff['staff_id']}")

# Planted: new vendors paid within days of creation (rule 5)
for k in range(2):
    d = workday()
    vid = f"V{len(vendors) + 1:03d}"
    cat = "IT hardware" if k == 0 else "Facility services"
    vendors.append({"vendor_id": vid, "name": f"{rng.choice(WORDS)} Quick {rng.choice(SUFFIX)} (fictional)",
                    "cr_number": str(rng.randint(1010000000, 1019999999)), "bank_iban": iban(),
                    "created_date": (d - timedelta(days=1)).isoformat()})
    vendor_cats[vid] = cat
    item, base = CATEGORIES[cat][0]
    po = add_po(d, vid, item, normal_qty(base), normal_price(base), paid_offset=0)
    invoices[-1]["invoice_date"] = d.isoformat()
    invoices[-1]["paid_date"] = (d + timedelta(days=2)).isoformat()
    plant("R5 new vendor paid fast", vid, f"Created {d - timedelta(days=1)}, paid within 3 days ({po})")

# Planted: price outliers (rule 6)
for k in range(6):
    vid = rng.choice(vendors[:54])["vendor_id"]
    item, base = CATEGORIES[vendor_cats[vid]][rng.randint(0, 2)]
    po = add_po(workday(), vid, item, normal_qty(base), round(base * rng.uniform(1.7, 2.4), 2))
    plant("R6 price outlier", po, f"{item} at about 2x the median")

# Planted: approval problems (rule 7)
for k in range(5):
    vid = rng.choice(vendors[:54])["vendor_id"]
    item, base = CATEGORIES[vendor_cats[vid]][0]
    d = workday()
    if k < 2:
        who = rng.choice(approvers)
        po = add_po(d, vid, item, normal_qty(base), normal_price(base), requester=who, approver=who)
        plant("R7 approval control", po, "Requester approved own PO")
    elif k < 4:
        late = datetime(d.year, d.month, d.day, 23, rng.randint(0, 59))
        po = add_po(d, vid, item, normal_qty(base), normal_price(base), approved_at=late)
        plant("R7 approval control", po, "Approved at night")
    else:
        fri = d + timedelta(days=(4 - d.weekday()) % 7)
        po = add_po(d, vid, item, normal_qty(base), normal_price(base),
                    approved_at=datetime(fri.year, fri.month, fri.day, 13, 5))
        plant("R7 approval control", po, "Approved on Friday")

# Planted: round and sequential invoices from one vendor (rule 8)
rv = vendors[30]["vendor_id"]
start = rng.randint(100, 200)
item = "Monthly maintenance contract"
for k in range(5):
    amount = rng.choice([15_000, 20_000, 25_000, 30_000])
    add_po(workday(), rv, item, 1, amount, invoice_no=f"INV-{start + k}", invoice_amount=amount)
plant("R8 round or sequential invoices", rv, "5 round-number invoices numbered one after another")

OUT.mkdir(exist_ok=True)


def write(name, rows):
    with open(OUT / name, "w", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=list(rows[0].keys()))
        w.writeheader()
        w.writerows(rows)


write("vendors.csv", vendors)
write("staff.csv", staff)
write("purchase_orders.csv", pos)
write("invoices.csv", invoices)
write("approval_limits.csv", [{"level": n, "limit_sar": v} for n, v in LIMITS])
write("answer_key.csv", answer_key)
print(f"Wrote {len(pos)} POs, {len(invoices)} invoices, {len(vendors)} vendors, {len(staff)} staff to {OUT}")
print(f"Planted {len(answer_key)} anomalies (answer_key.csv)")
