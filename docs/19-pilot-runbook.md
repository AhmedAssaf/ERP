# 19. Pilot runbook: Oracle Cloud Always Free, Jeddah (W-19)

Prepared on 2026-10-03 on branch `w-19-pilot-prep` as pre-gate-1 preparation (docs/05 section 8). Nothing is deployed:
you create the Oracle Cloud account and the VM, then follow section 4. Every file named here is under `infra/pilot/`.
Acceptance (docs/09, W-19): the pilot tenant's host serves over HTTPS from one Jeddah VM, all data stays in Jeddah (N-01),
a nightly backup lands in Jeddah object storage (N-07), and a destroyed VM is rebuilt from backup within one hour.

## 1. What runs where

```mermaid
flowchart LR
    U["Tenant staff and vendors"] -->|"HTTPS 443"| SL
    OP["Operator<br/>allow-listed IP"] -->|"SSH 22, tunnel to Kibana"| SL
    subgraph OCI["Oracle Cloud, Jeddah (me-jeddah-1): all data stays here (N-01)"]
        SL["Security list<br/>22 from your IP, 80 and 443"] --> CADDY
        subgraph VM["VM.Standard.A1.Flex, 2 OCPU, 12 GB, aarch64"]
            CADDY["Caddy<br/>TLS, security headers"]
            subgraph EDGE["edge network"]
                WEB["Platform.Web"]
                KC["Keycloak 26"]
            end
            subgraph BACK["backend network (internal, no route out)"]
                WRK["Platform.Worker"]
                PG[("PostgreSQL 16")]
                RD[("Redis")]
                S3[("MinIO")]
                COL["OTel Collector"] --> ES[("Elasticsearch")]
                KIB["Kibana, on demand"]
            end
            subgraph EGR["backend and egress networks"]
                AV["ClamAV"]
            end
            CADDY --> WEB & KC
            CADDY -.->|"on-demand TLS ask, port 8081"| WEB
            WEB --> PG & RD & S3 & AV & COL
            WRK --> PG & RD & S3 & AV & COL
            KC --> PG
            KIB --> ES
            BK["backup.sh, nightly"] --> PG & S3
        end
        OS[("Object Storage<br/>encrypted backups")]
        MAIL["Email Delivery"]
    end
    BK -.->|"encrypted"| OS
    WEB & WRK & KC -.->|"SMTP, STARTTLS 587, login"| MAIL
    AV -.->|"signature updates only"| CDN["ClamAV mirror"]
    CADDY -.->|"certificate issuance only"| LE["Let's Encrypt"]
```

Only Caddy publishes ports (80 and 443). Keycloak's port 8080 and Kibana's 5601 bind to 127.0.0.1 for scripts and the SSH
tunnel. Outside Oracle's Jeddah region only two things travel, and neither carries customer data: certificate requests
(host names) and ClamAV signature downloads.

Memory limits (W-10 spec section 9, applied in `docker-compose.yml`):

| Service | Limit | Service | Limit |
|---|---|---|---|
| PostgreSQL | 1.5 GB | Web host | 1 GB |
| Keycloak | 1.5 GB | Worker | 512 MB |
| ClamAV (`ConcurrentDatabaseReload no`) | 2 GB | OTel Collector (`GOMEMLIMIT` 250 MiB) | 320 MB |
| Elasticsearch (768 MB heap) | 1.75 GB | MinIO, Redis, Caddy | 1 GB together |
| **Steady total** | **9.56 GB** | Kibana, only while open | 1.25 GB |

## 2. The files

