# Design System: Tailwind as the Default UI Foundation

Date: 2026-09-21
Status: decided (ADR-0002). Replaces MudBlazor in `02-core-features-and-tech-stack.md` section 4.
Related: F-02 white-label, F-04 Arabic and RTL, N-05 performance on mobile, N-09 accessibility

## 1. What the system has to do

Two audiences use the same product under many brands. A contracts officer works on a desktop for hours, reading dense comparison tables and moving tenders through stages. A vendor opens an invitation on a phone, often in Arabic, often on a weak connection, and must finish a sealed submission before a deadline. Every screen carries the tenant's brand, not ours. So the design system's own personality must be quiet, the tenant's colour must be able to take over, Arabic must be the first-class script, and the memorable element must be the one thing no competitor shows: the sealed envelope and the stage it is in.

## 2. Decision in one paragraph

Tailwind CSS v4 is the styling foundation, built with the standalone Tailwind CLI so the .NET build needs no Node. Tokens are CSS custom properties declared with Tailwind's `@theme`, which is what lets one tenant's primary colour replace another's at runtime. Components are our own Razor components in `src/UI/Platform.UI`, styled only with Tailwind utilities and the tokens. Tables use Microsoft's QuickGrid with our styling. No third-party component kit: MudBlazor, Radzen, and daisyUI are out, because each brings its own visual identity and its own right-to-left assumptions, and the white-label promise needs both to be ours.

## 3. Tokens

### 3.1 Colour

The base is a cool neutral built from one hue so tenant colours sit on it without clashing. The tenant primary is the only strong colour on any screen; everything else is neutral or a state colour.

| Token | Role | Default | Notes |
|---|---|---|---|
| `--color-primary` | Tenant brand, set per tenant (F-02) | `#1E4E79` | Overridden per tenant at runtime. Contrast against white is checked on upload; if it fails, the system darkens it and tells the admin. |
| `--color-on-primary` | Text on primary | `#FFFFFF` or `#111827` | Computed from the primary's luminance. |
| `--color-ink` | Body text | `#1F2933` | Not pure black. |
| `--color-ink-muted` | Secondary text | `#52606D` | Meets 4.5:1 on canvas. |
| `--color-canvas` | Page background | `#F5F7FA` | Cool, not cream. |
| `--color-surface` | Cards, tables, inputs | `#FFFFFF` | |
| `--color-line` | Borders, table rules | `#D9E2EC` | One border colour everywhere. |
| `--color-sealed` | Sealed envelope, locked scores | `#5B3A8C` | The signature colour of the product. Used only for sealed and locked states. |
| `--color-success` | Awarded, approved, passed | `#0F7B4F` | |
| `--color-warning` | Deadline near, document expiring | `#B45309` | |
| `--color-danger` | Rejected, late, failed | `#B42318` | |

Tender stage colours are fixed and do not change per tenant, so an officer moving between companies still reads a stage at a glance: Draft neutral, Published primary, Closed ink, Evaluation warning, Sealed and Locked sealed, Awarded success, Cancelled danger.

### 3.2 Typography

One family for both scripts: **IBM Plex Sans Arabic** (Open Font License). It was designed with Arabic and Latin in the same family, so a bilingual line such as a vendor name followed by a CR number sits on one baseline with one rhythm. No second display face; hierarchy comes from size and weight, not from switching fonts.

| Step | Size | Line height | Weight | Use |
|---|---|---|---|---|
| Display | 30px | 1.2 | 600 | Page title only |
| Heading | 22px | 1.3 | 600 | Section headings, tender title |
| Subheading | 17px | 1.4 | 600 | Card and table headers |
| Body | 15px | 1.6 | 400 | Everything else; Arabic needs the extra leading |
| Small | 13px | 1.5 | 400 | Table cells in dense views, timestamps |
| Micro | 12px | 1.4 | 500 | Badges only |

Rules: line length under 75 characters in prose views; numbers in tables use tabular figures (`font-variant-numeric: tabular-nums`) so totals align; Latin digits by default everywhere, with an optional tenant setting for Arabic-Indic digits later; no uppercase labels, because Arabic has no case and the two languages should look alike.

### 3.3 Spacing, radius, elevation

- Spacing follows Tailwind's 4px scale. Page gutter 16px on phones, 32px on desktop.
- Two radii only: 6px for inputs, buttons, badges; 10px for cards and dialogs. A sealed envelope card uses the 10px radius plus a 2px `--color-sealed` border, nothing else.
- One shadow, used only for floating things (dialogs, menus, toasts). Cards on the canvas use a border, not a shadow.

### 3.4 Motion

No entrance animations. Motion only answers an action: a stage advancing slides the timeline marker, a sealed envelope opening at the financial opening event plays one deliberate reveal, and that is the single orchestrated moment in the product. `prefers-reduced-motion` disables all of it.

## 4. Right-to-left as the default direction

