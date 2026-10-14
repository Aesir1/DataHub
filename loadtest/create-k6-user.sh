#!/usr/bin/env bash
# Creates the k6 load-test user in the datahub realm and writes .k6user.
# Run on the host that runs the Keycloak container (local or the test server):
#   KC_ADMIN_PASSWORD=... ./create-k6-user.sh [k6-password]
# Idempotent: safe to re-run; resets the password and role if the user exists.
set -euo pipefail
cd "$(dirname "$0")"

KC=${KC_CONTAINER:-datahub-keycloak-1}
ADMIN_USER=${KC_ADMIN_USER:-admin}
: "${KC_ADMIN_PASSWORD:?set KC_ADMIN_PASSWORD (Keycloak admin)}"
K6_EMAIL=${K6_EMAIL:-k6@local.test}
# No arg: random password (lands in .k6user — run.sh reads it from there anyway).
K6_PASSWORD=${1:-$(openssl rand -base64 18)}

kcadm() { docker exec "$KC" /opt/keycloak/bin/kcadm.sh "$@"; }

kcadm config credentials --server http://localhost:8080 --realm master \
  --user "$ADMIN_USER" --password "$KC_ADMIN_PASSWORD" >/dev/null

# firstName/lastName required: without them Keycloak interrupts the login
# with a VERIFY_PROFILE action and the OIDC flow never reaches the app.
kcadm create users -r datahub \
  -s "username=$K6_EMAIL" -s "email=$K6_EMAIL" -s enabled=true \
  -s emailVerified=true -s firstName=k6 -s lastName=loadtest \
  -s 'requiredActions=[]' 2>/dev/null || echo "user $K6_EMAIL exists, updating"

kcadm set-password -r datahub --username "$K6_EMAIL" --new-password "$K6_PASSWORD"
kcadm add-roles -r datahub --uusername "$K6_EMAIL" --rolename user

echo "K6PW=$K6_PASSWORD" > .k6user && chmod 600 .k6user
echo "done: $K6_EMAIL / password in .k6user"
