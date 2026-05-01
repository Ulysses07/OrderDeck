#!/usr/bin/env bash
set -euo pipefail

# Generate (or rotate) the AES-256-GCM master key for OrderDeck cloud backups.
# Writes BACKUP_MASTER_KEY=<64 hex chars> to /opt/orderdeck/.env (mode 600).
# Restarts the license-server container so the new key is loaded.

ENV_FILE=/opt/orderdeck/.env
COMPOSE_DIR=/opt/orderdeck

if [ ! -f "$ENV_FILE" ]; then
  echo "ERROR: $ENV_FILE not found" >&2
  exit 1
fi

# Detect existing key
if grep -q '^BACKUP_MASTER_KEY=' "$ENV_FILE"; then
    EXISTING=$(grep '^BACKUP_MASTER_KEY=' "$ENV_FILE" | cut -d= -f2-)
    if [ -n "$EXISTING" ] && [ "$EXISTING" != "REPLACE-WITH-64-HEX-CHARS" ]; then
        read -rp "An existing BACKUP_MASTER_KEY is set. Rotating will make ALL existing encrypted backups unreadable. Continue? [yes/NO]: " confirm
        if [ "$confirm" != "yes" ]; then
            echo "Aborted."
            exit 0
        fi
    fi
fi

# Generate 32 bytes -> 64 hex chars
NEW_KEY=$(openssl rand -hex 32)
if [ ${#NEW_KEY} -ne 64 ]; then
    echo "ERROR: openssl produced unexpected key length: ${#NEW_KEY}" >&2
    exit 1
fi

# Update .env (replace or append)
if grep -q '^BACKUP_MASTER_KEY=' "$ENV_FILE"; then
    sed -i "s|^BACKUP_MASTER_KEY=.*|BACKUP_MASTER_KEY=${NEW_KEY}|" "$ENV_FILE"
else
    echo "BACKUP_MASTER_KEY=${NEW_KEY}" >> "$ENV_FILE"
fi

chmod 600 "$ENV_FILE"
echo "BACKUP_MASTER_KEY written (64 hex chars). Length: ${#NEW_KEY}"

echo ""
echo "Restarting license-server..."
cd "$COMPOSE_DIR"
docker compose up -d --force-recreate license-server 2>&1 | tail -5

echo ""
echo "Done. Verify with:"
echo "  docker exec orderdeck-license env | grep Backup__MasterKeyHex"
