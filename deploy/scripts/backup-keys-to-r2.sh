#!/usr/bin/env bash
# Nightly DataProtection keys → R2. See HA-PLAYBOOK.md gap G1.
# Without these keys off-host: customer JWTs + password reset tokens
# unrecoverable if VPS dies.
# Cron: 30 3 * * * /opt/orderdeck/scripts/backup-keys-to-r2.sh >>/var/log/orderdeck-keys-backup.log 2>&1
set -euo pipefail

LOG_PREFIX="[$(date -u +%Y-%m-%dT%H:%M:%SZ)]"
echo "$LOG_PREFIX keys-sync start"

# `aws s3 sync` is incremental + deletes removed files on remote with --delete.
# Keys are tiny (KB-range), churn rare. Default sync sufficient.
aws s3 sync /opt/orderdeck/keys/ s3://orderdeck-prod-backups/keys/ \
  --profile r2 --only-show-errors --delete

COUNT=$(ls /opt/orderdeck/keys/ | wc -l)
echo "$LOG_PREFIX keys-sync done count=${COUNT}"
