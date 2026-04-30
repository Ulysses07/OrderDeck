#!/usr/bin/env bash
set -euo pipefail

# Bootstrap initial OrderDeck admin user.
# Hashes a password (Argon2id, matches PasswordHasher.cs OWASP 2024 params),
# escapes $ as $$ for docker-compose interpolation safety,
# writes ADMIN_PASSWORD_HASH to /opt/orderdeck/.env, deletes any existing
# admin row (so SeedAdminAsync re-inserts), then restarts license-server.

ENV_FILE=/opt/orderdeck/.env
COMPOSE_DIR=/opt/orderdeck

if [ ! -f "$ENV_FILE" ]; then
  echo "ERROR: $ENV_FILE not found" >&2
  exit 1
fi

read -rsp "Admin password: " PWD1
echo
read -rsp "Confirm:        " PWD2
echo

if [ "$PWD1" != "$PWD2" ]; then
  echo "ERROR: passwords do not match" >&2
  exit 1
fi

if [ ${#PWD1} -lt 12 ]; then
  echo "ERROR: password must be at least 12 characters" >&2
  exit 1
fi

# Argon2id m=65536 KB, t=4, p=2, l=32 — matches PasswordHasher.cs exactly
SALT=$(openssl rand -base64 12 | head -c 16)
HASH=$(printf '%s' "$PWD1" | argon2 "$SALT" -id -t 4 -m 16 -p 2 -l 32 -e)

# CRITICAL: escape $ as $$ for docker-compose interpolation in .env
# (docker compose treats $argon2id$v=19$m=... as variable references otherwise)
HASH_ESCAPED=$(printf '%s' "$HASH" | sed 's/\$/$$/g')

# Update .env (use # as sed delimiter; hash contains / + =)
sed -i "s#^ADMIN_PASSWORD_HASH=.*#ADMIN_PASSWORD_HASH=${HASH_ESCAPED}#" "$ENV_FILE"

# Verify written value starts with $$argon2id (quoted so $$ is literal)
if ! grep -qF '$$argon2id' "$ENV_FILE"; then
  echo "ERROR: failed to write properly-escaped hash to $ENV_FILE" >&2
  exit 1
fi

echo ""
echo "Hash written (escaped) to $ENV_FILE"

ADMIN_USERNAME=$(grep '^ADMIN_USERNAME=' "$ENV_FILE" | cut -d= -f2-)
SQL_PWD=$(grep '^SQL_PASSWORD=' "$ENV_FILE" | cut -d= -f2-)

echo ""
echo "Deleting any existing admin row (so SeedAdminAsync re-inserts on restart)..."
docker exec orderdeck-sqlserver /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$SQL_PWD" -C -No \
  -Q "USE OrderDeckLicense; DELETE FROM AdminUsers WHERE Username = '${ADMIN_USERNAME}';" | tail -3

echo ""
echo "Restarting license-server..."
cd "$COMPOSE_DIR"
docker compose up -d --force-recreate license-server 2>&1 | tail -5

echo ""
echo "Waiting 8s for app to start + reseed..."
sleep 8

echo ""
echo "Verifying admin user (HashPrefix should start with \$argon2id):"
docker exec orderdeck-sqlserver /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$SQL_PWD" -C -No \
  -Q "USE OrderDeckLicense; SELECT Username, LEFT(PasswordHash, 30) AS HashPrefix, CreatedAt FROM AdminUsers;" | head -8

echo ""
echo "Done. Login at https://license.orderdeck.app/admin/login (once DNS+TLS up)"
