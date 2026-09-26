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

Test it without SAP: `python to_sap.py` (see below)

## Odoo exports

```
python map_odoo.py <odoo_folder> <mapped_folder> [utc_offset_hours]   # default +3, Riyadh
python review.py <mapped_folder> <report_folder>
```

Ask the company's Odoo admin to set their user language to English and use the list-view **Export** (Odoo 16, 17 or 18). Keep the file names Odoo gives (`purchase.order.xlsx`, ...). Field labels or technical names ("I want to update data") both work. Many2one fields such as Vendor export as display names, so the vendor name must be the same in every file.

| Model (menu) | Filter | Fields to export | Feeds |
|---|---|---|---|
| `purchase.order` (Purchase > Orders) | Order Date in the quarter | ID, Order Reference, Vendor, Order Date, Confirmation Date, Created by, Buyer, Status | All PO rules |
| `purchase.order.line` (debug mode, or Purchase > Reporting) | the same orders | Order Reference, Product, Product/Product Category, Description, Quantity, Unit Price, Subtotal | R1, R2, R6 |
| `mail.tracking.value` (Settings > Technical, debug mode) | Message/Related Document Model = purchase.order | Message/Related Document Model, Message/Related Document ID, Field, New Value Char, Created on, Created by | R7: approver is who moved the PO to "Purchase Order" |
| `account.move` (Accounting > Vendors > Bills) | Bill Date in the quarter | Number, Vendor, Bill Reference, Bill Date, Source Document, Total Signed, Status, Payment Status, Type | R3, R8 |
| `account.payment` (Accounting > Vendors > Payments) | Date from the quarter start onward | Date, Status, Reconciled Bills | R5: payment date |
| `res.partner` (Purchase > Vendors) | vendors on the orders and bills | Display Name, Company ID, Tax ID, Created on, Bank Accounts/Account Number | Names, R4, R5 |
| `hr.employee` (optional) | active staff | Badge ID, Bank Account Number | R4; restricted HR data, only with the data form's staff option ticked |
| `approval_limits.csv` | typed by hand; Odoo's purchase double validation amount is one limit | level, limit_sar | R1 |

The mapper keeps only confirmed orders (Purchase Order or Locked) and posted vendor bills that are not reversed, fills down the blank parent columns Odoo writes for one2many rows (a vendor with two bank accounts, a payment settling two bills), shifts UTC datetimes to local time, and keeps the last part of the product category path. Missing optional files are listed in `mapping_report.txt` with the rules they disable. Odoo's own datetime export behaviour can vary by version and settings: check one known PO's confirmation time against the screen before trusting rule R7.

Test it without Odoo: `python to_odoo.py` writes `odoo_sample/` (UTC datetimes, a cancelled order, a reversed bill, a two-line order, continuation rows); mapping it and running the review finds all 21 planted red flags again.

## Oracle exports (E-Business Suite R12 or Fusion Cloud)

```
python map_oracle.py <oracle_folder> <mapped_folder> [utc_offset_hours]   # EBS: 0 (default); Fusion: 3
python review.py <mapped_folder> <report_folder>
```

EBS: the customer's DBA runs one query per table in SQL Developer or Toad and exports CSV named after the table. Before exporting, run `ALTER SESSION SET NLS_DATE_FORMAT = 'YYYY-MM-DD HH24:MI:SS'`, or dates lose their time. Fusion Cloud: there is no database access, so the customer's Oracle admin builds a BI Publisher data model over the same tables (Fusion names below) and exports CSV. Fusion stores UTC, so pass 3.

