#!/usr/bin/env bash
#
# test-external.sh — attacks the server from the outside, the way anyone on the Internet can.
#
# Run FROM ANOTHER MACHINE (your laptop, or the deploy workflow), never on the server itself:
#   APP_DOMAIN=app.example.com AUTH_DOMAIN=auth.example.com HOOKS_DOMAIN=hooks.example.com \
#     bash server-provision/tests/test-external.sh
#
# Environment:
#   APP_DOMAIN AUTH_DOMAIN HOOKS_DOMAIN   required
#   SSH_HOST (APP_DOMAIN)  SSH_PORT (22)
#   CA_FILE     extra CA to trust (local test with Caddy's internal CA); empty on a real server
#   SKIP_SSH=1  skip the SSH checks (e.g. local test without an SSH server)
# fail2ban records ~3 findings per run (5 within 10 minutes = 1 h ban): run it once per 10 minutes.
# Needs: bash, curl, openssl, ssh (client). Exit code: number of failed checks.

set -uo pipefail
# shellcheck source-path=SCRIPTDIR source=lib.sh
. "$(dirname "$0")/lib.sh"

: "${APP_DOMAIN:?set APP_DOMAIN}" "${AUTH_DOMAIN:?set AUTH_DOMAIN}" "${HOOKS_DOMAIN:?set HOOKS_DOMAIN}"
SSH_HOST="${SSH_HOST:-$APP_DOMAIN}"
SSH_PORT="${SSH_PORT:-22}"
CA_FILE="${CA_FILE:-}"
FAKE_ID=00000000-0000-0000-0000-000000000000

CURL=(curl -sS --max-time 10 --proto '=http,https')
OPENSSL_CA=()
if [ -n "$CA_FILE" ]; then CURL+=(--cacert "$CA_FILE"); OPENSSL_CA=(-CAfile "$CA_FILE"); fi
status() { local c; c="$("${CURL[@]}" -o /dev/null -w '%{http_code}' "$@" 2>/dev/null)"; echo "${c:-000}"; }
headers() { "${CURL[@]}" -o /dev/null -D - "$@" 2>/dev/null | tr -d '\r'; }

title "External security check" \
"Probes $APP_DOMAIN, $AUTH_DOMAIN and $HOOKS_DOMAIN from outside: open ports, HTTPS and TLS \
configuration, security headers, hidden admin and internal surfaces, and the SSH login policy. \
This is exactly what an attacker scanning the Internet sees."

# ---------------------------------------------------------------------------------------------
section "1. Open TCP ports" \
"Only SSH ($SSH_PORT), HTTP (80, redirect + ACME) and HTTPS (443) may accept connections. Databases, \
queues, S3, admin UIs and Docker's API must be unreachable. 'closed' and 'filtered' both count as \
not reachable."
host_ip="$(getent ahostsv4 "$SSH_HOST" 2>/dev/null | awk '{print $1; exit}')"
[ -n "$host_ip" ] || host_ip="$SSH_HOST"
info "scanning $SSH_HOST ($host_ip)"
ports=(21 22 23 25 53 80 110 111 135 139 143 443 445 993 995 1433 2375 2376 3000 3300 3306 5000 5100 5432 5672 6379 7071 8025 8080 8081 8443 9000 9001 9090 9093 9100 11211 15672 27017 "$SSH_PORT")
tmp="$(mktemp -d)"
for p in $(printf '%s\n' "${ports[@]}" | sort -un); do
    ( if tcp_open "$host_ip" "$p" 3; then touch "$tmp/$p"; fi ) &
done
wait
for p in $(printf '%s\n' "${ports[@]}" | sort -un); do
    if [ -e "$tmp/$p" ]; then
        case "$p" in
            "$SSH_PORT"|80|443) pass "port $p open (expected)" ;;
            *) fail "port $p OPEN" "close it in ufw, or bind the service to 127.0.0.1" ;;
        esac
    elif [ "$p" = 443 ] || [ "$p" = 80 ]; then
        fail "port $p not reachable (the site is down or blocked)"
    elif [ "$p" = "$SSH_PORT" ] && [ "${SKIP_SSH:-0}" != 1 ]; then
        warn "SSH port $p not reachable from here (fine if SSH is allow-listed)"
    else
        pass "port $p not reachable"
    fi
done
rm -rf "$tmp"

