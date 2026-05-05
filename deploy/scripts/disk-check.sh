#!/usr/bin/env bash
# Hourly disk-fill alert via msmtp. See HA-PLAYBOOK.md gap G3.
# Without this, SQL Express silently rejects inserts when disk fills,
# corrupting backups (the restore drill would still pass on truncated
# blobs). UptimeRobot only catches HTTP-up, not disk pressure.
#
# Cron: 17 * * * * /opt/orderdeck/scripts/disk-check.sh >>/var/log/orderdeck-disk-check.log 2>&1
set -euo pipefail

THRESHOLD=85          # percent used → alert
RECOVERY_BUFFER=5     # only alert recovery once we drop ≥5% below threshold
STATE_FILE="/var/lib/orderdeck-disk-alert/active"
mkdir -p "$(dirname "$STATE_FILE")"

ALERT_TO=$(grep ^Admin__AlertEmail /opt/orderdeck/.env | cut -d= -f2 | tr -d "\"")
SMTP_FROM=$(grep ^SMTP_FROM /opt/orderdeck/.env | cut -d= -f2 | tr -d "\"")

# Read root filesystem usage. -P forces POSIX columns even with long device names.
DF_LINE=$(df -P / | tail -1)
USED_PCT=$(echo "$DF_LINE" | awk '{print $5}' | tr -d %)
SIZE=$(echo "$DF_LINE" | awk '{print $2}')
USED=$(echo "$DF_LINE" | awk '{print $3}')
AVAIL=$(echo "$DF_LINE" | awk '{print $4}')

LOG_PREFIX="[$(date -u +%Y-%m-%dT%H:%M:%SZ)]"
echo "$LOG_PREFIX disk-check root used=${USED_PCT}% avail=${AVAIL}KB"

send_mail () {
  local subject="$1" body="$2"
  printf "Subject: %s\nFrom: %s\nTo: %s\n\n%s\n" \
    "$subject" "$SMTP_FROM" "$ALERT_TO" "$body" \
  | msmtp -t
}

if (( USED_PCT >= THRESHOLD )); then
  if [[ ! -f "$STATE_FILE" ]]; then
    BODY=$(cat <<EOF
OrderDeck VPS root filesystem at ${USED_PCT}% (threshold ${THRESHOLD}%).

Used:      ${USED} KB
Available: ${AVAIL} KB
Total:     ${SIZE} KB

Common causes:
  - SQL backups not pruning. Check /opt/orderdeck/sql-data/backup/.
  - Customer backup blobs accumulating. Check /opt/orderdeck/backups/.
  - Docker layers. \`docker system prune --filter "until=72h"\`.
  - Hangfire job logs. Check the Hangfire dashboard for stuck retries.

Action: SSH in and free space; SQL Express silently rejects inserts
when the partition fills, which would corrupt backups taken after the
trigger.
EOF
)
    send_mail "[OrderDeck] Disk usage ${USED_PCT}% on prod VPS" "$BODY"
    touch "$STATE_FILE"
    echo "$LOG_PREFIX ALERT sent (first crossing of ${THRESHOLD}%)"
  else
    echo "$LOG_PREFIX still over threshold, alert already active — skipping mail"
  fi
elif [[ -f "$STATE_FILE" ]] && (( USED_PCT <= THRESHOLD - RECOVERY_BUFFER )); then
  send_mail "[OrderDeck] Disk usage recovered to ${USED_PCT}% on prod VPS" \
    "Root filesystem dropped back below the alert threshold. Available: ${AVAIL} KB."
  rm -f "$STATE_FILE"
  echo "$LOG_PREFIX RECOVERED (cleared state)"
fi
