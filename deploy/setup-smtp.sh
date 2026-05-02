#!/usr/bin/env bash
set -euo pipefail

# Configure SMTP credentials in /opt/orderdeck/.env (Brevo by default).
# Reads password from stdin without echo. Restarts license-server.

ENV_FILE=/opt/orderdeck/.env
COMPOSE_DIR=/opt/orderdeck

if [ ! -f "$ENV_FILE" ]; then
  echo "ERROR: $ENV_FILE not found" >&2
  exit 1
fi

# Defaults — override by typing different values when prompted.
DEFAULT_HOST="smtp-relay.brevo.com"
DEFAULT_PORT="587"
DEFAULT_FROM="noreply@orderdeckapp.com"

read -rp "SMTP host [${DEFAULT_HOST}]: " SMTP_HOST
SMTP_HOST=${SMTP_HOST:-$DEFAULT_HOST}

read -rp "SMTP port [${DEFAULT_PORT}]: " SMTP_PORT
SMTP_PORT=${SMTP_PORT:-$DEFAULT_PORT}

read -rp "SMTP username (Brevo login, e.g. a9ca4e001@smtp-brevo.com): " SMTP_USERNAME
if [ -z "$SMTP_USERNAME" ]; then
  echo "ERROR: SMTP username required" >&2
  exit 1
fi

read -rsp "SMTP password (key, hidden): " SMTP_PASSWORD
echo

if [ -z "$SMTP_PASSWORD" ]; then
  echo "ERROR: SMTP password required" >&2
  exit 1
fi

read -rp "FROM address [${DEFAULT_FROM}]: " SMTP_FROM
SMTP_FROM=${SMTP_FROM:-$DEFAULT_FROM}

# Escape $ as $$ for docker-compose interpolation safety (same gotcha we hit
# with the Argon2 admin hash — Brevo keys are usually alphanumeric so this
# is mostly defensive, but it's free insurance).
escape_for_compose() {
  printf '%s' "$1" | sed 's/\$/$$/g'
}

SMTP_USERNAME_ESCAPED=$(escape_for_compose "$SMTP_USERNAME")
SMTP_PASSWORD_ESCAPED=$(escape_for_compose "$SMTP_PASSWORD")
SMTP_FROM_ESCAPED=$(escape_for_compose "$SMTP_FROM")
SMTP_HOST_ESCAPED=$(escape_for_compose "$SMTP_HOST")

# Use # as sed delimiter (values contain @ but no #)
sed -i "s#^SMTP_HOST=.*#SMTP_HOST=${SMTP_HOST_ESCAPED}#" "$ENV_FILE"
sed -i "s#^SMTP_PORT=.*#SMTP_PORT=${SMTP_PORT}#" "$ENV_FILE"
sed -i "s#^SMTP_USERNAME=.*#SMTP_USERNAME=${SMTP_USERNAME_ESCAPED}#" "$ENV_FILE"
sed -i "s#^SMTP_PASSWORD=.*#SMTP_PASSWORD=${SMTP_PASSWORD_ESCAPED}#" "$ENV_FILE"
sed -i "s#^SMTP_FROM=.*#SMTP_FROM=${SMTP_FROM_ESCAPED}#" "$ENV_FILE"

# SMTP_USESSL=true for port 587 (STARTTLS) or 465 (TLS)
sed -i "s#^SMTP_USESSL=.*#SMTP_USESSL=true#" "$ENV_FILE"

echo ""
echo "Settings written. Inspecting (password line redacted):"
grep -E '^SMTP_' "$ENV_FILE" | sed -E 's/^(SMTP_PASSWORD=).*/\1<hidden>/'

echo ""
echo "Restarting license-server to pick up new env vars..."
cd "$COMPOSE_DIR"
docker compose up -d --force-recreate license-server 2>&1 | tail -5

echo ""
echo "Waiting 6s for app startup..."
sleep 6

echo ""
echo "Last 20 lines of license-server log (looking for any SMTP-related boot warnings):"
docker logs orderdeck-license --tail 20 2>&1 | grep -iE 'smtp|email|mail|error|warn' || echo "(no SMTP-related messages — normal at boot, real test happens on first send)"

echo ""
echo "Done. SMTP configured. Next: trigger a test email (e.g. password reset for the admin user)"
echo "to verify delivery + check spam folder."
