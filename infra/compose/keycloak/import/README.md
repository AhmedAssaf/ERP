# Keycloak realm import

Files in this folder are imported by Keycloak on start (`start-dev --import-realm`). Admin console: http://localhost:8080 with the credentials from `.env`.

## waslabid-realm.json

Realm `waslabid` with Organizations: clients `waslabid-web` (code flow with PKCE, confidential) and `waslabid-tests`
(password grant, tests only), organizations `acme` and `beta` with one admin user each. `waslabid-web` requires pushed
authorization requests (`require.pushed.authorization.requests`; the app already uses PAR, so a front-channel request
without it is refused) and accepts exactly the signed-out callbacks `https://{acme,beta}.localhost[:8443]/signout-callback-oidc`
as post-logout redirect URIs. The client secrets and the
users' password are `${...}` placeholders filled from `infra/compose/.env` at import. Keycloak imports a realm only
when it does not exist yet; after editing this file, delete the realm in the admin console and restart Keycloak.

### Staff invitations, TOTP and lockout (F-06, plan task 9, spec D-4 and D-5)

| Setting | Value | Why |
|---|---|---|
| Brute-force detection | on; temporary lockout (`permanentLockout` false) after `failureFactor` 3, wait 900 s (`waitIncrementSeconds` and `maxFailureWaitSeconds`), failures forgotten after 12 h; Keycloak's default quick-login check kept (two failures under 1 s apart lock for 60 s) | F-06: three wrong TOTP codes lock the account for fifteen minutes. Wrong passwords count too. |
| Browser flow | `tenant browser`: Keycloak 26.3's built-in browser flow (cookie, identity provider redirector, the organization identity-first step) with the forms sub-flow changed to Username Password Form REQUIRED plus OTP Form REQUIRED | every tenant user needs TOTP at every login; a user without an OTP credential is sent to `CONFIGURE_TOTP` by that step. OTP policy TOTP, SHA-1, 6 digits, 30 s. |
| Required actions | not defined in the file | `CONFIGURE_TOTP` and `UPDATE_PASSWORD` are enabled by default in 26.3; an import that defines `requiredActions` replaces the built-in list. `acme.admin` and `beta.admin` carry `CONFIGURE_TOTP` as a user required action, so they enrol on their first login. |
| SMTP | `mailpit:1025`, from `no-reply@waslabid.test` (display name WaslaBid), no auth, no TLS | Compose's Mailpit catches the invitation emails; `mailpit` resolves on the Compose network. |
| `waslabid-admin-api` | confidential client, service account only (no browser or password flows), secret `${WASLABID_ADMIN_API_SECRET}` | the web host's Keycloak Admin API client (`KeycloakAdmin:*` settings). |
| Service-account roles | realm-management `manage-users`, `view-users`, `query-users`, `manage-realm` | Keycloak 26.3 serves every `/admin/realms/{realm}/organizations` endpoint, reads included (organization search, `members/count`, adding a member), only to `manage-realm`; `view-realm` alone gets 403 (checked on 26.3). The users endpoints (find by email, create, credentials, `execute-actions-email`) need the other three. |
| `waslabid-web` redirect URIs | also `https://{acme,beta}.localhost[:8443]/` exactly | the invitation link (`execute-actions-email`, lifespan 72 h, `client_id=waslabid-web`) returns to the tenant's home; Keycloak refuses a `redirect_uri` that is not registered on the client ("Invalid redirect uri"). |

`manage-realm` is broader than the task needs: it also lets the service account change realm settings (flows, brute
force, SMTP). There is no narrower role for organizations in 26.3 (fine-grained admin permissions v2 cover users,
clients, groups and roles, not organizations). The secret is therefore a production secret like the web client's;
revisit when Keycloak adds an organization-scoped role. **Known risk, awaiting the user's decision:** `manage-realm` on
the Admin API account means a leaked `WASLABID_ADMIN_API_SECRET` can change the whole tenant realm, not only its users
and organizations. The options are to accept it for the pilot, to move organization calls to a separate, more tightly
held account, or to wait for a narrower Keycloak role; until the user decides, the grant stays as it is.