| File | What it is |
|---|---|
| `docker-compose.yml` | The pilot stack: production settings, limits, networks, health checks, no port but 80 and 443 |
| `docker/app.Dockerfile` | Web, worker and migrator images: .NET 10 pinned by digest, non-root (uid 1654), read-only root file system |
| `Caddyfile` | TLS (tenant hosts on demand, after the web host's ask endpoint), security headers, Keycloak admin only from your IP |
| `keycloak/import/*.json` | The two realms for production: no development users, no test client, placeholders from `.env` |
| `postgres/bootstrap.sql` | Roles `erp_app` and `keycloak`, databases, extensions; passwords sent only as SCRAM verifiers |
| `.env.example`, `generate-secrets.sh` | Every setting with a generation hint; the script fills empty secrets and makes the key-ring certificate |
| `bootstrap.sh` | VM preparation: Docker, firewall, kernel, swap, NTP, security updates, SSH, systemd timers |
| `build-images.sh` | On your laptop: builds the arm64 images and loads them on the VM over SSH (no registry) |
| `deploy.sh` | Idempotent deploy in order, migrator first, then health verification |
| `provision-tenant.sh` | One tenant by hand until F-01: Keycloak organization, redirect URIs, tenant admin, database rows |
| `backup.sh`, `restore.sh`, `rclone.sh` | Nightly encrypted backup, restore test, disaster recovery |
| `watchdog.sh`, `systemd/` | Alerts when the worker, web host, backup or disk fail (the worker cannot report its own death) |
| `compose.sh`, `lib.sh`, `minio-init.sh` | Operations wrapper, shared helpers, MinIO users and lifecycle |
| `dry-run/` | The local rehearsal only (section 10): a runner container as the VM, a Compose override with stand-ins; never used on the VM |

Where each host setting outside Development comes from (docs/07 section 4):

| Setting | Source |
|---|---|
| `ConnectionStrings:Platform`, `KeyRing` (web), `Worker` (worker), `Owner` (migrator) | `ERP_APP_DB_PASSWORD`, `ERP_KEY_RING_DB_PASSWORD`, `ERP_WORKER_DB_PASSWORD`, `POSTGRES_PASSWORD` |
| `ConnectionStrings:Redis` (both hosts) | `REDIS_PASSWORD`; required by the web host, and the worker's F-60 Redis check (W-34) |
| `Jobs:SigningKey` (both) | `WASLABID_JOB_SIGNING_KEY` (W-42) |
| `Telemetry:OtlpEndpoint`, `Telemetry:Environment` (both) | the collector on the backend network; `pilot` |
| `Oidc:*`, `PlatformOidc:*`, `Platform:Host` | `AUTH_HOST`, `PLATFORM_HOST`, the two client secrets |
| `KeycloakAdmin:BaseUrl`, `ClientSecret`, `TenantUrl` | `http://keycloak:8080` (internal), `WASLABID_ADMIN_API_SECRET`, `KEYCLOAK_TENANT_URL` |
| `ForwardedHeaders:KnownProxies` (W-24) | Caddy's pinned address `CADDY_EDGE_IP` (172.30.10.2) |
| `TlsAsk:Port`, `TlsAsk:TenantBaseDomain` (web, docs/07 section 4) | `8081` (with `ASPNETCORE_HTTP_PORTS=8080;8081`), `TENANT_BASE_DOMAIN` |
| `DataProtection:CertificatePath`, `CertificatePassword` (W-24) | `secrets/key-ring.pfx` as a Compose secret, `KEY_RING_CERT_PASSWORD` |
| `ObjectStorage:*`, `Health:MinIo:*` | MinIO user `waslabid-app` (bucket only), `health-probe` (list only) |
| `ClamAv:*`, `Smtp:*`, `Platform:AlertRecipients`, `Platform:DiskPath` | `clamav:3310`, `SMTP_HOST`/`SMTP_PORT` (STARTTLS), `SMTP_USERNAME`/`SMTP_PASSWORD` and `SMTP_FROM`, `ALERT_RECIPIENT`, the database volume |
| `Vendors:CrAuditKey`, `Wathq:*` | `VENDORS_CR_AUDIT_KEY`; Wathq optional |
| `Telemetry:Elasticsearch*`, `Observability:KibanaUrl` | `waslabid_monitor` and `ELASTIC_MONITOR_PASSWORD`; `http://127.0.0.1:5601` through the tunnel |

## 3. Before you start

- A domain you control (examples below use `example.sa`), and a password manager.
- A laptop with Docker Desktop (buildx) and Git Bash, able to build the solution.
- About two hours for the first run; one hour for a rebuild from backup.

## 4. Step by step

**Step 1. Oracle Cloud tenancy.** Sign up for Oracle Cloud Free Tier and choose **Saudi Arabia West (Jeddah)** as the home
region. The home region cannot be changed later, and Always Free resources exist only there. Turn on MFA for your
console user.

**Step 2. DNS.** Point A records at the VM's public IP (step 3 gives it): `platform.example.sa`, `auth.example.sa`, and
either a wildcard `*.example.sa` (no DNS change per tenant) or one per tenant host (`acme.example.sa`). TTL 300 seconds, so a rebuilt VM is reachable quickly. Add the SPF and DKIM
records that Email Delivery shows in step 4.

**Step 3. The VM.** Networking, Virtual Cloud Networks, "VCN with Internet Connectivity". In the public subnet's security
list allow ingress TCP 22 from your address only (`/32`), and TCP 80 and 443 from `0.0.0.0/0`; remove any other ingress
rule. Then Compute, Create instance: shape **VM.Standard.A1.Flex, 2 OCPU, 12 GB**, image **Canonical Ubuntu 24.04
(aarch64)**, boot volume 100 GB (Always Free allows 200 GB of block storage in all), your SSH public key. Reserve the
public IP (Networking, Reserved public IPs) and attach it, so a rebuilt VM keeps the same address and DNS. If Jeddah
answers "Out of capacity" for A1, retry later; the paid fallback in docs/02 section 5 item 3 (about 20 USD a month) is
the answer if it persists.

**Step 4. Object Storage and email.**
1. Object Storage, Create bucket `waslabid-pilot-backups` in Jeddah, Standard tier, private, no public access. Note the
   tenancy's Object Storage namespace (bucket details).
2. Identity: a group `waslabid-backup` with one user `waslabid-backup`, and a policy that limits the group to that bucket:
   `Allow group waslabid-backup to manage objects in tenancy where target.bucket.name='waslabid-pilot-backups'` and
   `Allow group waslabid-backup to read buckets in tenancy where target.bucket.name='waslabid-pilot-backups'`.
3. On the bucket: Retention rules, Create, a time-bound rule of 35 days (the same as `BACKUP_RETENTION_DAYS`). Nothing
   younger than that can be deleted or overwritten, not even with the VM's own key, so a compromised VM cannot purge the
   off-VM copies; `backup.sh` writes each object once and prunes only after 36 days. Once a nightly backup and a restore
   test have run with the rule in place, lock it (a locked rule cannot be shortened or removed, even by an
   administrator; the console asks for a lock date at least 14 days ahead; check the current terms there).
4. On the user `waslabid-backup`: Customer secret keys, Generate. The access key and secret go to `OCI_ACCESS_KEY_ID` and
   `OCI_SECRET_ACCESS_KEY`; `OCI_S3_ENDPOINT` is `https://<namespace>.compat.objectstorage.me-jeddah-1.oraclecloud.com`.
5. Email Delivery (Jeddah): an email domain with DKIM, an approved sender (the `SMTP_FROM` address), and SMTP credentials
   for a dedicated IAM user that holds only the Email Delivery SMTP credential and no other policy (the credential is
   readable by the internet-facing web host, the worker and Keycloak; the user's only power is sending mail from the
   approved sender). Identity, Users, SMTP credentials; the password is shown once; put it in `SMTP_USERNAME` and `SMTP_PASSWORD`.
   Confirm the SMTP endpoint shown in the console matches `SMTP_HOST` (port 587, STARTTLS), and the free sending
   allowance. The web host, the worker, Keycloak (realm import) and the watchdog log in to it directly; there is no relay
   container. The worker and the web host need outbound access to port 587 (the worker is on the `egress` network, the
   web host and Keycloak reach it through `edge`); the OCI security list must allow egress to the endpoint. The
   application refuses to send the login over an unencrypted connection (STARTTLS is mandatory with a login). The
   password must not contain `$`, `"` or a backslash (`deploy.sh` refuses it). To rotate it: generate a new SMTP credential, change `.env`, run
   `deploy.sh` for the hosts, set the new password in the Keycloak admin console (realm settings, Email; the realm
   import only runs on the first start), then delete the old credential.
6. On the instance (Instance details, Instance metadata service): set it to version 2 only (IMDSv1 disabled), so a
   container cannot read instance metadata with a plain GET.
7. Observability, Notifications: a topic with your email, and Monitoring, Alarm definitions: an alarm on the instance's
   `CpuUtilization` metric being absent for 10 minutes (the VM is down), sent to that topic. The watchdog cannot see this
   case from inside the VM.

**Step 5. Prepare the VM and the secrets.** The checkout belongs to root (the scripts run with `sudo`), so the deploy key
is root's: `sudo ssh-keygen -t ed25519 -N '' -f /root/.ssh/id_ed25519`, then add `/root/.ssh/id_ed25519.pub` to the
repository on GitHub as a read-only deploy key. Then on the VM:

```bash
sudo git clone git@github.com:AhmedAssaf/ERP.git /opt/waslabid && cd /opt/waslabid
sudo SSH_ALLOW_CIDR=203.0.113.7/32 infra/pilot/bootstrap.sh      # your address; it refuses one that locks you out
sudo infra/pilot/generate-secrets.sh                              # .env and secrets/key-ring.pfx
sudo nano infra/pilot/.env                                        # hosts, emails, SMTP and OCI values (comments say which)
```

Copy `infra/pilot/.env` and `infra/pilot/secrets/key-ring.pfx` into the password manager now. Without them no backup can
be restored, and the key-ring certificate must never be in the database backup (W-24).

**Step 6. Build, ship and deploy.** On the laptop, from a clean checkout of the commit to deploy:

```bash
infra/pilot/build-images.sh ubuntu@platform.example.sa   # arm64 images, loaded on the VM over SSH
```

The images can also be built on the VM itself (Platform.UI's Tailwind step now picks the linux-arm64 CLI there, checked
by its SHA-256): `cd /opt/waslabid && sudo git checkout <tag> && sudo infra/pilot/build-images.sh` with no argument builds
the three images natively, tagged with the commit, and nothing needs shipping. Before a build on the VM, stop the heavy
services (`docker compose ... stop elasticsearch clamav kibana`; the SDK needs the memory), and start them again by
running `deploy.sh`. The arm64 build was verified under QEMU emulation on the laptop; it has not run on the VM itself.

On the VM: `cd /opt/waslabid && sudo git checkout <tag> && sudo infra/pilot/deploy.sh --tag <tag>`. The first run takes
about 15 minutes: ClamAV downloads its signatures and Keycloak builds its configuration. It ends with the checks in
section 5; a failed check names what to look at.

**Step 7. Keycloak, day one.** Open `https://auth.example.sa/admin` from your allow-listed address and sign in with
`KEYCLOAK_ADMIN` from `.env` (Keycloak marks this bootstrap account as temporary).
1. Master realm, Users: create your named admin with the `admin` role, set a password, and require an OTP
   (Authentication, Required actions, Configure OTP). Sign in again as that user.
2. Master realm, Clients: create `waslabid-ops` (client authentication on, service accounts on, every flow off). On its
   service account roles assign, from client `waslabid-realm`: `manage-users`, `view-users`, `query-users`,
   `manage-clients`, `manage-realm`. Put its secret in `.env` as `KEYCLOAK_OPS_CLIENT_SECRET`.
3. Delete the bootstrap admin user. `KEYCLOAK_ADMIN` and its password stay in `.env` (Compose requires them) but no longer
   sign in.
4. Platform console: open `https://platform.example.sa/platform`, sign in as `PLATFORM_ADMIN_USERNAME` with
   `PLATFORM_ADMIN_INITIAL_PASSWORD`; Keycloak asks for a new password and an authenticator app. The health board fills
   within two minutes.

**Step 8. The pilot tenant.**

```bash
sudo infra/pilot/provision-tenant.sh --slug acme --name "Acme Contracting" --culture ar-SA --color '#0F766E' \
     --admin-email admin@acme.example.sa --admin-first Sara --admin-last Alqahtani
```

The tenant admin gets a setup email (password and authenticator, 72 hours) and signs in at `https://acme.example.sa/`.
A new tenant later: only DNS (none with the wildcard record) and this script; no `.env` change and no deploy. The tenant
host is `<slug>.<TENANT_BASE_DOMAIN>`; the script's last step checks that the web host now allows it, and Caddy obtains
its certificate at the first HTTPS request (a few seconds; the first visitor waits for it). How that is guarded:

```mermaid
sequenceDiagram
    participant B as Browser
    participant C as Caddy (443)
    participant W as Web host, ask listener 8081
    participant D as tenancy.tenant_hosts
    participant L as Let's Encrypt
    B->>C: TLS handshake, SNI acme.example.sa (no certificate yet)
    C->>W: GET /internal/tls-ask?domain=acme.example.sa
    W->>W: one label under TENANT_BASE_DOMAIN? not the platform host, not an IP? within the rate?
    W->>D: resolve_host (cached 60 s, a miss 5 s)
    W-->>C: 200 (anything else refuses)
    C->>L: obtain certificate
    C-->>B: handshake completes
```

The ask endpoint is unauthenticated. What makes that acceptable is where it listens and how little it says. It sits on a
separate Kestrel port (8081) that serves that one path and nothing else, with no tenant resolution, sign-in or page behind
it. The port is told apart by the connection's local port, never by the `Host` header. Compose publishes no port of the
web host and no Caddy site block proxies to 8081, so nothing on the internet reaches it. Through Caddy (port 8080) any
`/internal` path is a 404 on every host. Inside the VM it is not limited to Caddy: the web host listens on all its
interfaces, so every container on the edge network (Caddy, Keycloak) and on the backend network (databases, storage,
telemetry, ClamAV, the worker, the one-shots) can call it. What such a caller gains is a tenant-existence
oracle and nothing more: whether a host is a tenant host, which DNS and HTTPS show anyway. The platform host, Keycloak's
hosts (from the two OIDC authorities) and any `TlsAsk:ExcludedHosts` are never allowed. Only a well-formed name under the
base domain that the tenant directory has not cached costs a database lookup, and those are limited to 20 a second per
instance. Junk names, the kind a handshake flood aimed at the VM's address sends, cost nothing and cannot use up the rate
for a new tenant; a refused name is asked again at its next handshake. Certificates are only ever issued for
`<one label>.<TENANT_BASE_DOMAIN>` that a tenant owns, which also bounds the Let's Encrypt rate limits (50 new
certificates per registered domain a week). Custom domains (F-03) are not allowed yet.