| Table (Fusion name if different) | Filter | Columns | Feeds |
|---|---|---|---|
| PO_HEADERS_ALL | CREATION_DATE in the quarter, one operating unit (ORG_ID) | PO_HEADER_ID, SEGMENT1, TYPE_LOOKUP_CODE, VENDOR_ID, AGENT_ID, CREATION_DATE, AUTHORIZATION_STATUS, APPROVED_DATE, CANCEL_FLAG | All PO rules |
| PO_LINES_ALL | those headers | PO_HEADER_ID, LINE_NUM, ITEM_ID, ITEM_DESCRIPTION, CATEGORY_ID, QUANTITY, UNIT_PRICE, AMOUNT, CANCEL_FLAG | R1, R2, R6 |
| MTL_CATEGORIES_KFV (EGP_CATEGORIES_TL) | all | CATEGORY_ID, CONCATENATED_SEGMENTS (CATEGORY_NAME) | Category names (optional) |
| PO_ACTION_HISTORY | OBJECT_TYPE_CODE = 'PO', those headers | OBJECT_ID, OBJECT_TYPE_CODE, SEQUENCE_NUM, ACTION_CODE, EMPLOYEE_ID, ACTION_DATE | R7: last APPROVE is the approver, SUBMIT the requester |
| AP_SUPPLIERS (POZ_SUPPLIERS plus HZ_PARTIES for the name) | vendors on the orders and invoices | VENDOR_ID, SEGMENT1, VENDOR_NAME (or PARTY_ID and HZ_PARTIES.PARTY_NAME), VAT_REGISTRATION_NUM, CREATION_DATE | Names, R5 |
| SUPPLIER_BANK_ACCOUNTS (query below) | the same vendors | VENDOR_ID, IBAN | R4 |
| AP_INVOICES_ALL | INVOICE_DATE in the quarter | INVOICE_ID, INVOICE_NUM, VENDOR_ID, INVOICE_DATE, INVOICE_AMOUNT, INVOICE_TYPE_LOOKUP_CODE, CANCELLED_DATE | R3, R8 |
| AP_INVOICE_LINES_ALL | those invoices | INVOICE_ID, LINE_NUMBER, PO_HEADER_ID | Invoice to PO link |
| AP_INVOICE_PAYMENTS_ALL, AP_CHECKS_ALL | those invoices | INVOICE_ID, CHECK_ID, REVERSAL_FLAG; CHECK_ID, CHECK_DATE, STATUS_LOOKUP_CODE, VOID_DATE | R5: paid date, voided payments ignored |
| STAFF_BANK_ACCOUNTS (optional, from HR) | active staff | PERSON_ID, IBAN | R4; restricted HR data, only with the data form's staff option ticked |
| `approval_limits.csv` | typed by hand from the approval hierarchy | level, limit_sar | R1 |

Supplier bank accounts in EBS R12 live in Oracle Payments; a typical query, for the DBA to confirm on their instance:

```sql
SELECT s.vendor_id, eba.iban, eba.bank_account_num
FROM   ap_suppliers s
JOIN   iby_external_payees_all epa ON epa.payee_party_id = s.party_id
JOIN   iby_pmt_instr_uses_all  piu ON piu.ext_pmt_party_id = epa.ext_payee_id
                                  AND piu.instrument_type = 'BANKACCOUNT'
JOIN   iby_ext_bank_accounts   eba ON eba.ext_bank_account_id = piu.instrument_id;
```

Staff bank accounts come from HR (payroll payment methods); ask for PERSON_ID and IBAN only. PO_ACTION_HISTORY.EMPLOYEE_ID is a person ID, so it matches PERSON_ID.

The mapper keeps approved STANDARD orders that are not cancelled, drops cancelled lines, cancelled invoices and credit or debit memos, ignores voided payments, uses the supplier number (SEGMENT1) as the vendor ID, and uses AMOUNT for service lines without a quantity price. If only APPROVED_DATE is available and it has no time, the approval time is left empty rather than read as midnight, and the night and weekend check skips those orders.

Test it without Oracle: `python to_oracle.py` writes `oracle_sample/` (DD-MON-RR dates, internal IDs, SUBMIT, FORWARD and APPROVE history, an incomplete and a cancelled order, a cancelled line, a cancelled invoice, a credit memo, a voided payment); mapping it and running the review finds all 21 planted red flags again.

## Testing the mappers

`python to_sap.py` turns the fictional sample into `sap_sample/` in SAP format (German number format, price units, a two-line PO, a reversed invoice, change documents that are not releases). Mapping it and running the review finds all 21 planted red flags again.

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