An invitation creates the user (username and email = the invited address, email not yet verified, `locale` from the
tenant's default culture) or finds the existing one, adds them to the tenant's organization, and sends the setup link
for whatever the account still lacks (`UPDATE_PASSWORD` without a password, `CONFIGURE_TOTP` without an OTP
credential). An account that lacks nothing gets no Keycloak email; the app sends its own short notice in Arabic and
English through `Smtp:*` (Mailpit in Development), so nobody is added to a tenant without being told. A disabled
account is refused. Keycloak's organization search matches names and domains, not aliases, so the client lists organizations
and caches their ids by alias.

Proven by `tests/Platform.IntegrationTests/Identity/StaffInvitationTests.cs` against a test copy of this file (the
development admins lose their required action there so the `waslabid-tests` password grant works, and two acme users
get a seeded OTP credential), with Keycloak and Mailpit on one Docker network under the alias `mailpit`:
`Three_wrong_totp_codes_lock_the_account` drives the browser flow from the app's own challenge (the password grant never
reaches the browser flow's OTP form), spaces the attempts more than a second apart so only `failureFactor` can lock,
and checks Keycloak's attack-detection status after each code. The lockout is not yet copied into the tenant's audit;
that is W-28.

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

### Hardening (review of task 6)

| Setting | Value | Why |
|---|---|---|
| Brute-force detection | on; temporary lockout (`permanentLockout` false) after `failureFactor` 5, wait 60 s growing to at most 900 s, failures forgotten after 12 h; two failures under 1 s apart lock for at least 60 s | a console account reaches every tenant; permanent lockout would let anyone lock out the operators |
| `passwordPolicy` | `length(12) and notUsername and passwordHistory(5)` | staff passwords; history stops rotating back to an old one |
| `sslRequired` | `external` (unchanged) | local development talks to Keycloak over plain http on `localhost`, which `external` allows; every non-private address still needs TLS. A production realm should use `all`; that realm does not exist yet. |
| PAR | `require.pushed.authorization.requests` on `waslabid-platform-web` | the app always pushes; a hand-built front-channel request (for example one that leaves out `acr_values`) is refused |
| Post-logout URIs | exactly `https://platform.localhost[:8443]/signout-callback-platform` | no wildcard: the realm returns only to the platform scheme's signed-out callback |

Keycloak does enforce the password policy on imported credentials (checked on 26.3: a 5-character
`WASLABID_DEV_USER_PASSWORD` made the import fail with `invalidPasswordMinLengthMessage` and Keycloak did not start), so
the dev password in `.env` must be at least 12 characters and differ from the user names (`.env.example` says so; the
tests use a 15-character one). The tenant realm has no policy yet; its users are tenants' own staff (F-03).

Proven by `PlatformSignInTests`: five wrong passwords then the right one leave the login form in place
(`Five_wrong_passwords_lock_the_account_so_the_right_one_is_refused`); a request without `acr_values` still reaches the
OTP form because of `minimum.acr.value` alone, shown against a control client without the attribute that completes on
the password (`Without_acr_values_the_clients_minimum_acr_alone_asks_for_the_code`; both use a public copy of
`waslabid-platform-web` without PAR that exists only in the test realm). The tests import the repository realm as it
is, plus test users and clients.

### Sign-out

`POST /platform/sign-out` (platform host) and `POST /account/sign-out` (tenant hosts), each with an antiforgery token,
delete that host's cookie only and redirect to the realm's end-session endpoint with `client_id` and
`post_logout_redirect_uri` (the handlers keep no id token, so there is no `id_token_hint`). With a live Keycloak session
Keycloak asks the user to confirm the sign-out, then returns to the signed-out callback, and the app sends the browser
to `/platform` or `/`. After editing either realm file, delete the realm in the admin console and restart Keycloak so
the new settings are imported.
