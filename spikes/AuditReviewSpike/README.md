# Audit review spike (docs/16, idea 7)

A demo of the procurement audit review: eight red-flag rules run over one ERP export, producing ranked findings and a printable report. It is a sales and learning tool for the first manual reviews, not product code (see CLAUDE.md on spikes).

```
python generate_sample.py      # writes sample/: a fictional quarter with 21 planted red flags
python review.py               # reads sample/, writes output/findings.csv and output/report.html
python review.py <in> <out>    # run on a real export folder
```

Needs Python 3.11+ and pandas. Open `output/report.html` in a browser and print it to PDF for the customer. On the sample data the review finds all 21 planted red flags with no false alarms (it checks itself against `sample/answer_key.csv` when that file is present).

## Input: five CSV files

| File | Columns |
|---|---|
| `purchase_orders.csv` | po_id, po_date, vendor_id, category, item, quantity, unit_price, amount, requester_id, approver_id, approved_at (`YYYY-MM-DD HH:MM`) |
| `invoices.csv` | invoice_no, vendor_id, po_id, invoice_date, amount, paid_date |
| `vendors.csv` | vendor_id, name, cr_number, bank_iban, created_date |
| `staff.csv` (optional) | staff_id, name, department, bank_iban |
| `approval_limits.csv` | level, limit_sar |

A real ERP export needs mapping into these columns first. SAP, Oracle, Odoo and local ERPs name them differently.

## The eight rules

| Rule | Flags | Amount shown |
|---|---|---|
| R1 | 2+ POs from one requester to one vendor within 14 days, each 80 to 100% of an approval limit, together over it | The POs together |
| R2 | One vendor takes 90%+ of a category's spend over 5+ POs | That vendor's spend |
| R3 | Same invoice number paid twice, or same vendor and amount within 3 days | The extra payment |
| R4 | Vendor bank account equals a staff bank account | Total paid to the vendor |
| R5 | Vendor first paid within 7 days of being created | Total paid to the vendor |
| R6 | Unit price 1.5x or more above the item's median (5+ purchases) | Estimated overpayment |
| R7 | Requester approved own PO, or approval before 07:00, after 20:00, or on Friday or Saturday | The PO amount |
| R8 | 3+ round-number invoices (multiples of SAR 1,000 from 5,000), or 3+ invoice numbers in a row | The round invoices |

Thresholds are constants at the top of `review.py`; tune them per customer. Every finding is a question for the reviewer, not a conclusion: add a note per finding before the report goes out.

## Data handling for real exports

- Get written consent from the CFO or audit head before receiving any file; process only on a machine you control, in Saudi Arabia.
- Staff bank accounts are hashed on load and never written to output; vendor accounts appear masked.
- Delete the input and output folders 30 days after the review. Do not commit real data: `sample/` is fictional, and `output/` is ignored by git.
- A PDPL review comes before the first real export (docs/16 section 3).
