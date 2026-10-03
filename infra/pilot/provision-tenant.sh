#!/usr/bin/env bash
# W-19: provisions one tenant on the pilot by hand, because tenant provisioning (F-01) is not built yet. Safe to repeat:
# every step finds what exists and adds only what is missing. Replace with F-01 when it ships.
#
#   sudo infra/pilot/provision-tenant.sh --slug acme --name "Acme Contracting" --culture ar-SA --color '#0F766E' \
#        --admin-email admin@acme.example.sa --admin-first Sara --admin-last Alqahtani
#
# It does, in this order:
#   1. checks that <slug>.<TENANT_BASE_DOMAIN> is the host KEYCLOAK_TENANT_URL gives and is neither the platform nor
#      the auth host (its certificate comes on demand at the first HTTPS request once step 3 has run; no deploy)
#   2. Keycloak (realm waslabid, Admin API on 127.0.0.1:8080 as waslabid-ops, or the bootstrap admin before day one):
#      the organization (alias = slug), the tenant host's redirect and sign-out URIs on client waslabid-web, the tenant
#      admin's account (required actions UPDATE_PASSWORD and CONFIGURE_TOTP) and its organization membership
#   3. PostgreSQL: tenancy.tenants, tenancy.tenant_hosts and the tenant admin's member row, keyed by email; the row binds
#      to the Keycloak user on that user's first sign-in, as in development (F-07)
#   4. the setup email (link valid 72 hours), again on every run while the account's setup is unfinished
#   5. asks the web host's on-demand TLS endpoint whether the host is allowed (it is once step 3 has run)
# Not done (no page needs it before the tender slice): the default approval chain (F-56 seed).
set -euo pipefail
# shellcheck source=infra/pilot/lib.sh
. "$(dirname "$0")/lib.sh"

SLUG="" NAME="" CULTURE="ar-SA" COLOR="#0F766E" EMAIL="" FIRST="" LAST=""
while [ $# -gt 0 ]; do
  case "$1" in
    --slug) SLUG="${2:?}"; shift 2 ;;
    --name) NAME="${2:?}"; shift 2 ;;
    --culture) CULTURE="${2:?}"; shift 2 ;;
    --color) COLOR="${2:?}"; shift 2 ;;
    --admin-email) EMAIL="$(printf '%s' "${2:?}" | tr '[:upper:]' '[:lower:]')"; shift 2 ;;
    --admin-first) FIRST="${2:?}"; shift 2 ;;
    --admin-last) LAST="${2:?}"; shift 2 ;;
    *) sed -n '2,18p' "$0"; exit 2 ;;
  esac
