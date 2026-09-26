#!/usr/bin/env bash
#
# Server-side release management for DataHub. Runs as the deploy user; the GitHub workflows call it
# over SSH, and you can call it by hand on the server for an emergency rollback without GitHub.
#
# Layout (under ~/datahub, created by provision.sh):
#   .env                     live secrets, shared by every release (never in git)
#   releases/<sha>/          compose files, docker/, keycloak realm/theme and this folder for one commit
#   releases/<sha>/.release  IMAGE_REPO/IMAGE_TAG the release runs
#   current -> releases/<sha>   the release that passed its health check last
#   history                  one line per successful activation: "<timestamp> <sha>"
#
# Commands:
#   release.sh activate <sha>   start that release (pulls images), wait until healthy, point current at it
#   release.sh rollback [sha]   re-activate an earlier release (default: the one before current)
#   release.sh restore          re-activate current (after a failed activation left a half-started stack)
#   release.sh previous         print the SHA "rollback" would use
#   release.sh status           show current, recent history and the containers
#
# The compose project name is fixed ("name: datahub" in compose.prod.yaml), so every release drives
# the same containers and volumes; only images and stack files change.
#
# Database migrations are NOT rolled back: dbutils-migrate only moves forward. Keep migrations
# backwards compatible (expand, deploy, contract) so the previous release still runs on the new schema.

set -euo pipefail

BASE="${DATAHUB_HOME:-$HOME/datahub}"
KEEP="${KEEP_RELEASES:-5}"
HEALTH_TIMEOUT="${HEALTH_TIMEOUT:-420}"

log() { printf '[release] %s\n' "$*"; }
die() { printf '[release] error: %s\n' "$*" >&2; exit 1; }

valid_sha() { [[ "$1" =~ ^[0-9a-f]{40}$ ]] || die "not a full 40-character commit SHA: $1"; }
current_sha() { [ -L "$BASE/current" ] && basename "$(readlink "$BASE/current")" || true; }

compose() { docker compose --project-directory "$DIR" -f "$DIR/compose.prod.yaml" "$@"; }

check_env() {
    # A key added to .env.example since the server was provisioned would make compose fail with a
    # cryptic interpolation error halfway through. Fail early and say which keys are missing.
    local missing
    missing="$(comm -23 \
        <(sed -n 's/^\([A-Z0-9_]*\)=.*/\1/p' "$DIR/.env.example" | sort -u) \
        <(sed -n 's/^\([A-Z0-9_]*\)=.*/\1/p' "$BASE/.env" | sort -u))"
    [ -z "$missing" ] || die "keys missing from $BASE/.env (add them, see .env.example): $(echo "$missing" | tr '\n' ' ')"
}

