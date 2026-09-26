# Keycloak realm import

Files in this folder are imported by Keycloak on start (`start-dev --import-realm`). Admin console: http://localhost:8080 with the credentials from `.env`.

## waslabid-realm.json

Realm `waslabid` with Organizations: clients `waslabid-web` (code flow with PKCE, confidential) and `waslabid-tests`
(password grant, tests only), organizations `acme` and `beta` with one admin user each. The client secret and the
users' password are `${...}` placeholders filled from `infra/compose/.env` at import. Keycloak imports a realm only
when it does not exist yet; after editing this file, delete the realm in the admin console and restart Keycloak.

## waslabid-platform-realm.json

Realm `waslabid-platform` for platform staff only (D-1): no organizations, one confidential client
`waslabid-platform-web` (code flow with PKCE, redirect `https://platform.localhost[:8443]/signin-platform`, secret
`${WASLABID_PLATFORM_CLIENT_SECRET}`), realm role `platform-admin`, and one user `platform.admin` with password
`${WASLABID_DEV_USER_PASSWORD}` and required action `CONFIGURE_TOTP`. OTP policy: TOTP, SHA-1, 6 digits, 30 seconds.

How an OTP login yields `acr` = `2` in Keycloak 26.3 (D-2), all four parts needed, each proven by
`tests/Platform.IntegrationTests/Identity/PlatformSignInTests.cs`:

1. Browser flow `platform browser` (bound as the realm's `browserFlow`): Cookie, or the sub-flow `platform browser forms`
   with two CONDITIONAL sub-flows. `platform level 1 password` = "Condition - Level of Authentication" (level 1, max age
   36000 s) plus Username Password Form; `platform level 2 otp` = the same condition at level 2 (max age 0, so every login
   asks for the code) plus OTP Form, REQUIRED. A user without an OTP credential is sent to `CONFIGURE_TOTP` by that
   step, so every platform account ends up with a second factor, not only the imported one.
2. Realm attribute `acr.loa.map` = `{"1":1,"2":2}`: the acr value names are the level numbers, so the claim reads `"2"`.
3. Client attribute `minimum.acr.value` = `2` on `waslabid-platform-web`: without it a request that asks for no acr is
   satisfied at level 1 and the OTP step is skipped. The app also sends `acr_values=2`.
4. The client's default scopes include `acr` (the claim mapper), and a client mapper puts realm roles in the id token as
   `roles`. The app keeps `acr` on the principal (the ASP.NET Core handler deletes it by default).

What does not work, found while building this: the password grant never yields `acr` 2, not even with a valid `totp`
parameter and not with the same level conditions in a direct-grant flow (Keycloak evaluates them only in browser
flows), so no password-grant client exists in this realm; the tests seed an OTP credential in a test-only copy of the
realm and drive the browser flow. Defining a `requiredActions` array in the import replaces Keycloak's built-in list
(only the listed actions remain), so this file sets the required action on the user instead. The `ByHost`/host rules
that keep this realm's cookie off tenant hosts are in `src/Platform.Web/PlatformHost/`.