done
if [ -z "$SLUG" ] || [ -z "$NAME" ] || [ -z "$EMAIL" ] || [ -z "$FIRST" ] || [ -z "$LAST" ]; then sed -n '2,18p' "$0"; exit 2; fi
[[ "$SLUG" =~ ^[a-z0-9-]{2,40}$ ]] || die "slug must match ^[a-z0-9-]{2,40}$"
[[ "$COLOR" =~ ^#[0-9A-Fa-f]{6}$ ]] || die "color must be #RRGGBB"
[ "$CULTURE" = "ar-SA" ] || [ "$CULTURE" = "en-US" ] || die "culture must be ar-SA or en-US"
[[ "$EMAIL" =~ ^[^@[:space:]]+@[^@[:space:]]+\.[^@[:space:]]+$ ]] || die "admin email looks invalid"

require_root
require_env_file
command -v jq >/dev/null || die "jq is missing (bootstrap.sh installs it)"
IMAGE_TAG="$(cat "$STATE_DIR/deployed-tag" 2>/dev/null || echo unknown)"
export IMAGE_TAG

TENANT_URL_TEMPLATE="$(env_value KEYCLOAK_TENANT_URL)"
TENANT_URL="${TENANT_URL_TEMPLATE//\{slug\}/$SLUG}"
HOST="$(printf '%s' "$TENANT_URL" | sed -E 's#^https://([^/:]+)/?.*$#\1#')"
if [ -z "$HOST" ] || [ "$HOST" = "$TENANT_URL" ]; then die "KEYCLOAK_TENANT_URL must look like https://{slug}.example.sa/"; fi
LOCALE="${CULTURE%%-*}"

log "1. tenant host $HOST"
BASE_DOMAIN="$(tenant_base_domain)" || die "fix TENANT_BASE_DOMAIN or KEYCLOAK_TENANT_URL in $ENV_FILE"
[ "$HOST" = "$SLUG.$BASE_DOMAIN" ] || die "$HOST is not $SLUG.$BASE_DOMAIN (KEYCLOAK_TENANT_URL and TENANT_BASE_DOMAIN disagree)"
if [ "$HOST" = "$(env_value PLATFORM_HOST)" ] || [ "$HOST" = "$(env_value AUTH_HOST)" ]; then
  die "$HOST is the platform or auth host; choose another slug"
fi

KC=http://127.0.0.1:8080
REALM=waslabid
# After day one (docs/19 step 7) the master realm's client waslabid-ops (service account, client credentials) is used;
# before that, the temporary bootstrap admin. Credentials go to curl on stdin (-d @-), never on the command line.
token() {
  local ops_secret
  ops_secret="$(env_value KEYCLOAK_OPS_CLIENT_SECRET)"
  if [ -n "$ops_secret" ]; then
    printf 'grant_type=client_credentials&client_id=waslabid-ops&client_secret=%s' "$ops_secret"
  else
    printf 'grant_type=password&client_id=admin-cli&username=%s&password=%s' \
      "$(env_value KEYCLOAK_ADMIN)" "$(env_value KEYCLOAK_ADMIN_PASSWORD)"
  fi | curl -fsS --max-time 20 -d @- "$KC/realms/master/protocol/openid-connect/token" | jq -r .access_token
}
TOKEN="$(token)" || die "Keycloak refused the credentials (KEYCLOAK_OPS_CLIENT_SECRET, or KEYCLOAK_ADMIN before day one)"
if [ -z "$TOKEN" ] || [ "$TOKEN" = null ]; then die "no admin token from Keycloak"; fi
# kc <method> <path> [json body]: the token goes to curl through stdin (-K -), never on the command line.
kc() {
  local method="$1" path="$2" body="${3:-}"
  local args=(-sS --max-time 20 -X "$method" -K - -H 'Content-Type: application/json' -w '\n%{http_code}')
  [ -n "$body" ] && args+=(--data-binary "$body")
  printf 'header = "Authorization: Bearer %s"\n' "$TOKEN" | curl "${args[@]}" "$KC/admin/realms/$REALM$path"
}
# body_of / code_of split kc's output (body, then the status code on the last line).
body_of() { sed '$d' <<<"$1"; }
code_of() { tail -n 1 <<<"$1"; }

log "2a. organization $SLUG"
out="$(kc GET "/organizations?first=0&max=1000")"; [ "$(code_of "$out")" = 200 ] || die "listing organizations failed ($(code_of "$out"))"
ORG_ID="$(body_of "$out" | jq -r --arg a "$SLUG" '.[] | select(.alias == $a) | .id')"
if [ -z "$ORG_ID" ]; then
  # The tenant host, not the admin's email domain: a domain belongs to one organization only, and admins of different
  # tenants may share a provider (gmail.com). Unverified, so it routes no sign-in.
  out="$(kc POST /organizations "$(jq -nc --arg n "$NAME" --arg a "$SLUG" --arg d "$HOST" '{name:$n, alias:$a, enabled:true, domains:[{name:$d, verified:false}]}')")"
  [ "$(code_of "$out")" = 201 ] || die "creating the organization failed ($(code_of "$out")): $(body_of "$out")"
  out="$(kc GET "/organizations?first=0&max=1000")"
  ORG_ID="$(body_of "$out" | jq -r --arg a "$SLUG" '.[] | select(.alias == $a) | .id')"
  log "   created ($ORG_ID)"
else
  log "   exists ($ORG_ID)"
fi

log "2b. redirect URIs on waslabid-web for $HOST"
out="$(kc GET "/clients?clientId=waslabid-web")"; CLIENT="$(body_of "$out" | jq -c '.[0]')"
CLIENT_ID="$(jq -r .id <<<"$CLIENT")"
if [ -z "$CLIENT_ID" ] || [ "$CLIENT_ID" = null ]; then die "client waslabid-web not found in realm $REALM"; fi
UPDATED="$(jq -c --arg h "$HOST" '
  .redirectUris = ((.redirectUris // []) + ["https://\($h)/signin-oidc", "https://\($h)/"] | unique)
  | .attributes["post.logout.redirect.uris"] = (
      ((.attributes["post.logout.redirect.uris"] // "") | split("##") | map(select(. != "")))
      + ["https://\($h)/signout-callback-oidc"] | unique | join("##"))' <<<"$CLIENT")"
# Compared as sets: Keycloak keeps its own order, jq's unique sorts.
same() { jq -S '[(.redirectUris // [] | sort), ((.attributes["post.logout.redirect.uris"] // "") | split("##") | map(select(. != "")) | sort)]' <<<"$1"; }
if [ "$(same "$UPDATED")" != "$(same "$CLIENT")" ]; then
  out="$(kc PUT "/clients/$CLIENT_ID" "$UPDATED")"; [ "$(code_of "$out")" = 204 ] || die "updating waslabid-web failed ($(code_of "$out"))"
  log "   added"
else
  log "   already present"
fi

log "2c. tenant admin account $EMAIL"
out="$(kc GET "/users?email=$(jq -rn --arg e "$EMAIL" '$e|@uri')&exact=true")"
USER_ID="$(body_of "$out" | jq -r '.[0].id // empty')"
if [ -z "$USER_ID" ]; then
  out="$(kc POST /users "$(jq -nc --arg e "$EMAIL" --arg f "$FIRST" --arg l "$LAST" --arg loc "$LOCALE" \
    '{username:$e, email:$e, firstName:$f, lastName:$l, enabled:true, emailVerified:false, attributes:{locale:[$loc]}, requiredActions:["UPDATE_PASSWORD","CONFIGURE_TOTP"]}')")"
  [ "$(code_of "$out")" = 201 ] || die "creating the user failed ($(code_of "$out")): $(body_of "$out")"
  out="$(kc GET "/users?email=$(jq -rn --arg e "$EMAIL" '$e|@uri')&exact=true")"
  USER_ID="$(body_of "$out" | jq -r '.[0].id')"
  log "   created ($USER_ID)"
else
  log "   exists ($USER_ID)"
fi
out="$(kc POST "/organizations/$ORG_ID/members" "\"$USER_ID\"")"
case "$(code_of "$out")" in
  201) log "   added to organization $SLUG" ;;
  409) log "   already a member of $SLUG" ;;
  *) die "adding the member failed ($(code_of "$out")): $(body_of "$out")" ;;
esac

log "3. PostgreSQL rows"
export P_SLUG="$SLUG" P_NAME="$NAME" P_CULTURE="$CULTURE" P_COLOR="$COLOR" P_HOST="$HOST" P_EMAIL="$EMAIL" P_DISPLAY="$FIRST $LAST"
PSQL_ENV="P_SLUG P_NAME P_CULTURE P_COLOR P_HOST P_EMAIL P_DISPLAY" psql_owner <<'SQL'
\getenv slug P_SLUG
\getenv name P_NAME
\getenv culture P_CULTURE
\getenv color P_COLOR
\getenv host P_HOST
\getenv email P_EMAIL
\getenv display P_DISPLAY
begin;
insert into tenancy.tenants (id, slug, keycloak_org_alias, default_culture, portal_name, primary_color)
values (gen_random_uuid(), :'slug', :'slug', :'culture', :'name', :'color')
on conflict (slug) do nothing;
select id as tenant_id from tenancy.tenants where slug = :'slug' \gset
insert into tenancy.tenant_hosts (host, tenant_id) values (lower(:'host'), :'tenant_id') on conflict (host) do nothing;
select set_config('app.tenant_id', :'tenant_id', true) as tenant_context \gset
insert into identity.members (id, tenant_id, user_id, email, display_name, roles, status, invited_at)
values (gen_random_uuid(), :'tenant_id', null, :'email', :'display', array['tenant-admin'], 'invited', now())
on conflict (tenant_id, email) do nothing;
commit;
select format('tenant %s (%s) on %s; tenant admins: %s', t.slug, t.id, h.host,
              (select string_agg(m.email, ', ') from identity.members m where m.tenant_id = t.id and 'tenant-admin' = any (m.roles)))
from tenancy.tenants t join tenancy.tenant_hosts h on h.tenant_id = t.id where t.slug = :'slug';
SQL

# Last, so a mail problem never leaves the tenant half made. Sent again on every run while the account still has setup
# steps pending (a lost or expired link is fixed by running the script again).
log "4. setup email"
out="$(kc GET "/users/$USER_ID")"
pending="$(body_of "$out" | jq -r '.requiredActions // [] | length')"
if [ "$pending" -gt 0 ]; then
  redirect="$(jq -rn --arg u "https://$HOST/" '$u|@uri')"
  out="$(kc PUT "/users/$USER_ID/execute-actions-email?client_id=waslabid-web&redirect_uri=$redirect&lifespan=259200" '["UPDATE_PASSWORD","CONFIGURE_TOTP"]')"
  [ "$(code_of "$out")" = 204 ] || die "sending the setup email failed ($(code_of "$out")): check the SMTP relay (docker compose ... logs smtp-relay), then run this again"
  log "   sent to $EMAIL (link valid 72 hours)"
else
  log "   not needed: the account has finished its setup"
fi
unset TOKEN
log "5. on-demand certificate for $HOST"
# The web host's directory remembers a missing host for 5 seconds, so a lookup just before step 3 can still say no.
ask="000"
for _ in 1 2 3 4; do
  ask="$(web_status "/internal/tls-ask?domain=$HOST" 8081)"
  [ "$ask" = "200" ] && break
  sleep 3
done
if [ "$ask" = "200" ]; then
  log "   allowed: Caddy obtains the certificate at the first HTTPS request (DNS for $HOST must point at this VM)"
else
  warn "the ask endpoint answered $ask for $HOST, expected 200: no certificate will be issued (docs/19 section 9)"
fi

log "done: $EMAIL opens the setup link, sets a password and an authenticator, then signs in at https://$HOST/"
