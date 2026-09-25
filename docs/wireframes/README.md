# MVP Wireframes

Date: 2026-09-21
Source: `mvp-wireframes.html` in this folder (open in a browser; the language switch flips direction). Images in `img/` are rendered from it, English then Arabic for each screen.
Related: `05-mvp-scope.md` (scope), `08-design-system.md` (tokens and components), `09-backlog.md` (feature IDs)

Low-fidelity on purpose: grey blocks stand for content, while buttons, badges, stages, and the sealed-envelope treatment are real. Tenant screens are desktop width; vendor screens are phone width because vendors mostly submit from a phone. Purple appears only on sealed and locked states.

Copy rules, set 2026-09-26 at the user's request:

- **No abbreviations on screen.** Labels, buttons, and messages are written in full in both languages: "purchase order", "bill of quantities", "commercial registration", "value added tax", "Saudi riyals", full month names, full vendor names. Identifiers such as `TND-2026-014` stay, always with a label ("Tender number TND-2026-014").
- **Full sentences for hints and messages; buttons name the action and its object** ("Open tender", "Send reminder", "Approve the award").
- **Fixed button colours, independent of the tenant's brand:** blue for continue, submit, or approve; red for close, cancel, reject, or discard; white for neutral actions such as save or go back; link style for open or download. A key at the top of the wireframes page shows them.

## Tenant workspace

### T1 Tenders list (F-16, F-27, F-14a)

The contracts officer's first screen. Stage reads from the badge colour; a row opens the tender page.

![T1 English](img/t1-en.png)
![T1 Arabic](img/t1-ar.png)

### T2 New tender (F-15, F-16, F-17, F-14a, F-19, F-55)

Six steps. Step 4 is the workflow (T2a); step 5 picks vendors from the address book or takes a new email, which is saved to the book. Publishing sends the invitations.

![T2 English](img/t2-en.png)
![T2 Arabic](img/t2-ar.png)

### T2a New tender, workflow step (F-56, F-08, F-09, F-27)

Added for ADR-0003. The tenant picks a workflow template and assigns who acts at each step. Fixed points (sealing, score lock, financial opening) show as locked rows that cannot move. Publishing snapshots the template with the tender; the pilot ships only the default template.

![T2a English](img/t2a-en.png)
![T2a Arabic](img/t2a-ar.png)

### T3 Tender page (F-27, F-56, F-55, F-21, F-23)

The timeline is built from the tender's workflow snapshot and names the department at each step. The invitees tab shows who opened the link, who registered, and who submitted, with remind and resend actions.

![T3 English](img/t3-en.png)
![T3 Arabic](img/t3-ar.png)

### T4 Compliance screening (F-28, F-23)

After close, only technical envelopes open. The financial envelope stays sealed, visibly and functionally. Excluded vendors are told which item failed.

![T4 English](img/t4-en.png)
![T4 Arabic](img/t4-ar.png)

### T5 Technical scoring, evaluator view (F-29, F-30, F-17)

Each evaluator sees only their own scores until everyone has submitted. Locking belongs to the officer; the dialog states the consequence and the weighted averages.

![T5 English](img/t5-en.png)
![T5 Arabic](img/t5-ar.png)

### T6 Financial opening and comparison sheet (F-23, F-30, F-31)

The one deliberate motion in the product: envelopes open with the names present recorded in the audit log. The comparison sheet is built automatically, with arithmetic errors flagged.

![T6 English](img/t6-en.png)
![T6 Arabic](img/t6-ar.png)

### T7 Finance approval (F-33)

One screen for the approver: recommendation, ranking, budget code, decision. A return requires a reason.

![T7 English](img/t7-en.png)
![T7 Arabic](img/t7-ar.png)

### T8 Award and purchase order (F-36, F-41)

The purchase order is generated from the winning offer under the tenant's brand and stored with the tender. The audit list sits beside it.

![T8 English](img/t8-en.png)
![T8 Arabic](img/t8-ar.png)

### T9 Workflow editor, version 1.1 (F-56b, F-09)

Not in the pilot; designed now so the model is right from the start. Templates per tenant with versions; purple rows are fixed points that cannot be removed or moved; validation refuses any order that violates them; published tenders keep their snapshot when a template changes.

![T9 English](img/t9-en.png)
![T9 Arabic](img/t9-ar.png)

### T10 Offer review: internal comments and information requests, version 1.1 (F-57, F-58, F-29)

Committee members comment internally with mentions, convert a comment into a vendor request, and see the request log per offer. Only the contracts officer sends requests; the vendor never sees internal comments; prices cannot change through a reply.

![T10 English](img/t10-en.png)
![T10 Arabic](img/t10-ar.png)

## Vendor portal (phone)

### V1 Invitation landing (F-55, F-02)

What the vendor sees after tapping the email link: the buyer's brand, not ours, apart from a small "Powered by WaslaBid" line at the foot on the Starter tier (document 11, section 8; it appears at the foot of every tenant and vendor screen). A registered vendor signs in; a new one registers with the invited email.

![V1 English](img/v1-en.png)
![V1 Arabic](img/v1-ar.png)

### V2 Registration (F-11, F-12, F-55)

Two steps in the first version: company details, then two documents with expiry dates. Progress is saved and resumes from the same link.

![V2 English](img/v2-en.png)
![V2 Arabic](img/v2-ar.png)

### V3 Tender view (F-16, F-21, F-24)

Deadline first, then documents, then clarifications, then the start button.

![V3 English](img/v3-en.png)
![V3 Arabic](img/v3-ar.png)

### V4 Submission wizard (F-22, F-23, F-24, ADR-0001)

Five steps on a phone. Uploads are chunked and survive a dropped connection. The financial envelope carries the sealed colour so the vendor knows it stays unread until the opening.

![V4 English](img/v4-en.png)
![V4 Arabic](img/v4-ar.png)

### V5 Submitted (F-24, F-25, F-41)

The confirmation states server time and file hashes. Resubmitting before the deadline replaces the earlier offer.

![V5 English](img/v5-en.png)
![V5 Arabic](img/v5-ar.png)

### V6 Information request from the buyer, version 1.1 (F-57, F-24, ADR-0001)

The vendor sees the question and deadline, replies with text and attachments over the chunked upload path, and is told plainly that prices cannot change.

![V6 English](img/v6-en.png)
![V6 Arabic](img/v6-ar.png)

## Deliberately absent

Custom domain, the workflow editor (T9), vendor information requests and internal comments (T10, V6), all shown for design only; committees beyond the default template, amendments, vendor dashboard, text messages, artificial intelligence, open tenders with a public listing. All after the pilot (document 05, section 4).

## Regenerating the images

The images come from the HTML through headless Chromium. After editing `mvp-wireframes.html`, run `node render.js` in this folder to refresh `img/` (it finds puppeteer in the npx cache; if missing, `npm install --no-save puppeteer` once), then re-mirror the page to OneNote so both stay true.
