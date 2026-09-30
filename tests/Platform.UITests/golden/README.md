# Gallery golden set (W-06, admin and vendor subsets)

Screenshots of `/dev/gallery` (Development only, signed in as `acme.admin` on `https://acme.localhost:8443`), the
component gallery of `Platform.UI` for the admin UI slice (spec `docs/superpowers/specs/2026-09-27-admin-ui-design.md`
section 5). The page shows every component twice, an Arabic right-to-left panel and an English left-to-right panel; the
page chrome follows the culture cookie.

| File | Page culture | Viewport width |
|---|---|---|
| `gallery-ar-SA-360.png` | `ar-SA` (`dir="rtl"`) | 360 px |
| `gallery-ar-SA-1280.png` | `ar-SA` (`dir="rtl"`) | 1280 px |
| `gallery-en-US-360.png` | `en-US` (`dir="ltr"`) | 360 px |
| `gallery-en-US-1280.png` | `en-US` (`dir="ltr"`) | 1280 px |

How they were taken (2026-09-26, retaken 2026-09-28 by `tests/e2e/golden.mjs` for the vendor slice, which added the
vendor variant of `AppShell`, `FileUpload` and `FileUploadStatus` in each of its states): Playwright 1.x for Node with Chromium 147.0.7727.15 (headless), full-page screenshots
after `networkidle` and `document.fonts.ready`, viewport height 900 px. The culture was set through
`/culture/set?culture=<culture>&returnUrl=/dev/gallery`. The tenant `acme` had its seeded branding (portal name
"Acme Contracting", primary colour `#0F766E`, no logo), so the tenant variant of `AppShell` shows that colour.

Horizontal overflow check at each size: `document.documentElement.scrollWidth <= window.innerWidth`. Result: no overflow
in any of the four (scroll width 360 at 360 px, 1280 at 1280 px).

Pending retake (W-10, 2026-10-01): `StatTile` joined the gallery for the console usage page (a value with its staff and
vendor split, a value alone, and an unknown value shown as a dash). The four images above predate it; retake them with
`tests/e2e/golden.mjs` against the running stack and compare both directions at 360 and 1280 px before the merge.

These are reference images for a human comparison when a component changes, not an automated pixel test yet. Retake
them in the same way (same tenant branding, same widths) and review the difference in the pull request. `Stepper`,
`StageTimeline` and `SealedEnvelope` join the gallery, and this set, with the tender slices.