**Step 9. Smoke checks.** The `tests/e2e` scripts drive `*.localhost`, Mailpit and the development users, so they do not
run against the pilot yet (follow-up in section 9). By hand, in ar-SA and en-US:

| Check | Expected |
|---|---|
| `https://acme.example.sa` in a browser | the tenant's brand, Arabic right to left, a valid certificate |
| Tenant admin signs in, invites a staff user at `/admin/staff` | the invitation email arrives; the invitee enrols TOTP |
| A vendor registers at `/vendor/register`, verifies the email, uploads a certificate | the upload is scanned (ClamAV) and listed |
| Platform console health board | every tile Healthy within two minutes |
| `sudo infra/pilot/compose.sh stop clamav`, wait 2 minutes, `start clamav` | one "ClamAV is down" email, then one "has recovered" |
| `curl -sI https://acme.example.sa` | HSTS, `X-Frame-Options: DENY`, `X-Content-Type-Options: nosniff`, CSP headers |
| `curl -s -o /dev/null -w '%{http_code}' https://auth.example.sa/admin/` from another address | 404 |
| `nmap -Pn platform.example.sa` from outside | only 22 (your address), 80 and 443 |

**Step 10. First backup and restore test.** `sudo infra/pilot/backup.sh`, then `sudo infra/pilot/restore.sh --verify`.
Record the result in section 7.