# ---------------------------------------------------------------------------------------------
section "2. HTTPS everywhere" \
"Plain http:// must only redirect to https:// (no content over clear text), certificates must be \
valid for the name and not about to expire."
for d in "$APP_DOMAIN" "$AUTH_DOMAIN" "$HOOKS_DOMAIN"; do
    read -r code location < <("${CURL[@]}" -o /dev/null -w '%{http_code} %{redirect_url}\n' "http://$d/" 2>/dev/null || echo "000 -")
    if [[ "$code" =~ ^30[178]$ ]] && [[ "$location" == https://"$d"* ]]; then pass "http://$d -> $code $location"; else fail "http://$d answers $code ${location:-} (expected a redirect to https)"; fi
    if "${CURL[@]}" -o /dev/null "https://$d/" 2>/dev/null; then pass "https://$d certificate valid for the name"; else fail "https://$d certificate invalid or connection failed" "check Caddy logs: docker compose logs caddy"; fi
    cert="$(openssl s_client -connect "$host_ip:443" -servername "$d" "${OPENSSL_CA[@]}" </dev/null 2>/dev/null | openssl x509 2>/dev/null)"
    if [ -z "$cert" ]; then fail "$d: no certificate received"
    elif openssl x509 -noout -checkend $((14 * 86400)) <<< "$cert" >/dev/null; then pass "$d certificate valid for more than 14 days ($(openssl x509 -noout -enddate <<< "$cert" | cut -d= -f2))"
    elif [ -n "$CA_FILE" ]; then info "$d certificate expires within 14 days (normal for Caddy's internal CA: 12 h certificates)"
    else fail "$d certificate expires within 14 days" "Caddy renews automatically; check it can reach the ACME server"; fi
done

# ---------------------------------------------------------------------------------------------
section "3. TLS protocol versions" \
"TLS 1.0 and 1.1 are broken (BEAST, POODLE-class attacks, SHA-1) and must be refused; TLS 1.2 and \
1.3 must work."
tls_ok() { openssl s_client -connect "$host_ip:443" -servername "$APP_DOMAIN" "$1" -cipher 'DEFAULT:@SECLEVEL=0' </dev/null >/dev/null 2>&1; }
for v in tls1 tls1_1; do
    if tls_ok "-$v"; then fail "$v accepted"; else pass "$v refused"; fi
done
if tls_ok -tls1_2; then pass "tls1_2 accepted"; else fail "tls1_2 refused (old clients cannot connect)"; fi
if openssl s_client -connect "$host_ip:443" -servername "$APP_DOMAIN" -tls1_3 </dev/null >/dev/null 2>&1; then pass "tls1_3 accepted"; else fail "tls1_3 refused"; fi

# ---------------------------------------------------------------------------------------------
section "4. Security headers" \
"HSTS stops downgrade attacks (the browser never tries http:// again), nosniff and frame denial stop \
MIME confusion and clickjacking, and no header should advertise server software or versions."
h="$(headers "https://$APP_DOMAIN/")"
hsts="$(grep -i '^strict-transport-security:' <<< "$h" | sed -n 's/.*max-age=\([0-9]*\).*/\1/p')"
if [ "${hsts:-0}" -ge 15552000 ]; then pass "HSTS max-age=$hsts"; else fail "HSTS missing or shorter than 180 days (${hsts:-none})"; fi
if grep -qi '^x-content-type-options: nosniff' <<< "$h"; then pass "X-Content-Type-Options: nosniff"; else fail "X-Content-Type-Options missing"; fi
if grep -qiE '^x-frame-options: (deny|sameorigin)' <<< "$h" || grep -qi "frame-ancestors" <<< "$h"; then pass "clickjacking protection (X-Frame-Options / frame-ancestors)"; else fail "no clickjacking protection"; fi
if grep -qi '^referrer-policy:' <<< "$h"; then pass "Referrer-Policy set"; else warn "Referrer-Policy missing"; fi
leak="$(grep -iE '^(server|x-powered-by|x-aspnet-version|x-aspnetmvc-version):' <<< "$h" | tr '\n' ' ')"
if [ -z "$leak" ]; then pass "no server/version headers"; else warn "software disclosed: $leak" "strip it in Caddy (header -Name) or the app"; fi
for d in "$AUTH_DOMAIN" "$HOOKS_DOMAIN"; do
    if headers "https://$d/" | grep -qi '^strict-transport-security:'; then pass "$d sends HSTS"; else fail "$d without HSTS"; fi
done
code="$(status -X TRACE "https://$APP_DOMAIN/")"
if [ "$code" != 200 ]; then pass "TRACE method not served ($code)"; else fail "TRACE answered 200 (cross-site tracing)"; fi

# ---------------------------------------------------------------------------------------------
section "5. Hidden and internal surfaces" \
"The Api, MinIO, the Keycloak admin console/master realm and the Azure Functions admin endpoints must \
not be reachable from the Internet. Public routes that need a user must answer 401 without one."
expect() { # description expected-regex curl-args...
    local desc="$1" want="$2" code
    shift 2
    code="$(status "$@")"
    if [[ "$code" =~ ^($want)$ ]]; then pass "$desc -> $code"; else fail "$desc -> $code (expected $want)"; fi
}
expect "Keycloak admin console https://$AUTH_DOMAIN/admin/" 404 "https://$AUTH_DOMAIN/admin/"
expect "Keycloak master realm (admin login) https://$AUTH_DOMAIN/realms/master/" 404 "https://$AUTH_DOMAIN/realms/master/"
expect "Keycloak master realm token endpoint" 404 -X POST "https://$AUTH_DOMAIN/realms/master/protocol/openid-connect/token"
expect "Keycloak datahub realm discovery (public by design)" 200 "https://$AUTH_DOMAIN/realms/datahub/.well-known/openid-configuration"
expect "Keycloak metrics https://$AUTH_DOMAIN/metrics" 404 "https://$AUTH_DOMAIN/metrics"
expect "Api GraphQL is not published (https://$APP_DOMAIN/graphql)" "404|405" -X POST -H 'content-type: application/json' -d '{"query":"{__typename}"}' "https://$APP_DOMAIN/graphql"
expect "GraphQL BFF without session" 401 -X POST -H 'content-type: application/json' -d '{"query":"{__typename}"}' "https://$APP_DOMAIN/api/graphql"
expect "document download without session" 401 "https://$APP_DOMAIN/api/documents/$FAKE_ID/content"
expect "document upload without session" 401 -X PUT -H 'content-type: text/plain' --data 'x' "https://$APP_DOMAIN/api/documents/$FAKE_ID/content"
expect "webhook health (public by design)" 200 "https://$HOOKS_DOMAIN/api/health"
expect "webhook root" 404 "https://$HOOKS_DOMAIN/"
expect "Azure Functions admin API" 404 "https://$HOOKS_DOMAIN/admin/host/status"
expect "unsigned webhook" "401" -X POST -H 'content-type: application/json' -d '{}' "https://$HOOKS_DOMAIN/api/webhooks/container"
for sub in files s3 minio grafana; do
    name="$sub.${APP_DOMAIN#*.}"
    if getent ahosts "$name" >/dev/null 2>&1 && [ "$(status "https://$name/")" != 000 ]; then warn "https://$name answers; is something exposed there?"; else pass "$name serves nothing"; fi
done

# ---------------------------------------------------------------------------------------------
if [ "${SKIP_SSH:-0}" = 1 ]; then
    section "6. SSH" "Skipped (SKIP_SSH=1)."
else
    section "6. SSH" \
"SSH must accept public keys only (no passwords to guess), offer no outdated algorithms (SHA-1, CBC, \
small DH groups, ECDSA/DSA host keys) and not advertise the OS in its banner. Everything is read from \
ONE connection (the server's algorithm proposal and auth methods). fail2ban still records ~3 findings \
per run of this script: run it at most once per 10 minutes from the same IP."
    out="$(ssh -vv -p "$SSH_PORT" -o BatchMode=yes -o ConnectTimeout=8 -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null \
        -o PreferredAuthentications=none -o PubkeyAuthentication=no "probe-$RANDOM@$SSH_HOST" true 2>&1 | tr -d '\r')"
    proposal="$(sed -n '/peer server KEXINIT proposal/,/first_kex_follows/p' <<< "$out")"
    if [ -z "$proposal" ]; then
        warn "no SSH handshake with $SSH_HOST:$SSH_PORT ($(grep -m1 -iE 'refused|timed out|closed' <<< "$out" || echo 'no answer')); banned by fail2ban or filtered?"
    else
        methods="$(sed -n 's/.*Authentications that can continue: //p' <<< "$out" | head -1 | tr -d ' ')"
        if [ -n "$methods" ] && ! grep -qE 'password|keyboard-interactive' <<< "$methods"; then pass "only key-based authentication offered ($methods)"
        else fail "authentication methods offered: ${methods:-unknown}" "PasswordAuthentication no / KbdInteractiveAuthentication no"; fi
        algos() { sed -n "s/^debug2: $1: //p" <<< "$proposal" | head -1 | tr ',' '\n'; }
        weak_in() { # description list-name regex
            local found
            found="$(algos "$2" | grep -E "$3" | tr '\n' ' ')"
            if [ -z "$found" ]; then pass "$1: none offered"; else fail "$1 offered: $found" "restrict it in /etc/ssh/sshd_config.d/00-datahub-hardening.conf"; fi
        }
        weak_in "SHA-1 / small-group key exchange" "KEX algorithms" 'sha1|group1-|group14-sha256|group-exchange'
        weak_in "DSA/ECDSA/SHA-1 host key algorithms" "host key algorithms" 'dss|ecdsa|^ssh-rsa$'
        weak_in "CBC, 3DES or RC4 ciphers" "ciphers stoc" 'cbc|3des|arcfour|rijndael'
        weak_in "SHA-1, MD5, 64-bit or non-ETM MACs" "MACs stoc" 'md5|sha1|umac-64|^hmac-sha2-(256|512)$'
        info "offered KEX: $(algos "KEX algorithms" | grep -v '^ext-info\|^kex-strict' | tr '\n' ' ')"
        banner="$(sed -n 's/.*Remote protocol version [^,]*, remote software version //p' <<< "$out" | head -1)"
        if grep -qiE 'ubuntu|debian' <<< "$banner"; then warn "SSH banner discloses the OS: $banner" "DebianBanner no"
        else pass "SSH banner does not disclose the OS: ${banner:-?}"; fi
    fi
fi

summary
