#!/usr/bin/env bash
#
# test-containers.sh — verifies the isolation of the running DataHub stack (compose.prod.yaml).
#
# Run ON THE SERVER as the deploy user or root, while the stack is up:
#   bash ~/datahub/current/server-provision/tests/test-containers.sh
# Works the same against the local test stack on a developer machine.
#
# It starts short-lived probe containers (busybox, postgres client) attached to the stack's networks
# to prove what can and cannot be reached from each one.
#
# Environment: PROJECT (datahub). Exit code: number of failed checks (0 = everything passed).

set -uo pipefail
# shellcheck source-path=SCRIPTDIR source=lib.sh
. "$(dirname "$0")/lib.sh"

PROJECT="${PROJECT:-datahub}"
PROBE_IMAGE="${PROBE_IMAGE:-busybox:1.37}"
PG_IMAGE="${PG_IMAGE:-postgres:17-alpine}"
FAKE_ID=00000000-0000-0000-0000-000000000000

command -v docker >/dev/null || { echo "docker not found" >&2; exit 125; }
docker info >/dev/null 2>&1 || { echo "cannot talk to Docker (run as root or a docker group member)" >&2; exit 125; }

title "Container and network isolation check" \
"Checks the running stack from the inside: which Docker networks exist and which services sit on \
them, which ports are published on the host, what a compromised container could reach, whether \
internal services demand credentials, and how the containers themselves are confined."

container() { docker ps -q --filter "name=^${PROJECT}-$1-[0-9]+$" | head -1; }
networks_of() { docker inspect -f '{{range $k, $v := .NetworkSettings.Networks}}{{$k}} {{end}}' "$1" 2>/dev/null; }
probe() { docker run --rm --network "${PROJECT}_$1" "$PROBE_IMAGE" "${@:2}" >/dev/null 2>&1; }
http_status() { # network url -> HTTP status code or "none"
    local code
    code="$(docker run --rm --network "${PROJECT}_$1" "$PROBE_IMAGE" sh -c "wget -S -O /dev/null -T 5 '$2' 2>&1" | awk '{for (i = 1; i < NF; i++) if ($i ~ /^HTTP\//) c = $(i + 1)} END {print c}')"
    echo "${code:-none}"
}

[ -n "$(container caddy)" ] || { echo "the $PROJECT stack is not running (no ${PROJECT}-caddy-1)" >&2; exit 125; }
docker image inspect "$PROBE_IMAGE" >/dev/null 2>&1 || docker pull -q "$PROBE_IMAGE" >/dev/null

# ---------------------------------------------------------------------------------------------
section "1. Network topology" \
"proxy (Caddy <-> web/keycloak/webhook), backend (services <-> databases, queue, S3) and observability \
must be 'internal': Docker gives them no gateway, so nothing on them can open connections to the \
Internet. ingress is the only routed network (Caddy: 80/443 and ACME). tunnel publishes the admin \
UIs on loopback with IP masquerading off."
for n in proxy backend observability; do
    if [ "$(docker network inspect -f '{{.Internal}}' "${PROJECT}_$n" 2>/dev/null)" = true ]; then pass "network $n is internal (no route out)"; else fail "network $n is not internal" "set 'internal: true' in the compose file"; fi
done
if [ "$(docker network inspect -f '{{.Internal}}' "${PROJECT}_ingress" 2>/dev/null)" = false ]; then pass "network ingress is routed (Caddy only)"; else fail "network ingress missing or internal"; fi
if [ "$(docker network inspect -f '{{index .Options "com.docker.network.bridge.enable_ip_masquerade"}}' "${PROJECT}_tunnel" 2>/dev/null)" = false ]; then pass "network tunnel has IP masquerading off"; else fail "network tunnel masquerades (admin UIs could reach the Internet)"; fi