## 5. What deploy.sh does

```mermaid
flowchart LR
    A["Checks<br/>.env mode 600, certificate,<br/>images present"] --> B["Start PostgreSQL, Redis,<br/>MinIO, Elasticsearch,<br/>ClamAV; wait healthy"]
    B --> C["bootstrap.sql<br/>roles, databases, extensions"]
    C --> D["minio-init<br/>elastic-setup"]
    D --> E["Keycloak, collector"]
    E --> F["Migrator<br/>must succeed"]
    F --> G["Web and worker<br/>new tag"]
    G --> H["Caddy"]
    H --> I["Verify: /health 200,<br/>ask listener 8081, worker heartbeat,<br/>HTTPS, Keycloak issuer"]
```

The migrator applies every module's migrations as the owner, installs Hangfire's tables and secures them (`jobs/`
migrations), and gives `erp_key_ring` and `erp_worker` their logins (W-24, W-36). The owner is the PostgreSQL image's
superuser, so it holds the CREATEROLE the migrations need. Each step is safe to repeat, so a failed deploy is fixed and
`deploy.sh` run again. The hosts write no console logs outside Development (W-10 Q8): look in Kibana, or for a host that
does not start, `sudo infra/pilot/compose.sh logs web` (an unhandled startup exception still reaches stderr).

