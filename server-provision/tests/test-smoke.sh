#!/usr/bin/env bash
#
# test-smoke.sh — is the deployed site up? Used by the deploy and rollback workflows after every
# release (a failure triggers the automatic rollback) and handy by hand.
#
#   APP_DOMAIN=... AUTH_DOMAIN=... HOOKS_DOMAIN=... bash server-provision/tests/test-smoke.sh
#
# Retries each URL for up to ~2 minutes (Caddy may still be obtaining a certificate on the first
# deploy). CA_FILE: extra CA to trust for a local test. Exit code: number of failed checks.

set -uo pipefail
# shellcheck source-path=SCRIPTDIR source=lib.sh
. "$(dirname "$0")/lib.sh"

: "${APP_DOMAIN:?set APP_DOMAIN}" "${AUTH_DOMAIN:?set AUTH_DOMAIN}" "${HOOKS_DOMAIN:?set HOOKS_DOMAIN}"
CURL=(curl -fsS --max-time 10 --retry 12 --retry-delay 10 --retry-all-errors -o /dev/null)
[ -n "${CA_FILE:-}" ] && CURL+=(--cacert "$CA_FILE")

title "Smoke test" "Checks that the web app, Keycloak and the webhook answer through Caddy over valid HTTPS."
section "Public endpoints" "Each URL must answer 2xx with a certificate valid for its name."
check "web app            https://$APP_DOMAIN/" "${CURL[@]}" "https://$APP_DOMAIN/"
check "Keycloak discovery https://$AUTH_DOMAIN/realms/datahub/.well-known/openid-configuration" "${CURL[@]}" "https://$AUTH_DOMAIN/realms/datahub/.well-known/openid-configuration"
check "webhook health     https://$HOOKS_DOMAIN/api/health" "${CURL[@]}" "https://$HOOKS_DOMAIN/api/health"
summary
