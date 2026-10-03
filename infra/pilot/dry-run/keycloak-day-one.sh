#!/usr/bin/env bash
# Local rehearsal ONLY (dry-run/README.md): the scriptable part of docs/19 step 7, done through the Admin API on
# 127.0.0.1:8080 instead of the admin console, so provision-tenant.sh then runs as it will after day one.
#   7.1  a named master-realm admin (role admin, required actions UPDATE_PASSWORD and CONFIGURE_TOTP; enrolling the
#        authenticator is the human part)
#   7.2  client waslabid-ops (service account, every flow off) with manage-users, view-users, query-users,
#        manage-clients and manage-realm on waslabid-realm; its secret written to KEYCLOAK_OPS_CLIENT_SECRET in .env
#   7.3  the bootstrap admin deleted (its .env values stay, as the runbook says)
# Not scriptable: 7.1's OTP enrolment and 7.4, the platform admin's first sign-in (new password and authenticator).
# Secrets go to curl on stdin and into .env with builtins; nothing is printed. Safe to repeat.
set -euo pipefail
# shellcheck source=infra/pilot/lib.sh
. "$(dirname "$0")/../lib.sh"
require_root
require_env_file
KC=http://127.0.0.1:8080
NAMED_ADMIN="${NAMED_ADMIN:-ops-admin}"

admin_token() {
  printf 'grant_type=password&client_id=admin-cli&username=%s&password=%s' \
    "$(env_value KEYCLOAK_ADMIN)" "$(env_value KEYCLOAK_ADMIN_PASSWORD)" \
    | curl -fsS --max-time 20 -d @- "$KC/realms/master/protocol/openid-connect/token" | jq -r .access_token
}
ops_token() {
  printf 'grant_type=client_credentials&client_id=waslabid-ops&client_secret=%s' "$(env_value KEYCLOAK_OPS_CLIENT_SECRET)" \
    | curl -fsS --max-time 20 -d @- "$KC/realms/master/protocol/openid-connect/token" | jq -r .access_token
}
# api <method> <path> [body]: the token reaches curl through stdin (-K -).
api() {
  local args=(-fsS --max-time 20 -X "$1" -K - -H 'Content-Type: application/json')
  [ -z "${3:-}" ] || args+=(--data-binary "$3")
  printf 'header = "Authorization: Bearer %s"\n' "$TOKEN" | curl "${args[@]}" "$KC/admin/realms$2"
}

if [ -n "$(env_value KEYCLOAK_OPS_CLIENT_SECRET)" ] && TOKEN="$(ops_token)" && [ -n "$TOKEN" ] && [ "$TOKEN" != null ]; then
  log "day one already done: waslabid-ops signs in"
  exit 0
fi
TOKEN="$(admin_token)" || die "the bootstrap admin cannot sign in"

log "7.1 named admin $NAMED_ADMIN (OTP and password set at its first sign-in: the human part)"
if [ -z "$(api GET "/master/users?username=$NAMED_ADMIN&exact=true" | jq -r '.[0].id // empty')" ]; then
  api POST /master/users "$(jq -nc --arg u "$NAMED_ADMIN" --arg e "$(env_value PLATFORM_ADMIN_EMAIL)" \
    '{username:$u, email:$e, enabled:true, emailVerified:true, requiredActions:["UPDATE_PASSWORD","CONFIGURE_TOTP"]}')" > /dev/null
fi
uid="$(api GET "/master/users?username=$NAMED_ADMIN&exact=true" | jq -r '.[0].id')"
role="$(api GET /master/roles/admin)"
api POST "/master/users/$uid/role-mappings/realm" "[$role]" > /dev/null

log "7.2 client waslabid-ops and its roles on waslabid-realm"
if [ -z "$(api GET "/master/clients?clientId=waslabid-ops" | jq -r '.[0].id // empty')" ]; then
  api POST /master/clients '{"clientId":"waslabid-ops","enabled":true,"publicClient":false,"serviceAccountsEnabled":true,"standardFlowEnabled":false,"implicitFlowEnabled":false,"directAccessGrantsEnabled":false,"protocol":"openid-connect"}' > /dev/null
fi
cid="$(api GET "/master/clients?clientId=waslabid-ops" | jq -r '.[0].id')"
sa="$(api GET "/master/clients/$cid/service-account-user" | jq -r .id)"
realm_client="$(api GET "/master/clients?clientId=waslabid-realm" | jq -r '.[0].id')"
roles="$(api GET "/master/clients/$realm_client/roles" \
  | jq -c '[.[] | select(.name | IN("manage-users","view-users","query-users","manage-clients","manage-realm"))]')"
[ "$(jq length <<<"$roles")" -eq 5 ] || die "waslabid-realm does not have the five roles"
api POST "/master/users/$sa/role-mappings/clients/$realm_client" "$roles" > /dev/null
secret="$(api GET "/master/clients/$cid/client-secret" | jq -r .value)"
tmp="$(mktemp "$PILOT_DIR/.env.XXXXXX")"
while IFS= read -r line || [ -n "$line" ]; do
  if [ "${line%%=*}" = KEYCLOAK_OPS_CLIENT_SECRET ]; then printf 'KEYCLOAK_OPS_CLIENT_SECRET=%s\n' "$secret"; else printf '%s\n' "$line"; fi
done < "$ENV_FILE" > "$tmp"
unset secret
chmod 600 "$tmp"; mv "$tmp" "$ENV_FILE"
TOKEN="$(ops_token)"
[ -n "$TOKEN" ] && [ "$TOKEN" != null ] || die "waslabid-ops cannot sign in with the secret just stored"

log "7.3 deleting the bootstrap admin (signed in as waslabid-ops from here on)"
TOKEN="$(admin_token)"
boot="$(api GET "/master/users?username=$(env_value KEYCLOAK_ADMIN)&exact=true" | jq -r '.[0].id // empty')"
[ -z "$boot" ] || api DELETE "/master/users/$boot" > /dev/null
if admin_token > /dev/null 2>&1; then die "the bootstrap admin can still sign in"; fi
log "day one done; still by hand: $NAMED_ADMIN's password and OTP, and the platform admin's first sign-in (docs/19 step 7)"
