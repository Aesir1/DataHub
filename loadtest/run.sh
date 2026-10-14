#!/usr/bin/env bash
# Usage: ./run.sh [smoke|load|stress|soak] [local|compose|<url>] [extra k6 args...]
# HMAC_SECRET required; the compose preset reads WEBHOOK_HMAC_SECRET from ../.env.
set -euo pipefail
cd "$(dirname "$0")"

TYPE=${1:-smoke}
TARGET=${2:-compose}
shift $(( $# > 1 ? 2 : $# ))

SKIP_TLS=${SKIP_TLS:-}
case $TARGET in
  local)   BASE=https://localhost:7071 SKIP_TLS=1 ;;            # Aspire (dev cert)
  compose) BASE=https://hooks.datahub.localhost SKIP_TLS=1
           WEB_URL=${WEB_URL:-https://app.datahub.localhost} ;; # compose.prod.yaml via Caddy (internal CA)
  *)       BASE=$TARGET ;;                                      # raw URL passthrough
esac

# Compose preset: pull the secret the webhook container itself uses.
if [ -z "${HMAC_SECRET:-}" ] && [ -f ../.env ]; then
  HMAC_SECRET=$(grep -E '^WEBHOOK_HMAC_SECRET=' ../.env | head -1 | cut -d= -f2-)
fi

# Signed-in web flow: .k6user holds the local test user's password (see README "Web user flow").
if [ -z "${LOGIN_PASSWORD:-}" ] && [ -f .k6user ]; then
  LOGIN_PASSWORD=$(grep -E '^K6PW=' .k6user | cut -d= -f2-)
  LOGIN_EMAIL=${LOGIN_EMAIL:-k6@local.test}
fi
[ -n "${HMAC_SECRET:-}" ] || { echo "set HMAC_SECRET (or WEBHOOK_HMAC_SECRET in ../.env)" >&2; exit 1; }

# Every valid POST writes real Containers/Temperatures rows. Guard anything non-local.
case $BASE in
  *localhost*|*127.0.0.1*) ;;
  *) [ "${CONFIRM_PROD:-}" = 1 ] || { echo "refusing non-local target $BASE without CONFIRM_PROD=1 (writes real rows)" >&2; exit 1; } ;;
esac

mkdir -p results

ENVS=(-e TEST_TYPE="$TYPE" -e BASE_URL="$BASE" -e HMAC_SECRET="$HMAC_SECRET")
for v in CONTAINERS THINK SKIP_TLS WEB_URL LOGIN_EMAIL LOGIN_PASSWORD; do
  [ -n "${!v:-}" ] && ENVS+=(-e "$v=${!v}")
done

if command -v k6 >/dev/null 2>&1; then
  k6 run "${ENVS[@]}" "$@" main.js
else
  # --network host so localhost targets reach the host; --add-host because musl
  # inside the container does not resolve *.localhost names.
  # -u: the image's default user cannot write results/ on the host mount.
  docker run --rm -i --network host -u "$(id -u):$(id -g)" \
    --add-host hooks.datahub.localhost:127.0.0.1 \
    --add-host app.datahub.localhost:127.0.0.1 \
    --add-host auth.datahub.localhost:127.0.0.1 \
    -v "$PWD:/work" -w /work grafana/k6 run "${ENVS[@]}" "$@" main.js
fi
