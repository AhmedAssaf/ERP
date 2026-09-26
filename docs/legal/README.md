# Legal drafts

**Drafts only. Have a lawyer licensed in Saudi Arabia review both before anyone signs them. They are not legal advice.**

| File | What it is | When it is signed |
|---|---|---|
| `mutual-nda.docx` | Mutual non-disclosure agreement, Arabic and English side by side; the Arabic text prevails | Before the first detailed conversation or any data |
| `data-processing-authorisation.docx` | The company (controller) authorises us (processor) to process one quarter's purchasing data, under the Personal Data Protection Law; ends with a deletion certificate | Before the data is sent, by the CFO or head of internal audit |

Both serve the procurement audit review (docs/16 idea 7, `docs/offers/audit-review-offer.pdf`, `spikes/AuditReviewSpike`).

## Questions for the lawyer

1. Who signs as "Provider" before a company exists: an individual, a sole establishment, or wait for the company registration? Does a side business need the employer's written consent?
2. Is a controller-processor authorisation in this form enough under the PDPL Implementing Regulations, or is a fuller data processing agreement needed?
3. Are the 24-hour breach notice (ours to the company) and the 30-day deletion right, and should the NDA confidentiality term be longer than 3 years?
4. Courts in a named city, or arbitration (for example the Saudi Center for Commercial Arbitration)?
5. Does sharing staff bank account numbers for the conflict-of-interest check need anything beyond the company's own lawful basis and notice?
6. Is professional liability insurance needed before the first paid review, given the report is not a statutory audit?

## Rebuild

The text lives in `build.js`, so edit it there after the lawyer's review, not in Word, and rebuild:

```
npm install docx@9       # once, in any folder; node_modules is git-ignored
node docs/legal/build.js # set NODE_PATH to that folder's node_modules if it is elsewhere
```
