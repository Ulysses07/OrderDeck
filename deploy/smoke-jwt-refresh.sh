#!/usr/bin/env bash
# OrderDeck License Server — JWT refresh-token smoke test
#
# Exercises the full Phase 5d auth lifecycle against a live deployment:
#   register → force-confirm (DB) → login → call /me → refresh → call /me with
#   new access token → reuse old refresh (must 401) → logout → refresh post-
#   logout (must 401)
#
# Run AFTER deploying a build that ships the refresh-token rotation work
# (commit ba4416c onward). Verifies prod actually behaves the way the
# integration tests do.
#
# USAGE
#   BASE_URL=https://license.orderdeckapp.com SQL_PASSWORD=... bash smoke-jwt-refresh.sh
#
# REQUIREMENTS
#   - curl, jq
#   - SSH access to the VPS so the script can SQL-confirm the throwaway customer
#     (otherwise registration sits in unconfirmed state and login 403s)
#   - The script creates AND deletes a single customer; idempotent on re-run.

set -euo pipefail

BASE_URL=${BASE_URL:-https://license.orderdeckapp.com}
SSH_HOST=${SSH_HOST:-root@72.62.53.86}
SQL_CONTAINER=${SQL_CONTAINER:-orderdeck-sqlserver}
SQL_DB=${SQL_DB:-OrderDeckLicense}

if [[ -z "${SQL_PASSWORD:-}" ]]; then
  echo "ERROR: SQL_PASSWORD env var required (used over SSH to confirm the throwaway customer)" >&2
  exit 1
fi

EMAIL="smoke-$(date +%s)-$RANDOM@orderdeck-smoke.invalid"
PASSWORD="SmokeTest!$(date +%s)"
NAME="JWT Smoke"

# Color helpers — output is meant to be human-readable in a terminal.
red()    { printf "\e[31m%s\e[0m\n" "$*"; }
green()  { printf "\e[32m%s\e[0m\n" "$*"; }
yellow() { printf "\e[33m%s\e[0m\n" "$*"; }
step()   { yellow "═══ $* ═══"; }

cleanup() {
  yellow "Cleanup: deleting throwaway customer ${EMAIL}"
  ssh -o ConnectTimeout=5 "${SSH_HOST}" \
    "docker exec ${SQL_CONTAINER} /opt/mssql-tools18/bin/sqlcmd \
       -S localhost -U sa -P '${SQL_PASSWORD}' -d ${SQL_DB} -C -No \
       -Q \"DELETE FROM RefreshTokens WHERE CustomerId IN (SELECT Id FROM Customers WHERE Email = '${EMAIL}'); \
            DELETE FROM EmailLogs WHERE CustomerId IN (SELECT Id FROM Customers WHERE Email = '${EMAIL}'); \
            DELETE FROM EmailConfirmationTokens WHERE CustomerId IN (SELECT Id FROM Customers WHERE Email = '${EMAIL}'); \
            DELETE FROM Customers WHERE Email = '${EMAIL}';\"" \
    >/dev/null 2>&1 || true
}
trap cleanup EXIT

step "1. Register customer ${EMAIL}"
register_resp=$(curl -fsS -w '\n%{http_code}' -X POST "${BASE_URL}/api/v1/auth/register" \
  -H 'Content-Type: application/json' \
  -d "{\"email\":\"${EMAIL}\",\"password\":\"${PASSWORD}\",\"name\":\"${NAME}\"}")
status=$(echo "$register_resp" | tail -1)
[[ "$status" =~ ^(201|202)$ ]] || { red "register failed (HTTP $status)"; exit 1; }
green "  register OK (HTTP $status)"

step "2. Force-confirm email via SQL (test would normally click a mail link)"
ssh -o ConnectTimeout=5 "${SSH_HOST}" \
  "docker exec ${SQL_CONTAINER} /opt/mssql-tools18/bin/sqlcmd \
     -S localhost -U sa -P '${SQL_PASSWORD}' -d ${SQL_DB} -C -No \
     -Q \"UPDATE Customers SET EmailConfirmedAt = SYSDATETIMEOFFSET() WHERE Email = '${EMAIL}';\"" \
  >/dev/null
green "  email confirmed"

step "3. Login → expect access + refresh token"
login_resp=$(curl -fsS -X POST "${BASE_URL}/api/v1/auth/login" \
  -H 'Content-Type: application/json' \
  -d "{\"email\":\"${EMAIL}\",\"password\":\"${PASSWORD}\"}")
ACCESS_1=$(echo "$login_resp" | jq -r '.token')
REFRESH_1=$(echo "$login_resp" | jq -r '.refreshToken')
EXPIRES_AT=$(echo "$login_resp" | jq -r '.expiresAt')
[[ -n "$ACCESS_1" && "$ACCESS_1" != "null" ]]   || { red "login response missing .token"; exit 1; }
[[ -n "$REFRESH_1" && "$REFRESH_1" != "null" ]] || { red "login response missing .refreshToken"; exit 1; }
green "  login OK; access expires at ${EXPIRES_AT}"
green "  access len=${#ACCESS_1}, refresh len=${#REFRESH_1}"

step "4. /me with the access token → expect 200 + customer.email match"
me_resp=$(curl -fsS "${BASE_URL}/api/v1/me" -H "Authorization: Bearer ${ACCESS_1}")
me_email=$(echo "$me_resp" | jq -r '.email')
[[ "$me_email" == "$EMAIL" ]] || { red "/me email mismatch: got '${me_email}'"; exit 1; }
green "  /me OK"

step "5. Rotate via /auth/refresh → expect new access + new refresh"
rotate_resp=$(curl -fsS -X POST "${BASE_URL}/api/v1/auth/refresh" \
  -H 'Content-Type: application/json' \
  -d "{\"refreshToken\":\"${REFRESH_1}\"}")
ACCESS_2=$(echo "$rotate_resp" | jq -r '.token')
REFRESH_2=$(echo "$rotate_resp" | jq -r '.refreshToken')
[[ "$ACCESS_2" != "null" && "$REFRESH_2" != "null" ]] || { red "refresh response malformed"; exit 1; }
[[ "$REFRESH_2" != "$REFRESH_1" ]] || { red "refresh token did NOT rotate"; exit 1; }
green "  refresh rotated (old → new); both tokens differ"

step "6. /me with NEW access token → expect 200"
me2_status=$(curl -s -o /dev/null -w '%{http_code}' "${BASE_URL}/api/v1/me" \
  -H "Authorization: Bearer ${ACCESS_2}")
[[ "$me2_status" == "200" ]] || { red "/me with rotated access failed (HTTP $me2_status)"; exit 1; }
green "  /me OK with new access"

step "7. Replay OLD refresh → MUST 401 (single-use rotation)"
replay_status=$(curl -s -o /dev/null -w '%{http_code}' -X POST "${BASE_URL}/api/v1/auth/refresh" \
  -H 'Content-Type: application/json' \
  -d "{\"refreshToken\":\"${REFRESH_1}\"}")
[[ "$replay_status" == "401" ]] || { red "old refresh accepted! (HTTP $replay_status) — rotation chain broken"; exit 1; }
green "  old refresh correctly rejected (HTTP 401)"

step "8. Logout (revoke current refresh) → expect 204"
logout_status=$(curl -s -o /dev/null -w '%{http_code}' -X POST "${BASE_URL}/api/v1/auth/logout" \
  -H "Authorization: Bearer ${ACCESS_2}" \
  -H 'Content-Type: application/json' \
  -d "{\"refreshToken\":\"${REFRESH_2}\"}")
[[ "$logout_status" == "204" ]] || { red "logout failed (HTTP $logout_status)"; exit 1; }
green "  logout OK"

step "9. Refresh after logout → MUST 401"
post_logout_status=$(curl -s -o /dev/null -w '%{http_code}' -X POST "${BASE_URL}/api/v1/auth/refresh" \
  -H 'Content-Type: application/json' \
  -d "{\"refreshToken\":\"${REFRESH_2}\"}")
[[ "$post_logout_status" == "401" ]] || { red "refresh after logout accepted! (HTTP $post_logout_status)"; exit 1; }
green "  post-logout refresh correctly rejected"

green ""
green "═══════════════════════════════════════════"
green "  JWT REFRESH SMOKE PASSED"
green "═══════════════════════════════════════════"
