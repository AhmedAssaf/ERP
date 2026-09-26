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

A real ERP export needs mapping into these columns first. SAP, Oracle, Odoo and local ERPs name them differently. For SAP there is a mapper (next section).

## SAP exports

```
python map_sap.py <sap_folder> <mapped_folder>   # writes the five CSVs and mapping_report.txt
python review.py <mapped_folder> <report_folder>
```

Ask the company's SAP team for one file per table, filtered to the quarter and company code, downloaded from SE16N (or SE16H, or an ALV report) as tab-delimited text or Excel, named after the table (`EKKO.txt`, `EKPO.xlsx`, ...). Technical column names and the common English headers both work.

| Table | Filter | Columns | Feeds |
|---|---|---|---|
| EKKO (PO header) | BEDAT in the quarter | EBELN, LIFNR, BEDAT, ERNAM | All PO rules |
| EKPO (PO lines) | the EKKO POs | EBELN, EBELP, LOEKZ, MATNR, TXZ01, MATKL, MENGE, NETPR, PEINH, NETWR | R1, R2, R6 |
| T023T (material group texts) | SPRAS = E or A | MATKL, WGBEZ | Category names (optional) |
| CDHDR, CDPOS (change documents) | OBJECTCLAS = EINKBELEG, the EKKO POs | CDHDR: OBJECTID, CHANGENR, USERNAME, UDATE, UTIME; CDPOS: OBJECTID, CHANGENR, FNAME | R7: approver is the last release (FNAME FRGKE or FRGZU) |
| RBKP (invoice header) | BLDAT in the quarter | BELNR, GJAHR, LIFNR, XBLNR, BLDAT, RMWWR, STBLG | R3, R8 |
| RSEG (invoice lines) | the RBKP invoices | BELNR, GJAHR, EBELN | Invoice to PO link |
| BKPF, BSAK (accounting header, cleared vendor items) | AWTYP = RMRP; the same company code | BKPF: BELNR, GJAHR, AWTYP, AWKEY; BSAK: BELNR, GJAHR, AUGDT | R5: payment date via AWKEY, then the clearing date |
| LFA1 (vendor master) | vendors in EKKO or RBKP | LIFNR, NAME1, STCD1, ERDAT | Names, R5 |
| LFBK and TIBAN (vendor banks) | the same vendors | LFBK: LIFNR, BANKS, BANKL, BANKN (or IBAN); TIBAN: BANKS, BANKL, BANKN, IBAN | R4 |
| PA0009 (HR bank details, optional) | active staff | PERNR, IBAN | R4; restricted HR data, only with the data form's staff option ticked |
| `approval_limits.csv` | typed by hand from the authority matrix | level, limit_sar | R1 |

The mapper drops deleted PO lines (LOEKZ = L) and reversed invoices (STBLG filled), divides the net price by the price unit, strips leading zeros from vendor and material numbers, and reads dd.mm.yyyy dates and 1.234,56 numbers. When a table is missing it still runs, and `mapping_report.txt` says which rules cannot run. In S/4HANA, business partners still expose LFA1 and LFBK, and the tables above keep their names.

Test it without SAP: `python to_sap.py` turns the fictional sample into `sap_sample/` in SAP format (German number format, price units, a two-line PO, a reversed invoice, change documents that are not releases). Mapping it and running the review finds all 21 planted red flags again.

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