# ---------------------------------------------------------------------------------------------
section "2. Which service sits on which network" \
"The Api, PostgreSQL, RabbitMQ and MinIO must never share a network with Caddy (proxy/ingress): then \
no public request can reach them, even through a misconfigured route. Only Caddy may be on ingress."
expect_nets() { # service must-have-regex must-not-have-regex
    local id nets
    id="$(container "$1")"
    [ -n "$id" ] || { warn "service $1 is not running"; return; }
    nets="$(networks_of "$id" | sed "s/${PROJECT}_//g")"
    if [ -n "$2" ] && ! grep -qwE "$2" <<< "$nets"; then fail "$1 is not on required network(s) $2 (on: $nets)"; return; fi
    if [ -n "$3" ] && grep -qwE "$3" <<< "$nets"; then fail "$1 is on forbidden network(s) matching $3 (on: $nets)"; return; fi
    pass "$1 on: $nets"
}
expect_nets caddy    "ingress"  "backend|observability|tunnel"
expect_nets web      "proxy"    "ingress|tunnel"
expect_nets webhook  "proxy"    "ingress|tunnel"
expect_nets keycloak "proxy"    "ingress"
expect_nets api      "backend"  "proxy|ingress|tunnel"
expect_nets postgres "backend"  "proxy|ingress|tunnel"
expect_nets rabbitmq "backend"  "proxy|ingress|tunnel"
expect_nets minio    "backend"  "proxy|ingress|tunnel"
expect_nets grafana  "tunnel"   "proxy|ingress|backend"
on_ingress="$(docker network inspect -f '{{range .Containers}}{{.Name}} {{end}}' "${PROJECT}_ingress" 2>/dev/null | xargs)"
if [ "$on_ingress" = "${PROJECT}-caddy-1" ]; then pass "only Caddy is on ingress"; else fail "containers on ingress: $on_ingress"; fi

# ---------------------------------------------------------------------------------------------
section "3. Ports published on the host" \
"Only Caddy's 80/443 may be published on a public address. Grafana and the Keycloak admin console \
may be published on 127.0.0.1 (reached through an SSH tunnel). Everything else publishes nothing."
while IFS=$'\t' read -r name ports; do
    [ -z "$ports" ] && continue
    public_ports="$(tr ',' '\n' <<< "$ports" | grep -- '->' | grep -vE '^\s*(127\.0\.0\.1|\[::1\]):' | sed 's/^ *//' || true)"
    if [ -z "$public_ports" ]; then
        pass "$name: loopback only ($(echo "$ports" | tr -d ' '))"
    elif [ "$name" = "${PROJECT}-caddy-1" ] && ! grep -qvE ':(80|443)->(80|443)/tcp' <<< "$public_ports"; then
        pass "$name: public 80/443 only"
    else
        fail "$name publishes publicly: $(echo "$public_ports" | tr '\n' ' ')" "bind the port to 127.0.0.1 or remove it"
    fi
done < <(docker ps --filter "name=^${PROJECT}-" --format '{{.Names}}\t{{.Ports}}')

# ---------------------------------------------------------------------------------------------
section "4. Lateral movement: what a compromised container could reach" \
"A probe container joins a network the way an attacker who took over a service would. From the \
proxy network (Caddy, web, webhook) the Api, database, queue and S3 must be unreachable; from the \
internal networks nothing on the Internet may be reachable (no data exfiltration, no C2 channel)."
if probe proxy nc -z -w 3 web 3000; then pass "proxy -> web:3000 reachable (expected: Caddy needs it)"; else fail "proxy -> web:3000 unreachable (the site would be down)"; fi
for target in api:8080 minio:9000 postgres:5432 rabbitmq:5672; do
    if probe proxy nc -z -w 3 "${target%:*}" "${target#*:}"; then fail "proxy -> $target REACHABLE" "remove ${target%:*} from the proxy network"; else pass "proxy -> $target blocked"; fi
done
for n in proxy backend observability tunnel; do
    if probe "$n" nc -z -w 4 1.1.1.1 443; then fail "$n -> Internet (1.1.1.1:443) REACHABLE"; else pass "$n -> Internet blocked"; fi
done

# ---------------------------------------------------------------------------------------------
section "5. Credentials required inside the backend network" \
"Network isolation is one layer; every internal service must still refuse anonymous access, so a \
compromised neighbour gets nothing for free: Api (JWT), MinIO buckets (no public read), RabbitMQ \
(no guest), PostgreSQL (password)."
code="$(http_status backend http://api:8080/graphql)"
if [ "$code" = 401 ]; then pass "Api /graphql without token -> 401"; else fail "Api /graphql without token -> $code (expected 401)"; fi
code="$(http_status backend "http://api:8080/documents/$FAKE_ID/content")"
if [ "$code" = 401 ]; then pass "Api document content without token -> 401"; else fail "Api document content without token -> $code (expected 401)"; fi
for bucket in documents branding webhooks; do
    code="$(http_status backend "http://minio:9000/$bucket/")"
    if [ "$code" = 403 ]; then pass "MinIO bucket '$bucket' anonymous listing -> 403"; else fail "MinIO bucket '$bucket' anonymous listing -> $code (expected 403)" "mc anonymous set none local/$bucket"; fi
