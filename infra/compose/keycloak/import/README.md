# Keycloak realm import

Files in this folder are imported by Keycloak on start (`start-dev --import-realm`). Admin console: http://localhost:8080 with the credentials from `.env`.

## waslabid-realm.json

Realm `waslabid` with Organizations: clients `waslabid-web` (code flow with PKCE, confidential) and `waslabid-tests`
(password grant, tests only), organizations `acme` and `beta` with one admin user each. The client secret and the
users' password are `${...}` placeholders filled from `infra/compose/.env` at import. Keycloak imports a realm only
when it does not exist yet; after editing this file, delete the realm in the admin console and restart Keycloak.
