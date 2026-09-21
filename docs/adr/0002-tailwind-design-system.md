# ADR-0002: Use Tailwind CSS with an in-house component library instead of MudBlazor

Date: 2026-09-21
Status: Accepted
Deciders: Ahmed Assaf
Related: F-02, F-03, F-04, N-05, N-09; `docs/08-design-system.md`

## Context

Document 02 chose MudBlazor for its built-in right-to-left support and ready-made data grid. The product's central promise is white-label branding per tenant (F-02, F-03) and Arabic as a first-class script (F-04). A component kit brings its own visual identity, its own theme model, and its own assumptions about direction; overriding those per tenant fights the kit. The user asked for Tailwind as the default design system.

## Decision

Tailwind CSS v4 is the styling foundation, built with the standalone CLI (no Node dependency in the .NET build). Design tokens are CSS custom properties in Tailwind's `@theme`; the tenant primary colour is injected per request. Components are our own Razor components in `src/UI/Platform.UI`, styled only with Tailwind utilities and tokens, with Microsoft QuickGrid for tables. No third-party component kit. Only logical direction utilities are allowed, enforced by a lint step.

## Consequences

- The whole portal, emails, and PDFs read the same tokens, so a tenant's brand is consistent everywhere with one style block.
- Right-to-left is a build-time rule (logical utilities only) rather than a kit feature, and every component is accepted in both directions in the gallery.
- We build about thirteen components ourselves before the pilot (docs/08 section 6). This is real work, roughly two weeks, and it replaces the MudBlazor learning and override work rather than adding to it.
- No dependency on a kit's release cycle or licence; Tailwind and QuickGrid are permissively licensed.
- Document 02 section 4.1 and CLAUDE.md are updated in this change. Document 05's plan bar "Tenant, branding, RLS, audit table" absorbs the token and shell work; the component gallery is part of "Vendor registration, documents".

## Alternatives considered

| Option | Why not now |
|---|---|
| Keep MudBlazor | Own identity and theme model to fight per tenant; RTL support exists but is the kit's, not ours. |
| Tailwind plus daisyUI | Fast, themeable by CSS variables, but its components carry a recognisable look and we would spend the saved time overriding it. Revisit only if the component build slips. |
| Radzen or Syncfusion | Same identity problem, plus licence cost for Syncfusion. |
| Bootstrap with RTL build | Workable, but utility-first with logical properties is a better fit for a bilingual product, and the team asked for Tailwind. |