## 6. Operations

- **Status:** `sudo infra/pilot/compose.sh ps`; the platform console's health board (F-51) and its alerts (F-60).
- **Watchdog:** every five minutes `watchdog.sh` emails once when the worker's heartbeat is older than three minutes, the web
  host's `/health` is not 200, the last backup is older than 26 hours, the disk passes 85%, or security updates have
  waited more than a week for a reboot; and once more on recovery. `journalctl -u waslabid-watchdog` shows its runs.
- **Kibana (O-18):** `sudo infra/pilot/compose.sh --profile kibana up -d kibana kibana-setup`, then on your laptop
  `ssh -N -L 5601:127.0.0.1:5601 ubuntu@platform.example.sa` and open `http://127.0.0.1:5601` as `KIBANA_STAFF_USER`.
  The console's "Open in Kibana" link works while the tunnel is open. Stop it afterwards: `compose.sh stop kibana`.
- **Clock:** `chronyc tracking` (Oracle's NTP at 169.254.169.254); the job locks and token lifetimes depend on it.
- **Reboots:** security updates install daily without rebooting. Reboot in a quiet hour (`sudo reboot`); every container
  restarts on its own (`restart: unless-stopped`).
- **Idle reclamation:** Oracle may reclaim an Always Free instance that stays idle for 7 days (CPU, network and, on A1,
  memory below 20%). The stack keeps memory near 80%, so this should not apply; the OCI alarm in step 4 would report it.

## 7. Backups and restore (N-07)

| What | Where | Kept |
|---|---|---|
| `pg_dumpall --globals-only --no-role-passwords`, `pg_dump` of `platform` and `keycloak`, the list of MinIO object keys (without `staging/`), checksums | `nightly/<stamp>/` | 35 days, a new snapshot nightly |
| Every MinIO object, written once and never overwritten | `minio/objects/` | while a kept snapshot lists it, and at least 36 days |

The layout is write-once, so the bucket's 35-day retention rule (step 4) never blocks a backup: a snapshot is a new
folder, objects are copied with `--immutable` (the platform writes each object once: logos by hash, documents by id),
and pruning waits 36 days. A key already stored is skipped (`--ignore-existing`), since a key always names the same bytes:
saving the same logo again rewrites its key with a new date but identical content. Each snapshot gets its `COMPLETE`
marker last, in a second upload; a run that dies mid-upload leaves a folder without it, which `restore.sh --list` shows
as INCOMPLETE and no restore ever picks.

**Erasure window (PDPL deletion, N-02):** a vendor document or row deleted on the platform leaves the backups 36 to 37
days later: the snapshots that hold it age out after 36 days and the nightly run removes them (and any object no kept
snapshot lists) within the next day. That holds only while backups succeed: a failing backup also stops pruning, which
the watchdog's backup-age alert (26 hours) reports. The retention rule makes anything younger than 35 days impossible to
delete, so an erasure request cannot be honoured sooner in the backups; say so in the privacy notice.

Everything is encrypted on the VM (rclone crypt: contents and names) before it reaches the bucket in Jeddah;
`BACKUP_CRYPT_PASSWORD` and `BACKUP_CRYPT_SALT` from the password manager are the only way to read it. The timer runs at
02:30 Riyadh time. Not backed up: Elasticsearch (telemetry), Redis (counters), Caddy certificates (re-issued), and the
secrets. Optional extra: an OCI boot volume backup policy (Always Free includes five volume backups) as a coarse second
copy. Mind the Always Free Object Storage allowance (20 GB in all): each backup logs the bucket's stored size at the end
(`journalctl -u waslabid-backup`).

**Monthly restore test:** `sudo infra/pilot/restore.sh --verify` restores the newest snapshot into a throwaway
PostgreSQL with no network, applies `bootstrap.sql` as a real restore does, prints row counts (tenants, members, key-ring
keys, forced-RLS tables, Keycloak realms and users), fails if a role is missing or cannot connect (a restore loses the
database-level grants; `bootstrap.sql` gives them back, including `erp_key_ring`'s, without which the web host cannot
start), checks that every object the snapshot lists is stored, and removes everything. Log each run here:

| Date | Backup stamp | Result | Duration |
|---|---|---|---|
| (first run after go-live) | | | |

**Disaster recovery (target: the pilot tenant back within one hour):**

| Step | Minutes |
|---|---|
| Create the VM (step 3) and move the reserved public IP to it | 10 |
| Root's deploy key, clone, `bootstrap.sh`, restore `.env` and `secrets/key-ring.pfx` from the password manager | 10 |
| `build-images.sh <vm>` for the tag in the backup's `MANIFEST` (or load saved images) | 10 |
| `sudo infra/pilot/restore.sh --full` (PostgreSQL and the MinIO objects) | 5 to 10 |
| `sudo infra/pilot/deploy.sh --tag <tag>` (ClamAV signatures, Keycloak first start) | 15 |
| Smoke checks of step 9 | 5 |

## 8. Upgrades and rollback

**Upgrade:** merge to `main`; on the laptop `infra/pilot/build-images.sh <vm>`; on the VM `sudo infra/pilot/backup.sh`
(a restore point taken just before), `sudo git fetch && sudo git checkout <tag>`, `sudo infra/pilot/deploy.sh --tag <tag>`.
Avoid deploying in the last hours before a tender deadline (N-04). First upgrade past on-demand TLS (PR #24) on a VM set
up earlier: add `TENANT_BASE_DOMAIN` (for example `example.sa`, matching `KEYCLOAK_TENANT_URL`) to `infra/pilot/.env`
before running `deploy.sh`, which otherwise stops at the Compose check ("Set TENANT_BASE_DOMAIN"); remove `TENANT_HOSTS`
(it is ignored, and `deploy.sh` warns while it is there). Third-party images: change the version and digest in
both Compose files (development first), let CI verify signatures and scan, then deploy.

**Rollback:** the previous tag is in `/var/lib/waslabid/previous-tag` and its images stay on the VM.
`sudo git checkout <previous> && sudo infra/pilot/deploy.sh --tag <previous>`. Migrations only move forward: this is safe
when the newer release's migrations only added objects; if one changed or removed something the old code reads, restore
the backup taken before the upgrade (`restore.sh --full --force`), then deploy the old tag.

## 9. Residual risks and follow-ups

| Item | Effect on the pilot | Next step |
|---|---|---|
| ~~No `ask` endpoint for on-demand TLS~~ | Closed 2026-10-03, PR #24: `GET /internal/tls-ask` on the web host's port 8081 and on-demand TLS for tenant hosts in the Caddyfile (step 8); a new tenant needs no deploy | Custom domains (F-03, W-11) stay open |
| The SMTP credential lives in the web host (internet-facing), the worker and Keycloak | A compromise of any of them can send mail as the approved sender | A dedicated IAM user whose only power is sending (step 4); rotate on suspicion; the sender domain's SPF/DKIM/DMARC limit abuse |
| Keycloak's realm setting `starttls: true` only requests STARTTLS, it does not require it: confirmed in the rehearsal (section 10), where Keycloak 26.3.5 sent a setup email with its SMTP login to a server that offered no STARTTLS (the application and the watchdog do require it) | A stripped STARTTLS on the path between the VM and Email Delivery (both in OCI Jeddah) would send Keycloak's SMTP login in clear text | Before go-live: ask whether Email Delivery offers implicit TLS (port 465) and, if so, set the realm to `ssl: true` on 465; otherwise accept for the pilot (an in-region path) or require it through a Jakarta Mail property if Keycloak exposes one; rotate the credential on suspicion |
| The SMTP health check logs in on every run (about 1,440 times a day) | Counts against Email Delivery's rate and login limits and shows in its logs; a provider lockout after failures would also stop real mail | Watch the first week; lengthen the check interval or stop logging in if the provider objects |
| The worker has internet egress (the `egress` network) and web and Keycloak have it through `edge`; every container can reach the instance metadata service at 169.254.169.254 | A compromised container can reach the internet and, with IMDSv1 off, only needs a token request for metadata | Egress restriction (W-43, docs/09); until it lands, IMDS is v2-only (step 4) and the VM firewall is the only boundary |
| One VM, no replica | N-04 (99.5%) is not guaranteed; a VM loss means up to an hour down and up to a day of data (nightly backup) | Accepted for the pilot; WAL archiving or managed PostgreSQL when hosting is re-decided in December 2026 |
| Secrets live in a root-only file and in container environments (`docker inspect`) | Root on the VM reads every secret; N-10 asks for a KMS or secret store | OCI Vault when hosting is decided; until then SSH is allow-listed and key-only |
| MinIO and PostgreSQL are encrypted at rest only by the boot volume (Oracle-managed keys) | No per-tender keys (docs/03 section 9) | KMS-backed storage with the production hosting decision |
| `tests/e2e` targets `*.localhost`, Mailpit and development users | Pilot smoke checks are manual (step 9) | Parameterise the base URLs and mail check (qa-engineer) |
| CSP is report-only beyond framing, plugins, base and form targets | Script injection is not yet blocked by the browser | Enforce after a browser pass of every page on the pilot |
| Admin API over the internal network (`http://keycloak:8080`) with a public `KC_HOSTNAME` | Expected to work (tokens carry the configured issuer); not yet seen on a real host | Confirm at the first invitation in step 9; fallback `KeycloakAdmin:BaseUrl=https://auth.example.sa` with the web host's pinned address allowed for `/admin` in the Caddyfile |
| The worker's container health check only sees the process | A hung worker is caught by the watchdog's heartbeat check within about 8 minutes, not by Docker | Accepted |
| Keycloak `manage-realm` on the Admin API account (docs: `infra/compose/keycloak/import/README.md`) | A leaked `WASLABID_ADMIN_API_SECRET` can change the tenant realm | The user's open decision, unchanged |
| Images go laptop to VM without a registry or signature | Integrity rests on SSH and the commit tag | A registry in Jeddah (OCI Container Registry) with signing, if more than one person deploys |
| Caddy runs as root inside its container | A Caddy compromise is root in that container (no capabilities but `NET_BIND_SERVICE`, read-only root file system, `no-new-privileges`) | Accepted for the pilot: non-root needs the volumes re-owned and `net.ipv4.ip_unprivileged_port_start=0`; revisit with the production hosting decision |
| MinIO runs as `0:0` (the Chainguard image's own user cannot initialise the data directory, as in development) | A MinIO compromise is root in that container; it sits on the internal network only, no port published, `no-new-privileges` | Accepted for the pilot; same follow-up as development's MinIO image |
| Always Free capacity and terms | Capacity in Jeddah is not guaranteed; Oracle may change the allowance | Paid fallback, docs/02 section 5 item 3 |

Checked locally on 2026-10-03, without any cloud account: `docker compose config` of the pilot file; the arm64 images
built on an amd64 laptop and started under emulation; with amd64 images and no published port, PostgreSQL, Redis, MinIO,
Keycloak (production mode, both realms imported), the migrator, the web host and the worker came up
healthy, `bootstrap.sql`, `minio-init.sh`, the migrator and `provision-tenant.sh` ran twice without change on the second
run, the worker's health checks reported PostgreSQL, Redis, MinIO, Disk, Web and Worker healthy, and the dumps of
`backup.sh` restored into a throwaway container with the integrity checks of `restore.sh`; shellcheck, Caddy's validator
and Trivy (no fixable HIGH or CRITICAL in the web image) passed. Not checked: anything that needs Oracle Cloud (Object
Storage upload, Email Delivery, DNS and certificates, the firewall and `bootstrap.sh` on a real VM).

Fix round 1 (review, 2026-10-03), checked the same way: Caddy refuses `TENANT_HOSTS=a,b` ("Site addresses cannot contain
a comma", reproduced), and validates the normalised `a, b`; the restore of a dump, followed by the earlier
`bootstrap.sql`, left `erp_key_ring` without CONNECT on `platform` (reproduced), the new integrity check fails on it
("these roles cannot connect after the restore: erp_key_ring"), and the current `bootstrap.sql` restores the grant;
Keycloak, Elasticsearch and the collector are healthy with every capability dropped (effective set 0); the arm64 web,
worker and migrator images built with a docker-container builder have no fixable HIGH or CRITICAL finding.

On-demand TLS (2026-10-03), checked locally with the pinned Caddy 2.10 image: `caddy validate` passes on the pilot
Caddyfile; with Caddy's local issuer in place of Let's Encrypt and a stub ask server, a handshake for a host the stub
allows obtained its certificate and was served, a refused host and a host outside the base domain failed the handshake,
and the platform and auth certificates were still obtained at start. A `*.<base domain>` site was tried first and
rejected: with `on_demand` it also covers the platform and auth hosts, whose certificates were then no longer obtained at
start. The endpoint itself is covered by `TlsAskEndpointTests` (integration). Not checked: Let's Encrypt on a real host.

## 10. Rehearsal 2026-10-03 (local)

Chosen by the user on 2026-10-03: the real scripts of `infra/pilot/` run end to end on the laptop (Docker Desktop, amd64
images `ec0d67942327` built natively), with the stand-ins of `infra/pilot/dry-run/` (its README says how to repeat it):
a runner container as the VM (Ubuntu 24.04, root), hosts under `pilot.localhost`, Caddy's internal CA instead of
Let's Encrypt with the on-demand ask unchanged, Mailpit requiring login and STARTTLS instead of Email Delivery, and a
MinIO answering as `dryrun.compat.objectstorage.me-jeddah-1.oraclecloud.com` over HTTPS instead of Object Storage.
Nothing went to any cloud.

| Step | Result |
|---|---|
| 1. `generate-secrets.sh` | 26 secrets filled, `key-ring.pfx` owned by 1654 with mode 400; a second run filled none. `dry-run/prepare.sh` typed the values step 5 leaves to a human |
| 2. `deploy.sh` | First run stopped at the web host: "Address already in use" (bug 1). Fixed, then from empty volumes with the images present: 1 min 40 s, every check passed (`/health`, `/alive`, ask listener 400, worker heartbeat, platform over HTTPS, Keycloak issuer); a second run 18 s, no container recreated |
| 3. Keycloak day one | Both realms imported. `dry-run/keycloak-day-one.sh` did step 7.1 to 7.3 through the Admin API: `waslabid-ops` with its five roles and its secret in `.env`, a named admin, the bootstrap admin deleted (it no longer signs in). Still human: the named admin's OTP and the platform admin's first sign-in |
| 4. `provision-tenant.sh` | `acme` (ar-SA) and `beta` (en-US) in about 1.3 s each, as `waslabid-ops`; a re-run found everything present and sent the setup email again (account setup pending), as designed |
| 5. On-demand TLS | `acme`, `beta`, the platform and auth hosts: a certificate that verifies against the CA. `zzz.pilot.localhost` (no tenant), `a.b.pilot.localhost` and `evil.example.com`: handshake refused. Ask endpoint 200 for the two tenants, 404 for the others, the platform and auth hosts and an IP; `/internal/tls-ask` through Caddy and on port 8080: 404 |
| 6. Smoke | Tenant host 302 to Keycloak with a pushed authorization request (the web host's back channel through Caddy works); HSTS, `X-Frame-Options: DENY`, `nosniff`, CSP, Referrer and Permissions policies; Keycloak's login page `lang="ar" dir="rtl"`; `/alive` and `/health` 200 through Caddy; `/platform` 302 to `waslabid-platform` (login page in English); `/admin/` and `/realms/master` 404 from an address outside `ADMIN_ALLOW_CIDR`; HTTP 308 to HTTPS; every health-board component Healthy, SMTP included; Kibana on demand healthy and its dashboard imported. `tests/e2e` not run: no base-URL setting (section 9) |
| 7. Email | In Mailpit, over STARTTLS with login: Keycloak's setup emails (Arabic subject for `acme`) and the worker's F-60 alert "SMTP has recovered"; Mailpit refused mail without login (530). Not exercised: the web host's own sending (a staff invitation needs a tenant admin signed in with TOTP; it uses the worker's SMTP code, `Platform.Shared`) |
| 8. Backup and restore | `backup.sh` 16 s (two objects; `staging/` left out; names and contents encrypted in the bucket), again 16 s with both objects skipped; `restore.sh --list` two complete; `--verify` passed in 13 s (2 tenants, 2 members, 1 key-ring key, 26 forced-RLS tables, every role connects, 3 realms, 6 Keycloak users, 2 of 2 objects stored). Disaster recovery: every volume deleted but the bucket's, state and local copies removed, `restore.sh --full` 22 s and `deploy.sh` 1 min 42 s, 124 s in all with images present; tenants, Keycloak users, `waslabid-ops` and objects back, tenant certificates obtained again on demand |
| 9. `watchdog.sh` | Silent while healthy; with the web host stopped one "web is down" email (a second run sent none), after the start one "web has recovered" |
| 10. Memory | 3.9 GB idle for the pilot services against 9.56 GB of limits (below); Kibana 1.0 GB while open |

Idle memory (`docker stats`, amd64, no traffic; the stand-ins are left out):

| Service | Used / limit | Service | Used / limit |
|---|---|---|---|
| Elasticsearch | 1.42 GB / 1.75 GB (81%) | Worker | 152 MB / 512 MB |
| ClamAV | 990 MB / 2 GB | OTel Collector | 150 MB / 320 MB |
| Keycloak | 620 MB / 1.5 GB | Web host | 88 MB / 1 GB |
| MinIO | 249 MB / 640 MB | Caddy, Redis | 24 MB, 13 MB |
| PostgreSQL | 180 MB / 1.5 GB | **Total** | **3.9 GB** |

Found and fixed:

1. `docker-compose.yml`: a container without a pinned address on `edge` took the first free one. Keycloak starts before
   the web host and Caddy, so on the VM it would hold 172.30.10.2 and Caddy would never start; reproduced here as
   "failed to set up container networking: Address already in use". Unpinned containers now get addresses from
   `EDGE_IP_RANGE` (172.30.10.128/25), outside both pinned ones.
2. Keycloak and STARTTLS (section 9): Keycloak sends its login without STARTTLS when the server does not offer it.
   Recorded with its options; not changed.
3. For the rehearsal, inert on the VM: `lib.sh` layers `PILOT_COMPOSE_OVERRIDE` when set, and `deploy.sh` reaches Caddy
   on `PILOT_HTTPS_PORT` (443 by default; `--connect-to` instead of `--resolve`, the same request on the VM). CI now also
   shellchecks `dry-run/*.sh` and validates the override. The stale mention of the mail relay in step 8 is gone.

Seen, not a defect of the pilot files: MailKit checks certificate revocation, so the hosts must reach the CRL or OCSP
service of Email Delivery's certificate authority (W-43's revocation allowance); the beta tenant's login page is Arabic
for a client without `Accept-Language`, because the realm's default locale is Arabic and the web host sends no
`ui_locales` (as in development).

Different from the VM, so still to prove there: arm64 images on the Arm VM; `bootstrap.sh`, systemd timers, the firewall
and the security list; DNS and Let's Encrypt (HTTP-01, rate limits); Email Delivery (login, SPF, DKIM, sending limits);
Object Storage with its Customer Secret Key, the 35-day retention rule and its lock; a browser sign-in with TOTP
(ports 80 and 443 belong to Windows here); first-start times (images were already pulled, and ClamAV was healthy in
27 s); memory under real traffic; the one-hour rebuild including the VM and image shipping.
