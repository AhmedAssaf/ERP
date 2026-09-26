# Offer-to-Markdown spike (W-22)

Question: can we turn vendor offers (DOCX, PDF, scanned PDF, Arabic and English) into Markdown that a cheap LLM can evaluate against the RFP, with page references? This feeds option (e) of document 02 section 5 item 7.

Throwaway code (docs/07). Do not build on it.

## Run

```
python -m venv .venv
.venv/Scripts/python -m pip install "markitdown[pdf,docx]" pymupdf4llm python-docx pywin32 truststore
.venv/Scripts/python -m pip install torch torchvision --index-url https://download.pytorch.org/whl/cpu
.venv/Scripts/python -m pip install docling easyocr          # optional, large
dotnet build PdfPigCheck
.venv/Scripts/python make_samples.py     # needs Microsoft Word; the generated samples are committed, so this is optional
.venv/Scripts/python convert.py          # writes out/<tool>/*.md and out/scores.md
.venv/Scripts/python convert.py samples/real   # real offers: Markdown only, read by a human
```

`samples/real/` is git-ignored. Real offers are third parties' confidential bids; never commit them.

## Samples

`make_samples.py` writes a three-page Arabic technical offer with English product names, standards, numbers, and a compliance table, plus an English control. Word exports the PDFs (as most real offers are made), and the scanned PDF is the Arabic PDF rendered to 200 dpi JPEG pages with no text layer. `truth.json` holds the source text per page.

## Scores

Word recall: share of source words found in the output. Order kept or reversed: share of adjacent source word pairs found in the same or the opposite order. Presentation-form characters would break search and quoting.

| File | Tool | Seconds | Word recall | Arabic word recall | Recall after NFKC | Order kept | Order reversed | Presentation-form chars | Page markers |
|---|---|---|---|---|---|---|---|---|---|
| offer-ar-scanned.pdf | docling | 112.6 | 88% | 88% | 88% | 55% | 1% | 0 | yes |
| offer-ar-scanned.pdf | docling-forced-ocr | 200.8 | 81% | 80% | 81% | 46% | 1% | 0 | yes |
| offer-ar-scanned.pdf | markitdown | 1.7 | 0% | 0% | 0% | 0% | 0% | 0 | no |
| offer-ar-scanned.pdf | pdfpig | 0.7 | 0% | 0% | 0% | 0% | 0% | 0 | yes |
| offer-ar-scanned.pdf | pdfpig-logical | 0.5 | 0% | 0% | 0% | 0% | 0% | 0 | yes |
| offer-ar-scanned.pdf | pymupdf4llm | 13.8 | 6% | 0% | 6% | 2% | 0% | 0 | yes |
| offer-ar.docx | docling | 0.1 | 100% | 100% | 100% | 100% | 0% | 0 | no |
| offer-ar.docx | markitdown | 0.4 | 100% | 100% | 100% | 100% | 0% | 0 | no |
| offer-ar.pdf | docling | 28.1 | 100% | 100% | 100% | 69% | 19% | 0 | yes |
| offer-ar.pdf | docling-forced-ocr | 238.8 | 95% | 93% | 95% | 56% | 15% | 0 | yes |
| offer-ar.pdf | markitdown | 0.3 | 23% | 5% | 23% | 9% | 1% | 0 | no |
| offer-ar.pdf | pdfpig | 0.8 | 23% | 5% | 23% | 9% | 1% | 0 | yes |
| offer-ar.pdf | pdfpig-logical | 0.6 | 99% | 100% | 99% | 84% | 1% | 0 | yes |
| offer-ar.pdf | pymupdf4llm | 1.2 | 87% | 87% | 87% | 7% | 61% | 0 | yes |
| offer-en.docx | docling | 0.1 | 100% | n/a | 100% | 100% | 0% | 0 | no |
| offer-en.docx | markitdown | 0.3 | 100% | n/a | 100% | 100% | 0% | 0 | no |
| offer-en.pdf | docling | 37.4 | 100% | n/a | 100% | 100% | 0% | 0 | yes |
| offer-en.pdf | docling-forced-ocr | 109.0 | 66% | n/a | 66% | 52% | 0% | 0 | yes |
| offer-en.pdf | markitdown | 0.2 | 100% | n/a | 100% | 97% | 0% | 0 | no |
| offer-en.pdf | pdfpig | 0.7 | 100% | n/a | 100% | 100% | 0% | 0 | yes |
| offer-en.pdf | pdfpig-logical | 0.5 | 99% | n/a | 99% | 94% | 0% | 0 | yes |
| offer-en.pdf | pymupdf4llm | 1.1 | 96% | n/a | 96% | 90% | 0% | 0 | yes |