- The `<html>` element carries `lang` and `dir` from the user's culture. Arabic users get `dir="rtl"`, and the whole layout mirrors.
- Only logical utilities are allowed: `ms-`, `me-`, `ps-`, `pe-`, `start-`, `end-`, `text-start`, `text-end`, `rounded-s-`, `rounded-e-`. The physical utilities `ml-`, `mr-`, `pl-`, `pr-`, `left-`, `right-`, `text-left`, `text-right` are banned and a lint step fails the build if they appear in a `.razor` file.
- Icons that imply direction (back, next, chevrons) flip with `rtl:-scale-x-100`. Icons that do not (download, lock, calendar) never flip.
- Numbers, references such as `TND-2026-014`, and email addresses are wrapped in `<bdi>` so they keep their internal order inside Arabic sentences, the same lesson as the PDF spike.
- Every component story is rendered in both directions in the component gallery before it is accepted.

## 5. White-label mechanics (F-02, F-03)

The tenant record holds primary colour, logo, favicon, and portal name. The host page emits one inline style block per request:

```
<style>:root{--color-primary:#8A1538;--color-on-primary:#ffffff}</style>
```

Nothing else changes. Because every component reads tokens rather than hard-coded colours, the whole portal, the emails rendered from the same tokens, and the QuestPDF documents (which read the same values from the tenant record) agree. The design system itself has no logo and no colour of its own on tenant-facing screens; our brand (WaslaBid, logo files in `docs/brand/`) appears only in the platform admin area and, on the Starter tier, as a small "Powered by WaslaBid" line at the foot of the vendor portal (removed from Growth up; document 11 section 8).

## 6. Components for the MVP

Built once in `src/UI/Platform.UI`, each as a Razor component with a documented API and a gallery page. This is the whole list for the pilot; anything else is a page, not a component.

| Component | Purpose | Notes |
|---|---|---|
| `AppShell` | Header with tenant logo, primary navigation, culture switch, user menu | Two variants: tenant workspace, vendor portal |
| `Button` | Primary, secondary, danger, quiet | Loading state built in; label is the verb of the action |
| `TextField`, `NumberField`, `DateField`, `Select`, `Textarea` | Form inputs with label, help, error | Validation messages in both languages; error text is specific, never "invalid" |
| `FileUpload` | Chunked upload with progress and retry state | Wraps the chunked HTTP path from ADR-0001; never `InputFile` |
| `Stepper` | Vendor submission wizard | Shows saved state per step; works on a 360px screen |
| `DataTable` | QuickGrid wrapper with sticky header, tabular numbers, row density toggle | Comparison sheet, vendor list, audit log |
| `StageTimeline` | The tender's stage state machine as a horizontal timeline | Fixed stage colours from 3.1; current stage carries the tenant primary |
| `SealedEnvelope` | Card that shows sealed, locked, or opened, with who and when | The signature component; the only place `--color-sealed` and the reveal motion appear |
| `StatusBadge` | Stage, document validity, approval result | Micro type, no uppercase |
| `Dialog` | Confirmations for irreversible actions: publish, lock scores, open financial, award | Title states the consequence; primary button repeats the verb |
| `Toast` | Result of an action | Same verb as the button that caused it: "Published", "Scores locked" |
| `EmptyState` | No tenders, no vendors, no offers yet | Says what to do next, with the one button that does it |
| `AuditList` | Chronological event list with actor, action, time | Used in tender history and the admin log |

Wireframes of the MVP screens built on these tokens are in `docs/wireframes/mvp-wireframes.html`; open the file in a browser and use the language switch to check both directions.

## 7. Build integration

- The standalone Tailwind CLI (single binary per platform, no Node) runs from an MSBuild target before compile: input `src/UI/Platform.UI/Styles/app.css`, output `wwwroot/app.css`, content globs over `**/*.razor` and `**/*.cs`. The binary is restored by a script into `tools/` and pinned by version.
- `app.css` holds the `@theme` block with the tokens above and a small base layer (font loading, `tabular-nums` on tables, focus ring). No custom CSS outside this file except in components that need a keyframe.
- Fonts are self-hosted in `wwwroot/fonts` (OFL allows it), never loaded from a CDN, for data residency and for offline-ish vendor sessions.
- A lint step greps `.razor` files for banned physical-direction utilities and fails CI.
- The component gallery is a Blazor page under `/dev/gallery`, available only in Development, rendering every component in both directions and both cultures. The qa agent screenshots it for visual regression.

## 8. Accessibility floor (N-09)

Keyboard reachable everything, visible focus ring in the tenant primary, 4.5:1 text contrast enforced by the token contrast check, labels tied to inputs, live region for toasts, dialogs trap focus, tables have real headers. These are checked in the gallery, not left to chance.

## 9. Voice in the interface

Sentence case in both languages. Buttons name the action: "Publish tender", "Lock scores", "Open financial envelopes". The confirmation dialog says what cannot be undone. Errors say what happened and what to do: "Submission closed at 14:00 Riyadh time. The deadline has passed; contact the contracts officer." Empty states invite the next step. Arabic copy is written by a native speaker, not translated word for word, and both languages ship in the same pull request (docs/07 principle 5).

## 10. What changes elsewhere

- `02-core-features-and-tech-stack.md` section 4.1: component library row becomes Tailwind v4 plus `Platform.UI`; QuickGrid for tables.
- `05-mvp-scope.md`: no change to scope; the component list in section 6 above is the UI work behind rows 2, 10, 11, and 12.
- `07-ways-of-working.md` definition of done gains: new or changed components appear in the gallery in both directions.
- CLAUDE.md decisions: MudBlazor replaced by Tailwind and `Platform.UI`.