done
code="$(http_status backend http://guest:guest@rabbitmq:15672/api/overview)"
if [ "$code" != 200 ]; then pass "RabbitMQ management rejects guest/guest ($code)"; else fail "RabbitMQ accepts guest/guest"; fi
if docker image inspect "$PG_IMAGE" >/dev/null 2>&1 || docker pull -q "$PG_IMAGE" >/dev/null 2>&1; then
    if docker run --rm --network "${PROJECT}_backend" -e PGCONNECT_TIMEOUT=4 "$PG_IMAGE" psql -h postgres -U postgres -w -c 'select 1' >/dev/null 2>&1; then
        fail "PostgreSQL accepts a login without password"
    else
        pass "PostgreSQL refuses a login without password"
    fi
fi

# ---------------------------------------------------------------------------------------------
section "6. Container confinement" \
"Privileged containers, the Docker socket, host namespaces and extra capabilities each hand a \
container (near) root on the host. Only the monitoring agents that need them may have them: \
cAdvisor (privileged, host paths) and Alloy (read-only Docker socket for logs)."
nnp_missing=()
while read -r id; do
    name="$(docker inspect -f '{{.Name}}' "$id" | sed 's#^/##')"
    svc="$(docker inspect -f '{{index .Config.Labels "com.docker.compose.service"}}' "$id")"
    privileged="$(docker inspect -f '{{.HostConfig.Privileged}}' "$id")"
    netmode="$(docker inspect -f '{{.HostConfig.NetworkMode}}' "$id")"
    pidmode="$(docker inspect -f '{{.HostConfig.PidMode}}' "$id")"
    capadd="$(docker inspect -f '{{join .HostConfig.CapAdd ","}}' "$id")"
    sock="$(docker inspect -f '{{range .Mounts}}{{.Source}} {{end}}' "$id" | grep -o '/var/run/docker.sock\|/run/docker.sock' | head -1)"
    secopt="$(docker inspect -f '{{join .HostConfig.SecurityOpt ","}}' "$id")"
    user="$(docker inspect -f '{{.Config.User}}' "$id")"
    issues=()
    [ "$privileged" = true ] && [ "$svc" != cadvisor ] && issues+=("privileged")
    [ -n "$sock" ] && [[ "$svc" != alloy && "$svc" != cadvisor ]] && issues+=("mounts docker.sock")
    [ "$netmode" = host ] && issues+=("host network")
    [ "$pidmode" = host ] && issues+=("host pid")
    [ -n "$capadd" ] && issues+=("cap_add $capadd")
    if [ "${#issues[@]}" -gt 0 ]; then fail "$name: ${issues[*]}"; else pass "$name confined$([ "$privileged" = true ] && echo ' (privileged: allowed for cAdvisor)')$([ -n "$sock" ] && echo ' (docker.sock: allowed for monitoring)')"; fi
    [[ "$secopt" == *no-new-privileges* ]] || nnp_missing+=("$name")
    if [ -z "$user" ] || [ "$user" = root ] || [ "$user" = 0 ]; then info "$name runs as root inside its container"; fi
done < <(docker ps -q --filter "name=^${PROJECT}-")
if [ "$(docker info -f '{{json .SecurityOptions}}' 2>/dev/null | grep -c no-new-privileges)" -gt 0 ] || \
   [ "$(jq -r '."no-new-privileges" // false' /etc/docker/daemon.json 2>/dev/null)" = true ]; then
    pass "no-new-privileges enforced daemon-wide"
elif [ "${#nnp_missing[@]}" -eq 0 ]; then
    pass "no-new-privileges set on every container"
else
    warn "no-new-privileges not enforced for ${#nnp_missing[@]} container(s)" "set it in /etc/docker/daemon.json (provision.sh does)"
fi

# ---------------------------------------------------------------------------------------------
section "7. Image provenance" \
"Floating tags (latest) change underneath a deployment and make a rollback non-reproducible; \
application images should be pinned to a commit SHA."
while IFS=$'\t' read -r name image; do
    last="${image##*/}"
    if [[ "$last" != *:* || "$image" == *:latest ]]; then warn "$name uses a floating tag: $image" "pin a version or digest"; else pass "$name: $image"; fi
done < <(docker ps --filter "name=^${PROJECT}-" --format '{{.Names}}\t{{.Image}}')

summary
