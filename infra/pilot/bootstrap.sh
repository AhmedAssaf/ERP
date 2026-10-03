#!/usr/bin/env bash
# W-19: prepares a fresh OCI VM.Standard.A1.Flex (aarch64) for the pilot. Run once as root from the repository checkout;
# running it again changes nothing that is already in place.
#
#   sudo SSH_ALLOW_CIDR=203.0.113.7/32 infra/pilot/bootstrap.sh
#
# Supported: Ubuntu 24.04 (recommended; Canonical's OCI image) and Oracle Linux 9. It does, in this order:
#   1. packages: jq, curl, python3, chrony, git and the firewall persistence tools
#   2. firewall on the VM, BEFORE Docker is installed so the saved rules never contain Docker's: TCP 80 and 443 from
#      anywhere, SSH only from SSH_ALLOW_CIDR (refused if your current SSH session is outside it). The OCI security list
#      is the first wall (docs/19 step 3); this is the second.
#   3. Docker Engine with the compose and buildx plugins, from Docker's own repository
#   4. Docker daemon: log rotation, live-restore (containers keep running while the daemon upgrades), no userland proxy
#   5. kernel: vm.max_map_count=262144 (Elasticsearch), vm.swappiness=10, and a 2 GB swap file as a cushion
#   6. time: chrony against Oracle's NTP at 169.254.169.254 (the job locks and token lifetimes need a sane clock)
#   7. automatic security updates (no automatic reboot; the watchdog reminds after 7 days)
#   8. SSH: keys only, no root login, local port forwards only (the Kibana tunnel, O-18)
#   9. state and backup folders, and the systemd units for the backup (nightly) and the watchdog (every 5 minutes)
set -euo pipefail

PILOT_DIR="$(cd "$(dirname "$0")" && pwd)"
log() { printf '== %s\n' "$*"; }
die() { printf 'ERROR: %s\n' "$*" >&2; exit 1; }

[ "$(id -u)" -eq 0 ] || die "run as root (sudo)"
: "${SSH_ALLOW_CIDR:?set SSH_ALLOW_CIDR to the address you administer from, e.g. 203.0.113.7/32}"
[ "$(uname -m)" = "aarch64" ] || echo "WARNING: this is $(uname -m), not aarch64; the pilot images are built for arm64"
# shellcheck source=/dev/null
. /etc/os-release
case "$ID" in
  ubuntu) FAMILY=debian ;;
  ol|rhel|rocky|almalinux) FAMILY=rhel ;;
  *) die "unsupported distribution $ID (Ubuntu 24.04 or Oracle Linux 9)" ;;
esac

# Refuse an SSH allow-list that would lock out the session running this script.
if [ -n "${SSH_CLIENT:-}" ]; then
  client_ip="${SSH_CLIENT%% *}"
  python3 - "$client_ip" "$SSH_ALLOW_CIDR" <<'PY' || die "your SSH session comes from ${SSH_CLIENT%% *}, outside SSH_ALLOW_CIDR=$SSH_ALLOW_CIDR"
import ipaddress, sys
sys.exit(0 if ipaddress.ip_address(sys.argv[1]) in ipaddress.ip_network(sys.argv[2], strict=False) else 1)
PY
fi

log "1. base packages"
if [ "$FAMILY" = debian ]; then
  export DEBIAN_FRONTEND=noninteractive
  apt-get update -q
  apt-get install -y -q ca-certificates curl gnupg jq python3 chrony unattended-upgrades iptables-persistent git
else
  dnf install -y -q ca-certificates curl jq python3 chrony dnf-automatic git dnf-plugins-core firewalld
fi

log "2. firewall: 80 and 443 open, SSH from $SSH_ALLOW_CIDR only"
if [ "$FAMILY" = debian ]; then
  # Canonical's OCI image ends INPUT with a REJECT rule; new rules go just above it. Without one, a REJECT is appended.
  ipt_accept() { # ipt_accept <rule...>
    iptables -C INPUT "$@" -j ACCEPT 2>/dev/null && return
    local reject
    reject="$(iptables -L INPUT --line-numbers -n | awk '$2 == "REJECT" {print $1; exit}')"
    if [ -n "$reject" ]; then iptables -I INPUT "$reject" "$@" -j ACCEPT; else iptables -A INPUT "$@" -j ACCEPT; fi
  }
  ipt_accept -m state --state RELATED,ESTABLISHED
  ipt_accept -i lo
  ipt_accept -p icmp
  ipt_accept -p tcp -s "$SSH_ALLOW_CIDR" --dport 22 -m state --state NEW
  ipt_accept -p tcp --dport 80 -m state --state NEW
  ipt_accept -p tcp --dport 443 -m state --state NEW
  # Remove an SSH rule open to everyone (the image's default), now that the allow-listed one is in place.
  while iptables -C INPUT -p tcp -m state --state NEW -m tcp --dport 22 -j ACCEPT 2>/dev/null; do
    iptables -D INPUT -p tcp -m state --state NEW -m tcp --dport 22 -j ACCEPT
  done
  iptables -L INPUT -n | grep -q REJECT || iptables -A INPUT -j REJECT --reject-with icmp-host-prohibited
  if command -v docker >/dev/null && iptables -S | grep -q DOCKER; then
    echo "Docker is already installed: not saving the rules (they would include Docker's). Save by hand after review."
  else
    netfilter-persistent save
  fi
