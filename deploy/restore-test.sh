#!/usr/bin/env bash
#
# OrderDeck — Backup Restore Drill
#
# Picks the most recent encrypted backup blob under /opt/orderdeck/backups,
# pipes it through the running license-server container's RestoreVerify
# CLI, and reports whether decrypt + zip integrity + SQLite integrity all
# pass. NO production data is modified — the drill writes only into a
# temporary directory inside the container that is wiped on completion.
#
# Run manually any time you want to convince yourself the disaster recovery
# story actually works:
#
#     bash /opt/orderdeck/restore-test.sh                 # pick newest blob, default key version 0
#     bash /opt/orderdeck/restore-test.sh /path/to.bin    # specific blob
#     bash /opt/orderdeck/restore-test.sh /path/to.bin 1  # specific blob + key version
#
# Recommended cadence: weekly. A failing drill means a customer cannot
# actually recover from a real disaster; treat it as a P1.

set -euo pipefail

readonly BACKUPS_ROOT="/opt/orderdeck/backups"
readonly COMPOSE_DIR="/opt/orderdeck"
readonly SERVICE="license-server"

color() { printf "\033[%sm%s\033[0m\n" "$1" "$2"; }
green() { color "32" "$1"; }
red()   { color "31" "$1"; }
bold()  { color "1"  "$1"; }

bold "=== OrderDeck Backup Restore Drill ==="

# 1. Resolve the blob to verify.
if [[ "${1:-}" != "" && -f "${1}" ]]; then
    BLOB="${1}"
    echo "Using explicit blob: ${BLOB}"
elif [[ "${1:-}" != "" ]]; then
    red "Specified blob not found: ${1}"
    exit 2
else
    if [[ ! -d "${BACKUPS_ROOT}" ]]; then
        red "Backups root missing: ${BACKUPS_ROOT}"
        exit 2
    fi
    BLOB=$(find "${BACKUPS_ROOT}" -name '*.bin' -type f -printf '%T@ %p\n' \
            2>/dev/null | sort -nr | head -n 1 | cut -d' ' -f2-)
    if [[ -z "${BLOB}" ]]; then
        red "No backup blobs found under ${BACKUPS_ROOT}"
        echo "Either no customer has uploaded a backup yet, or the storage path is wrong."
        exit 0
    fi
    echo "Latest blob: ${BLOB}"
fi

KEY_VERSION="${2:-0}"
echo "Key version: ${KEY_VERSION}"

# 2. Map host blob path → in-container path. The container mounts
#    /opt/orderdeck/backups → /app/Backups (see deploy/docker-compose.yml).
CONTAINER_BLOB="${BLOB/#$BACKUPS_ROOT//app/Backups}"
echo "Container path: ${CONTAINER_BLOB}"
echo

# 3. Run the drill. The license-server container already has every
#    Backup__* env var loaded from .env, so the same key ring that
#    encrypted the blob is available for decrypt.
cd "${COMPOSE_DIR}"
if docker compose exec -T "${SERVICE}" \
        dotnet OrderDeck.LicenseServer.dll restore-verify \
        "${CONTAINER_BLOB}" "${KEY_VERSION}" \
        --workdir=/tmp/orderdeck-restore-test
then
    echo
    green "RESTORE DRILL PASSED — backup ${BLOB} decrypts and parses correctly."
    exit 0
else
    rc=$?
    echo
    red "RESTORE DRILL FAILED (exit ${rc}) — investigate immediately, recovery is not assured."
    echo "Common causes:"
    echo "  - Backup__ActiveKeyVersion mismatch: blob was encrypted with a key that's not in the current ring"
    echo "  - .env was rotated but old blobs still live under /opt/orderdeck/backups"
    echo "  - Disk corruption on the blob itself"
    exit "${rc}"
fi
