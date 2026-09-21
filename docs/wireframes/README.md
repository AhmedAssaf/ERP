# MVP Wireframes

Date: 2026-09-21
Source: `mvp-wireframes.html` in this folder (open in a browser; the language switch flips direction). Images in `img/` are rendered from it, English then Arabic for each screen.
Related: `05-mvp-scope.md` (scope), `08-design-system.md` (tokens and components), `09-backlog.md` (feature IDs)

Low-fidelity on purpose: grey blocks stand for content, while buttons, badges, stages, and the sealed-envelope treatment are real. Tenant screens are desktop width; vendor screens are phone width because vendors mostly submit from a phone. Purple appears only on sealed and locked states.

## Tenant workspace

### T1 Tenders list (F-16, F-27, F-14a)

The contracts officer's first screen. Stage reads from the badge colour; a row opens the tender page.

![T1 English](img/t1-en.png)
![T1 Arabic](img/t1-ar.png)

### T2 New tender (F-15, F-16, F-17, F-14a, F-19, F-55)

Five steps. The invitee step picks vendors from the address book or takes a new email, which is saved to the book. Publishing sends the invitations.

![T2 English](img/t2-en.png)
![T2 Arabic](img/t2-ar.png)

### T3 Tender page (F-27, F-55, F-21, F-23)

The stage timeline sits at the top of every tender page. The invitees tab shows who opened the link, who registered, and who submitted, with remind and resend actions.

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

### T8 Award and PO (F-36, F-41)

The PO is generated from the winning offer under the tenant's brand and stored with the tender. The audit list sits beside it.

![T8 English](img/t8-en.png)
![T8 Arabic](img/t8-ar.png)

## Vendor portal (phone)

### V1 Invitation landing (F-55, F-02)

What the vendor sees after tapping the email link: the buyer's brand, not ours. A registered vendor signs in; a new one registers with the invited email.

![V1 English](img/v1-en.png)
![V1 Arabic](img/v1-ar.png)

### V2 Registration (F-11, F-12, F-55)

Two steps in the MVP: company details, then two documents with expiry dates. Progress is saved and resumes from the same link.

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

## Deliberately absent

Custom domain, committees, amendments, vendor dashboard, SMS, AI, open tenders with a public listing. All after the pilot (document 05, section 4).

## Regenerating the images

The images come from the HTML through headless Chromium. After editing `mvp-wireframes.html`, run `npx --yes -p puppeteer node render.js` in this folder to refresh `img/`, then re-mirror the page to OneNote so both stay true.
