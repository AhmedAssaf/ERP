# QA report: foundation and admin UI slices
Date: 2026-09-27. Author: qa-engineer agent. Branch: admin-ui (contains foundation).
## Results before fixes
Build -warnaserror clean; format clean; tests 627 total, 620 passed, 7 failed by design (defects D-1, D-2).
## Acceptance matrix
| ID | Evidence | Verdict |
|---|---|---|
| W-02 | ModuleBoundaryTests, ModuleRegistrationTests, clean build | Covered |
| W-03 | RowLevelSecurityTests (EF, raw SQL, no tenant, pooled connection), TenantTableCatalogTests | Covered |
| W-04 | KeycloakTokenTests, OidcChallengeTests | Covered (worker client pending, per backlog) |
| W-05 | PhysicalUtilityLintTests, TailwindBuildTests | Partly: lint runs as a test until CI exists (W-09) |
| W-07 | LocalizationTests, AdminLocalizationTests, PlatformConsoleLocalizationTests, ResourceKeyUsageTests, ResourceParityTests | Covered for pages as first rendered; dialogs and toasts in English only |
| F-56 | WorkflowExecutorTests, WorkflowDefinitionsTests, DefinitionValidatorTests | Covered |
| W-06 subset | GalleryTests, bUnit component tests; overflow and golden set via Playwright | Partly: overflow and golden set outside dotnet test |
| W-08 | JobTests, dashboard tests | Covered as changed by spec D-1; deadline job skeleton not built |
| F-02 | BrandingServiceTests, BrandingPageTests, BrandingInputTests, LogoUploadInputTests, AdminIsolationTests | Covered as narrowed; defect D-2 |
| F-06 | StaffInvitationTests, StaffInvitationReuseTests, StaffInputTests | Partly: lockout auditing is W-28; defect D-1 |
| F-07 | TenantRolesTests, AdminRoleMatrixTests | Covered as narrowed |
| F-51 | PlatformConsoleTests, PlatformHostTests, HealthChecksTests, CrossHostSessionTests | Covered as narrowed |
| F-54 | PlatformConsoleTests, TenantCatalogTests | Partly: re-run proven at service level, dialog click untested |
| F-60 | AlertsTests, AlertFallbackTests | Covered as narrowed |
## Tests added by QA (124)
Isolation of admin pages and upload; cross-host sessions; role matrix; Arabic/RTL on admin and console pages; resource key usage; negative inputs; invitation link reuse.
## Defects
- D-1 (Medium): invitation email accepts bidi-override and zero-width characters. Test: StaffInputTests.An_email_with_a_bidi_override_or_zero_width_character_is_refused. **Fixed in commit 3644a9a**: the invisible/bidi rule already used for staff display names now lives in a shared helper, `Platform.Shared.Text.TextSafety.HasInvisibleOrBidiControl`, and `StaffService.NormalizeEmail` refuses an address that contains one (also tightened well beyond `MailAddress.TryCreate`, see Observations below).
- D-2 (Low to medium): portal name accepts the same characters. Test: BrandingInputTests.A_portal_name_with_a_bidi_override_or_zero_width_character_is_refused. **Fixed in commit 3644a9a**: `BrandingService.SaveAsync` calls the same shared helper before storing a portal name.
## Observations
MailAddress accepts quoted local parts, localhost, IP literals, trailing dot, leading-hyphen labels. Any Keycloak 400 maps to the name error. No admin navigation link exists, so "the evaluator saw no admin link" proves nothing.

**Fixed in commit 3644a9a**: the email rule is now stricter than `MailAddress.TryCreate` (ASCII only, no quoted local part, a domain of at least two letter/digit/hyphen labels not starting or ending with a hyphen, no trailing dot, no dotted-quad IP literal, at most 254 characters), and a Keycloak 400 while inviting someone maps to the name error only when it is the create-user request itself that Keycloak refused (`KeycloakAdminException.DuringUserCreation`); a 400 from an earlier step of the same call, such as renewing the service account's own token, is reported as the generic `identity.invitation_failed` instead.

**Fixed in commit 2** (this branch, after 3644a9a): the tenant's home page and the admin layout now show links to Staff and Branding, but only to a signed-in tenant admin (checked with `IAuthorizationService.AuthorizeAsync` against the `TenantAdmin` policy, the same check the pages themselves already enforce); every other role, and a member of the organization with no member row, sees no admin navigation on either surface, in both languages.
## Risks not covered by automation
72-hour invitation expiry only checked from token timestamps; lockout auditing (W-28); overflow and golden screenshots outside dotnet test; end-to-end scripts outside the repository; dialogs and toasts in Arabic; F-54 re-run via the browser; branding cache across several web instances; no CI (W-09).

**Addressed in commit 2**: the end-to-end scripts (`tenant.mjs`, `platform.mjs`, `golden.mjs`, `check.mjs`, shared `lib.mjs`) are no longer outside the repository; they live in `tests/e2e/` with secrets read from `infra/compose/.env` at run time, captured state in a git-ignored `tests/e2e/.state/` folder, the Chromium path overridable through `E2E_CHROMIUM_PATH`, and a README covering prerequisites and what each script proves. They are still not run as part of `dotnet test` or any CI, so this is evidence the scripts exist and are usable, not that they were re-run for this report.
## Recommendation
foundation: go. admin-ui: go. D-1 and D-2 are fixed (commit 3644a9a) and the missing admin-navigation observation is fixed (commit 2); the full suite is green (654 tests, 654 passed, 0 failed, 0 skipped), `dotnet build -warnaserror` and `dotnet format --verify-no-changes` are both clean.
