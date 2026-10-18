#!/usr/bin/env bash
#
# Installs nightly grandfather-father-son backups for the DataHub stack: Postgres (logical dump)
# and the MinIO data volume, pushed to Google Drive via restic's rclone backend, with a Telegram
# message on success and failure. Run once, as root, on the provisioned server:
#
#   scp server-provision/backup.sh root@<host>:
#   ssh -t root@<host> 'bash backup.sh'
#
# Botched a prompt (wrong rclone token etc.)? Wipe and redo everything:
#
#   ssh -t root@<host> 'bash backup.sh --reset'
#
# Prerequisites (one-time, before running):
#   1. Telegram: talk to @BotFather -> /newbot -> keep the token. Send your new bot any message,
#      then read your chat id from https://api.telegram.org/bot<TOKEN>/getUpdates (.message.chat.id).
#   2. Google Drive: on a machine WITH a browser run `rclone authorize "drive"`, copy the token
#      block it prints. This script runs `rclone config` for you and asks for that token.
#
# Safe to re-run: every step checks its own state first. Secrets live in /etc/datahub-backup.env
# (root only) and are never overwritten.
#
# Retention (GFS): 7 daily, 4 weekly, 12 monthly snapshots. Restore test is on YOU: run
# `restic restore latest --target /tmp/restore-drill` once after install, and quarterly.

set -euo pipefail

BASE="${BASE:-/home/deploy/datahub}"      # holds .env of the running stack
ONCALENDAR="${ONCALENDAR:-*-*-* 03:30}"   # off-peak: restic needs ~200-300MB RAM while running
RCLONE_REMOTE="${RCLONE_REMOTE:-gdrive}"
DRIVE_FOLDER="${DRIVE_FOLDER:-datahub-backups}"

if [ -t 1 ]; then B=$'\033[1m'; GREEN=$'\033[32m'; YELLOW=$'\033[33m'; RED=$'\033[31m'; R=$'\033[0m'
else B=; GREEN=; YELLOW=; RED=; R=; fi
step() { printf '\n%s==> %s%s\n' "$B" "$*" "$R"; }
ok()   { printf '    %s✓%s %s\n' "$GREEN" "$R" "$*"; }
skip() { printf '    %s·%s %s\n' "$YELLOW" "$R" "$*"; }
die()  { printf '\n%serror:%s %s\n' "$RED" "$R" "$*" >&2; exit 1; }
ask() { local var="$1" prompt="$2" reply=""; [ -n "${!var:-}" ] && return 0
    [ -t 0 ] || die "$var is not set and there is no terminal to ask on."
    while [ -z "$reply" ]; do read -r -p "    $prompt: " reply; done; printf -v "$var" '%s' "$reply"; }

[ "$(id -u)" = 0 ] || die "run as root"
[ -f "$BASE/.env" ] || die "$BASE/.env not found - provision the server first"

# --reset: wipe everything this script created (rclone remote, secrets, script, units), then
# reinstall from scratch. Does NOT touch snapshots already on Google Drive.
if [ "${1:-}" = "--reset" ]; then
    step "Reset"
    if [ -f /etc/datahub-backup.env ]; then
        printf '    %s!%s this deletes /etc/datahub-backup.env including RESTIC_PASSWORD.\n' "$YELLOW" "$R"
        printf '      If a restic repository was already initialized, its snapshots become UNREADABLE\n'
        printf '      without that password. Save it first if in doubt.\n'
        read -r -p "    wipe and reinstall? [y/N]: " reply
        [[ "$reply" =~ ^[Yy]$ ]] || die "aborted"
    fi
    systemctl disable --now datahub-backup.timer >/dev/null 2>&1 || true
    rm -f /etc/systemd/system/datahub-backup.service /etc/systemd/system/datahub-backup.timer \
          /usr/local/bin/datahub-backup /etc/datahub-backup.env
    systemctl daemon-reload
    rclone config delete "$RCLONE_REMOTE" >/dev/null 2>&1 || true
    ok "wiped - reinstalling"
fi

step "Packages (restic, rclone, zstd)"
if command -v restic >/dev/null && command -v rclone >/dev/null && command -v zstd >/dev/null; then
    skip "already installed"
else
    apt-get update -qq
    DEBIAN_FRONTEND=noninteractive apt-get install -y -qq restic rclone zstd >/dev/null
    ok "installed"
fi

step "rclone Google Drive remote '$RCLONE_REMOTE'"
if rclone listremotes 2>/dev/null | grep -qx "$RCLONE_REMOTE:"; then
    skip "remote exists"
else
    ask RCLONE_TOKEN 'paste the token JSON from `rclone authorize "drive"` (one line)'
    rclone config create "$RCLONE_REMOTE" drive scope drive.file token "$RCLONE_TOKEN" \
        config_refresh_token false >/dev/null
    ok "remote created (scope drive.file: the bot sees only files it created)"