## Findings

1. **DOCX is solved.** MarkItDown and Docling both return 100 percent of the words in order, Arabic and English, in under a second. Ask vendors for DOCX where the tender allows it.
2. **Arabic text PDFs break the common tools.** Word-exported PDFs store Arabic in visual order. MarkItDown (pdfminer) and PdfPig's own extractor return every Arabic word with its letters reversed (5 percent recall). PyMuPDF4LLM keeps letters but reverses word order on each line (61 percent of word pairs reversed) and splits lam-alef ligatures (الأفق becomes األفق).
3. **Two usable options for text PDFs.** Docling returns 100 percent of the Arabic words and keeps the compliance table as a Markdown table. PdfPig plus our visual-to-logical pass (`PdfPigCheck --logical`, about 60 lines of C#: build lines from glyph positions, read Arabic lines right to left, keep Latin and digit runs left to right, keep each glyph's text whole) also returns 100 percent, runs in under a second, stays in the .NET stack, but loses table structure.
4. **Mixed Arabic and English lines stay out of order in every tool.** In the sample PDF, Word laid out lines such as "نلتزم بتوريد محولات Cisco Catalyst 9300 ..." with the Arabic segments placed left to right, although Word reports the paragraph as right to left. A PDF does not store the base direction, so position-based ordering cannot recover it. The words survive; the order of segments on those lines does not. This may be specific to the generated sample and must be checked on real offers.
5. **Every text-layer extractor dropped the final ي of الفني in the page 1 heading**, so that letter is missing from the PDF's text layer itself; only OCR can recover such losses.
6. **Scanned PDFs need OCR, and only Docling has it here.** 88 percent of words recognised, but lines merged out of order, some OCR errors (IEEE read as ١٤٤٤, ISO read as 0$|, الحالة read as الدلة), and roughly 35 seconds per page on this laptop's CPU (first-run timings include model loading), so an 80-page scan takes most of an hour. Forcing OCR on text PDFs made results worse and slower.
7. **Page markers.** Every PDF path keeps page markers. DOCX has no pages; use headings or section numbers as the reference instead.
8. **Setup.** Docling downloads models from Hugging Face; behind this machine's TLS inspection that needs `truststore` (the Python counterpart of the git `schannel` setting).

Not tested, and the next step: giving the PDF directly to an LLM that reads PDFs natively (the model sees each page as an image as well as its text). It avoids the extraction problem entirely at a higher token cost. Also still needed for W-22 acceptance: two or three real Arabic offers from a prospect, placed in `samples/real/` and read by a human.

## Tender test data and an end-to-end test (added 2026-09-26)

`make_tenders.py` (content in `tenders_data.py`) writes three fictional tenders to `samples/tenders/`, each with an RFP, three offers (one fully compliant, one partial, one weak with planted red flags), a separate financial file per offer, and `answer-key.json` with the expected verdict per requirement and the planted flags. Every page carries a "fictional test data" footer.

| Tender | Language | RFP | V1 (compliant) | V2 (partial) | V3 (weak, red flags) |
|---|---|---|---|---|---|
| T1 network supply, Riyadh | Arabic | DOCX and PDF | DOCX | Word PDF | scanned PDF |
| T2 cleaning services, Jeddah | Arabic | PDF | Word PDF | DOCX | scanned PDF |
| T3 HR and payroll SaaS | English | DOCX | DOCX | Word PDF | scanned PDF |

Planted: expired commercial registration, licence expiring before the contract, social insurance certificate in another company's name, missing VAT certificate, shared phone number and copied paragraphs between bidders, a bidder subcontracting to a rival bidder, vague "complies" answers, arithmetic errors, missing price lines, quantity and unit mismatches.

`test_tenders.py` checks the data and the conversion; `score_llm.py` scores LLM verdicts against the answer keys. The LLM step used Claude Haiku 4.5 as a stand-in for the cheapest model, run as nine isolated Claude Code subagents (one per offer, each seeing only `prompts/offer-review-v1.md`, the RFP Markdown, and that one offer), because no API key or local model is configured on this machine.

Results:

1. **Answer keys consistent; the deterministic price checker found every planted financial error with no false positive** on the compliant offers. Arithmetic, missing lines, quantity and unit checks need no LLM.
2. **Conversion, share of facts (tokens with digits) kept:** DOCX 100 percent; Word PDF 94 to 100 percent with Docling, 29 to 100 percent with PdfPig logical (RTL tables break it); scanned PDF 46 to 71 percent with Docling OCR, which drops whole lines: the shared phone number, the subcontractor's name, the certificate holder's name, and a whole copied paragraph were lost, so those red flags cannot be found from the Markdown.
3. **LLM verdicts:** exact agreement 76 of 123 (62 percent) overall, 64 of 82 (78 percent) on DOCX and Word PDF, 12 of 41 on scans. The model called a requirement "met" wrongly 3 times in 123; on unreadable scans it said so and answered "unclear" or "not met" rather than guessing.
4. **Why it disagreed:**
   - Prompt: the rule "a document must stay valid for the contract" was applied to social insurance certificates, which renew routinely (3 misses). Version 2 should apply it only where the RFP says so.
   - Test data: a VAT row with "not applicable" in the expiry column read as ambiguous (3 misses).
   - Model errors: "30 days meets a 14-day requirement", "transport paid by the client" treated as met. These are the cases a human must catch, and the reason to keep evidence quotes beside every verdict.
   - Scans: most misses come from OCR loss, not from the model.

### Prompt version 2 (`prompts/offer-review-v2.md`)

General rules only, not answers fitted to the key: compare numbers explicitly with both values in the note, obligations moved to the buyer are partial, require validity for the whole contract only when the requirement says so, "not applicable" in an expiry column is normal, documents under renewal are unclear. Same inputs, same nine isolated Haiku runs; `python score_llm.py llm-v2`.

| Measure | v1 | v2 |
|---|---|---|
| Exact agreement, all | 75/123 (61%) | 88/123 (72%) |
| DOCX and Word PDF | 63/82 (77%) | 72/82 (88%) |
| Scanned PDF | 12/41 (29%) | 16/41 (39%) |
| Wrongly called "met" | 3 | 1 |
| Compliant offers fully right | 0 of 3 | 2 of 3 |
| Malformed JSON | 0 | 1 (a stray closing brace) |

Scores are against the answer key as corrected for the shortfall decision below. The remaining differences on DOCX and Word PDF, before that decision:

- **Policy, needs a decision (4):** when an offer falls short of a minimum (3 training days of 5, 2 references of 3, 18 hours of 24), v2's rule says `not_met` and the answer key says `partial`. The business rule has to be chosen, and could be a tenant setting.
- **Model errors (3):** Excel reports accepted as an electronic reporting system (the one wrong "met"), the contract-validity rule still applied to a bid bond, and a VAT row with "not applicable" still read as ambiguous.
- **Defensible either way (2):** one core switch with an optional second read as partial, and business-hours support with an English-only 24/7 line read as not met.

The malformed file shows why the product must use schema-enforced structured output rather than parse free text.

**Decision 2026-09-26: a shortfall against a minimum is `partial`.** Fewer days, staff, years, or references than required is partial; none at all is not met; exceeding a maximum (a deadline, a response time) stays not met. Recorded in `prompts/offer-review-v3.md`; the one answer-key row that contradicted it (T2 V2 T-01, 32 of 40 workers) is now partial. Prompt v3 has not been run yet.