wait_healthy() {
    # Healthy = no container starting/restarting/unhealthy, one-shot jobs exited 0, the rest running.
    local deadline=$((SECONDS + HEALTH_TIMEOUT)) states bad pending
    while :; do
        # service|state|health|exit code; health is empty for services without a healthcheck.
        states="$(compose ps -a --format json | jq -rs 'flatten | .[] | "\(.Service)|\(.State)|\(.Health // "")|\(.ExitCode // 0)"')"
        bad="$(awk -F'|' '$2 == "exited" && $4 != 0 { print $1 }' <<< "$states")"
        pending="$(awk -F'|' '$2 ~ /^(created|restarting|starting|paused|removing)$/ || $3 ~ /^(starting|unhealthy)$/ { print $1 }' <<< "$states")"
        if [ -n "$bad" ]; then
            compose ps -a
            for s in $bad; do compose logs --tail 40 "$s" || true; done
            die "failed: $(echo "$bad" | tr '\n' ' ')"
        fi
        [ -z "$pending" ] && { log "all services healthy"; return 0; }
        if [ "$SECONDS" -ge "$deadline" ]; then
            compose ps -a
            for s in $pending; do compose logs --tail 40 "$s" || true; done
            die "not healthy after ${HEALTH_TIMEOUT}s: $(echo "$pending" | tr '\n' ' ')"
        fi
        sleep 5
    done
}

prune() {
    # Keep the newest $KEEP releases from history (plus current) and only their images.
    local keep
    keep="$( { awk '{print $2}' "$BASE/history" 2>/dev/null | tac | awk '!seen[$0]++' | head -n "$KEEP"; current_sha; } | sort -u)"
    for d in "$BASE"/releases/*/; do
        d="${d%/}"
        # Uploaded within the last hour but not activated yet (a deploy in progress): keep.
        [ -n "$(find "$d" -maxdepth 0 -mmin -60)" ] && continue
        grep -qx "$(basename "$d")" <<< "$keep" || { rm -rf "$d"; log "removed release $(basename "$d")"; }
    done
    if [ -n "${IMAGE_REPO:-}" ]; then
        docker images --filter "reference=$IMAGE_REPO/*" --format '{{.Repository}}:{{.Tag}}' | while read -r image; do
            grep -qx "${image##*:}" <<< "$keep" || docker rmi "$image" >/dev/null 2>&1 || true
        done
    fi
    docker image prune -f >/dev/null
}

# activate <sha> <pull:yes|no>
activate() {
    local sha="$1" pull="$2"
    valid_sha "$sha"
    DIR="$BASE/releases/$sha"
    [ -f "$DIR/compose.prod.yaml" ] || die "release $sha is not on this server ($DIR); deploy it first"
    [ -s "$BASE/.env" ] || die "$BASE/.env is missing; run provision.sh"

    if [ -n "${IMAGE_REPO:-}" ]; then
        [[ "$IMAGE_REPO" =~ ^[a-z0-9./_-]+$ ]] || die "invalid IMAGE_REPO: $IMAGE_REPO"
        printf 'IMAGE_REPO=%s\nIMAGE_TAG=%s\n' "$IMAGE_REPO" "$sha" > "$DIR/.release"
    fi
    [ -f "$DIR/.release" ] || die "$DIR/.release is missing and IMAGE_REPO is not set"
    # Exported: shell variables outrank .env in compose interpolation.
    IMAGE_REPO="$(sed -n 's/^IMAGE_REPO=//p' "$DIR/.release")"
    IMAGE_TAG="$(sed -n 's/^IMAGE_TAG=//p' "$DIR/.release")"
    export IMAGE_REPO IMAGE_TAG

    ln -sfn ../../.env "$DIR/.env"
    check_env
    log "activating $sha ($IMAGE_REPO/*:$IMAGE_TAG)"
    if [ "$pull" = yes ]; then
        compose pull --quiet
    fi
    # Images still on disk are used as they are; missing ones are pulled (needs a registry login).
    compose up -d --remove-orphans
    wait_healthy

    ln -sfn "releases/$sha" "$BASE/current.tmp" && mv -Tf "$BASE/current.tmp" "$BASE/current"
    [ "$(tail -n1 "$BASE/history" 2>/dev/null | awk '{print $2}')" = "$sha" ] || printf '%s %s\n' "$(date -Iseconds)" "$sha" >> "$BASE/history"
    log "current -> $sha"
    prune
}

previous() {
    local cur
    cur="$(current_sha)"
    awk '{print $2}' "$BASE/history" 2>/dev/null | tac | awk -v cur="$cur" '$0 != cur && !seen[$0]++' | head -n1
}

case "${1:-}" in
    activate)
        [ -n "${2:-}" ] || die "usage: release.sh activate <sha>"
        activate "$2" yes
        ;;
    rollback)
        target="${2:-$(previous)}"
        [ -n "$target" ] || die "no earlier release in $BASE/history"
        log "rolling back from $(current_sha) to $target"
        activate "$target" no
        ;;
    restore)
        cur="$(current_sha)"
        [ -n "$cur" ] || die "no current release to restore"
        activate "$cur" no
        ;;
    previous)
        previous
        ;;
    status)
        echo "current: $(current_sha)"
        echo "history (newest last):"
        tail -n 10 "$BASE/history" 2>/dev/null | sed 's/^/  /' || true
        cur="$(current_sha)"
        if [ -n "$cur" ]; then DIR="$BASE/releases/$cur"; compose ps; fi
        ;;
    *)
        sed -n '2,/^set -euo/p' "$0" | sed 's/^# \{0,1\}//;/^set -euo/d'
        exit 2
        ;;
esac