else
  systemctl enable --now firewalld
  firewall-cmd --permanent --add-service=http --add-service=https
  firewall-cmd --permanent --add-rich-rule="rule family=ipv4 source address=$SSH_ALLOW_CIDR service name=ssh accept"
  firewall-cmd --permanent --remove-service=ssh || true
  firewall-cmd --reload
fi

log "3. Docker Engine from Docker's repository"
if ! command -v docker >/dev/null; then
  if [ "$FAMILY" = debian ]; then
    install -m 0755 -d /etc/apt/keyrings
    curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc
    chmod a+r /etc/apt/keyrings/docker.asc
    echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/ubuntu ${VERSION_CODENAME} stable" \
      > /etc/apt/sources.list.d/docker.list
    apt-get update -q
    apt-get install -y -q docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
  else
    dnf config-manager --add-repo https://download.docker.com/linux/rhel/docker-ce.repo
    dnf install -y -q docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
  fi
fi

log "4. Docker daemon settings"
mkdir -p /etc/docker
desired='{
  "log-driver": "json-file",
  "log-opts": { "max-size": "10m", "max-file": "3" },
  "live-restore": true,
  "userland-proxy": false
}'
if [ ! -f /etc/docker/daemon.json ] || [ "$(jq -S . /etc/docker/daemon.json)" != "$(jq -S . <<<"$desired")" ]; then
  printf '%s\n' "$desired" > /etc/docker/daemon.json
  systemctl restart docker
fi
systemctl enable --now docker

log "5. kernel settings and swap"
cat > /etc/sysctl.d/99-waslabid.conf <<'EOF'
# W-19: Elasticsearch needs at least 262144 memory map areas; swap only under pressure.
vm.max_map_count = 262144
vm.swappiness = 10
EOF
sysctl -q --system
if ! swapon --show=NAME --noheadings | grep -q .; then
  fallocate -l 2G /swapfile
  chmod 600 /swapfile
  mkswap -q /swapfile
  swapon /swapfile
  grep -q '^/swapfile ' /etc/fstab || echo '/swapfile none swap sw 0 0' >> /etc/fstab
fi

log "6. time sync (chrony, Oracle NTP 169.254.169.254)"
chrony_conf=/etc/chrony/chrony.conf; [ -f "$chrony_conf" ] || chrony_conf=/etc/chrony.conf
grep -q '^server 169.254.169.254' "$chrony_conf" || echo 'server 169.254.169.254 iburst prefer' >> "$chrony_conf"
systemctl enable --now chrony 2>/dev/null || systemctl enable --now chronyd
systemctl restart chrony 2>/dev/null || systemctl restart chronyd
sleep 3
chronyc -n tracking | grep -E 'Reference ID|System time|Leap status' || true

log "7. automatic security updates"
if [ "$FAMILY" = debian ]; then
  cat > /etc/apt/apt.conf.d/52waslabid-unattended <<'EOF'
// W-19: security updates every day; Docker and the kernel included. No automatic reboot: a restart stops the pilot
// for a minute or two, so it is done by hand in a quiet hour (docs/19 section 8); the watchdog reminds after 7 days.
Unattended-Upgrade::Allowed-Origins { "${distro_id}:${distro_codename}-security"; "${distro_id}ESMApps:${distro_codename}-apps-security"; };
Unattended-Upgrade::Automatic-Reboot "false";
EOF
  printf 'APT::Periodic::Update-Package-Lists "1";\nAPT::Periodic::Unattended-Upgrade "1";\n' > /etc/apt/apt.conf.d/20auto-upgrades
  systemctl enable --now unattended-upgrades
else
  sed -i -e 's/^upgrade_type.*/upgrade_type = security/' -e 's/^apply_updates.*/apply_updates = yes/' /etc/dnf/automatic.conf
  systemctl enable --now dnf-automatic.timer
fi

log "8. SSH: keys only, no root login"
mkdir -p /etc/ssh/sshd_config.d
cat > /etc/ssh/sshd_config.d/99-waslabid.conf <<'EOF'
PasswordAuthentication no
KbdInteractiveAuthentication no
PermitRootLogin no
X11Forwarding no
# Kibana (O-18) and the Keycloak admin API are reached through local forwards only.
AllowTcpForwarding local
EOF
sshd -t && (systemctl reload ssh 2>/dev/null || systemctl reload sshd)

log "9. folders and systemd units"
install -d -m 700 /var/lib/waslabid /var/backups/waslabid
install -m 644 "$PILOT_DIR/systemd/waslabid-backup.service" "$PILOT_DIR/systemd/waslabid-backup.timer" \
  "$PILOT_DIR/systemd/waslabid-watchdog.service" "$PILOT_DIR/systemd/waslabid-watchdog.timer" /etc/systemd/system/
sed -i "s#@PILOT_DIR@#$PILOT_DIR#g" /etc/systemd/system/waslabid-backup.service /etc/systemd/system/waslabid-watchdog.service
systemctl daemon-reload
# Enabled now, effective once .env exists and the first deploy has run (both scripts stop early before that).
systemctl enable --now waslabid-backup.timer waslabid-watchdog.timer

log "done. Checks:"
docker version --format 'docker {{.Server.Version}} ({{.Server.Arch}})'
docker compose version
sysctl vm.max_map_count
swapon --show
echo "Next: docs/19 step 5 (secrets) and step 6 (first deploy)."