fi
rclone lsd "$RCLONE_REMOTE:" >/dev/null || die "rclone cannot reach Google Drive - token invalid?"

step "Secrets /etc/datahub-backup.env"
if [ -f /etc/datahub-backup.env ]; then
    skip "exists, not touching it"
else
    ask TELEGRAM_BOT_TOKEN "Telegram bot token (from @BotFather)"
    ask TELEGRAM_CHAT_ID "Telegram chat id"
    RESTIC_PASSWORD="$(openssl rand -hex 32)"
    umask 077
    cat > /etc/datahub-backup.env <<ENV
RESTIC_REPOSITORY=rclone:$RCLONE_REMOTE:$DRIVE_FOLDER
RESTIC_PASSWORD=$RESTIC_PASSWORD
TELEGRAM_BOT_TOKEN=$TELEGRAM_BOT_TOKEN
TELEGRAM_CHAT_ID=$TELEGRAM_CHAT_ID
ENV
    ok "written (mode 600)"
    printf '\n    %s!%s RESTIC_PASSWORD (store it OUTSIDE this server - without it every backup is garbage):\n      %s\n\n' \
        "$YELLOW" "$R" "$RESTIC_PASSWORD"
fi

step "Backup script /usr/local/bin/datahub-backup"
cat > /usr/local/bin/datahub-backup <<'SCRIPT'
#!/usr/bin/env bash
set -euo pipefail
set -a; . /etc/datahub-backup.env; set +a
BASE=/home/deploy/datahub
STAGE=/var/backups/datahub
MINIO_DATA=/var/lib/docker/volumes/datahub_miniodata/_data

notify() { # notify <text>; never fails the backup over a Telegram hiccup
    curl -fsS -m 20 "https://api.telegram.org/bot${TELEGRAM_BOT_TOKEN}/sendMessage" \
        -d chat_id="${TELEGRAM_CHAT_ID}" -d text="$1" >/dev/null 2>&1 || true
}
trap 'notify "❌ DataHub backup FAILED on $(hostname) at line $LINENO - journalctl -u datahub-backup"' ERR

mkdir -p "$STAGE"; chmod 700 "$STAGE"
start=$(date +%s)
notify "🚀 DataHub backup started on $(hostname)"

# 1. Postgres: logical dump of all databases + roles (app, auth, keycloak). $$POSTGRES_USER is
#    resolved inside the container, so no credentials live in this script.
docker exec datahub-postgres-1 sh -c 'pg_dumpall -U "$POSTGRES_USER"' | zstd -q -o "$STAGE/pg_dumpall.sql.zst" -f

# 2. Stack .env: needed for disaster recovery (compose secrets). Repo is encrypted, so this is safe.
install -m 600 "$BASE/.env" "$STAGE/stack.env"

# 3. Snapshot to Google Drive. MinIO volume is read live: single-node MinIO writes objects
#    atomically, so file-level copy is consistent. restic dedups, only changed objects upload.
restic backup --quiet "$STAGE" "$MINIO_DATA"

# 4. GFS retention: 7 daily, 4 weekly, 12 monthly.
restic forget --quiet --prune --keep-daily 7 --keep-weekly 4 --keep-monthly 12

stats=$(restic stats --mode raw-data latest 2>/dev/null | grep 'Total Size' || true)
notify "✅ DataHub backup OK on $(hostname) in $(( $(date +%s) - start ))s. ${stats}"
SCRIPT
chmod 700 /usr/local/bin/datahub-backup
ok "written"

step "restic repository init"
set -a; . /etc/datahub-backup.env; set +a
if restic cat config >/dev/null 2>&1; then
    skip "repository exists"
else
    restic init >/dev/null
    ok "initialized at $RESTIC_REPOSITORY"
fi

step "systemd timer (daily $ONCALENDAR)"
cat > /etc/systemd/system/datahub-backup.service <<UNIT
[Unit]
Description=DataHub GFS backup to Google Drive (restic)
# Managed by server-provision/backup.sh

[Service]
Type=oneshot
ExecStart=/usr/local/bin/datahub-backup
# Keep restic off the app stack's throat on an overcommitted host.
Nice=15
IOSchedulingClass=idle
MemoryMax=400M
UNIT
cat > /etc/systemd/system/datahub-backup.timer <<UNIT
[Unit]
Description=Nightly DataHub backup
# Managed by server-provision/backup.sh

[Timer]
OnCalendar=$ONCALENDAR
Persistent=true
RandomizedDelaySec=10m

[Install]
WantedBy=timers.target
UNIT
systemctl daemon-reload
systemctl enable --now datahub-backup.timer >/dev/null 2>&1
ok "enabled: $(systemctl list-timers datahub-backup.timer --no-pager | sed -n 2p)"

step "Done"
ok "test it now:        systemctl start datahub-backup.service && journalctl -u datahub-backup -f"
ok "list snapshots:     . /etc/datahub-backup.env && restic snapshots"
ok "restore drill:      restic restore latest --target /tmp/restore-drill"
