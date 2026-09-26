#!/usr/bin/env bash
#
# test-host.sh — verifies the operating-system hardening applied by provision.sh.
#
# Run ON THE SERVER as root:   sudo bash server-provision/tests/test-host.sh [--lynis]
#
#   --lynis   also runs a Lynis audit (1-3 minutes) and reports its hardening index.
#
# Environment: SSH_PORT (22), ADMIN_USER (ops), DEPLOY_USER (deploy), APP_DIR_NAME (datahub).
# Exit code: number of failed checks (0 = everything passed).

set -uo pipefail
# shellcheck source-path=SCRIPTDIR source=lib.sh
. "$(dirname "$0")/lib.sh"

SSH_PORT="${SSH_PORT:-22}"
ADMIN_USER="${ADMIN_USER:-ops}"
DEPLOY_USER="${DEPLOY_USER:-deploy}"
APP_DIR_NAME="${APP_DIR_NAME:-datahub}"
RUN_LYNIS=0
[ "${1:-}" = --lynis ] && RUN_LYNIS=1

[ "$(id -u)" -eq 0 ] || { echo "run as root: sudo bash $0" >&2; exit 125; }

title "Host security check" \
"Checks the server itself: patches, accounts, SSH, firewall, brute-force protection, kernel settings, \
auditing, Docker daemon and file permissions. Each section explains what is tested and why; each line \
says PASS, FAIL (with a fix) or WARN."

# ---------------------------------------------------------------------------------------------
section "1. Security updates" \
"Unpatched packages are the most common way in. Security updates must install automatically, and a \
kernel update only protects after a reboot."
pending="$(apt-get -s -o Debug::NoLocking=1 upgrade 2>/dev/null | grep -c '^Inst .*-security' || true)"
if [ "$pending" -eq 0 ]; then pass "no pending security updates"; else fail "$pending security update(s) pending" "apt-get update && apt-get upgrade"; fi
check "unattended-upgrades service enabled" systemctl is-enabled unattended-upgrades
if grep -qs 'Unattended-Upgrade "1"' /etc/apt/apt.conf.d/51datahub-unattended; then pass "daily unattended upgrades configured"; else fail "daily unattended upgrades not configured" "re-run provision.sh"; fi
if [ -f /var/run/reboot-required ]; then warn "reboot required to activate updates ($(tr '\n' ' ' < /var/run/reboot-required.pkgs 2>/dev/null | cut -c1-80))" "sudo reboot"; else pass "no reboot pending"; fi

# ---------------------------------------------------------------------------------------------
section "2. Accounts and privilege" \
"Only root may have UID 0, no account may have an empty password, root's password is locked (no \
console/su login), the deploy account has no sudo, and su is limited to the sudo group."
extra_uid0="$(awk -F: '$3 == 0 && $1 != "root" {print $1}' /etc/passwd)"
if [ -z "$extra_uid0" ]; then pass "only root has UID 0"; else fail "other UID 0 accounts: $extra_uid0" "remove them"; fi
empty="$(awk -F: '$2 == "" {print $1}' /etc/shadow)"
if [ -z "$empty" ]; then pass "no account with an empty password"; else fail "accounts with empty password: $empty" "passwd -l <user>"; fi
if passwd -S root | awk '{exit ($2 == "L") ? 0 : 1}'; then pass "root password locked"; else fail "root password is usable" "passwd -l root"; fi
if id "$ADMIN_USER" >/dev/null 2>&1 && id -nG "$ADMIN_USER" | grep -qw sudo; then pass "$ADMIN_USER exists and is in sudo"; else fail "$ADMIN_USER missing or not in sudo" "re-run provision.sh"; fi
if id -nG "$DEPLOY_USER" 2>/dev/null | grep -qwE 'sudo|admin|wheel'; then fail "$DEPLOY_USER has sudo rights" "gpasswd -d $DEPLOY_USER sudo"; else pass "$DEPLOY_USER has no sudo rights"; fi
if grep -qE '^auth\s+required\s+pam_wheel.so.*group=sudo' /etc/pam.d/su; then pass "su restricted to the sudo group"; else fail "su usable by every user" "enable pam_wheel in /etc/pam.d/su"; fi
if sudo -l -U "$ADMIN_USER" 2>/dev/null | grep -q NOPASSWD; then warn "$ADMIN_USER can sudo without a password"; else pass "sudo requires a password"; fi
deploy_keys="$(getent passwd "$DEPLOY_USER" | cut -d: -f6)/.ssh/authorized_keys"
if [ -f "$deploy_keys" ] && ! grep -v '^restrict ' "$deploy_keys" | grep -q '^[^#[:space:]]'; then pass "every deploy key carries 'restrict' (no forwarding, no pty)"; else fail "deploy key without 'restrict'" "prefix the key line in $deploy_keys with 'restrict '"; fi

# ---------------------------------------------------------------------------------------------
section "3. SSH daemon (effective configuration)" \
"Reads the configuration sshd really uses (sshd -T), not the files: key-only login, no root, named \
users only, few auth attempts, no agent/X11 forwarding, modern algorithms only."
sshd_t="$(sshd -T -C user="$ADMIN_USER",host=test,addr=192.0.2.1 2>/dev/null)"
expect_ssh() { # key expected-value description
    local actual
    actual="$(awk -v k="$1" '$1 == k { $1 = ""; sub(/^ /, ""); print }' <<< "$sshd_t")"
    if [ "$actual" = "$2" ]; then pass "$3 ($1 $actual)"; else fail "$3 ($1 is '$actual', expected '$2')" "check /etc/ssh/sshd_config.d/00-datahub-hardening.conf"; fi
}
expect_ssh passwordauthentication no "password login disabled"
expect_ssh kbdinteractiveauthentication no "keyboard-interactive disabled"
expect_ssh permitrootlogin no "root login disabled"
expect_ssh permitemptypasswords no "empty passwords refused"
expect_ssh pubkeyauthentication yes "public key login enabled"
expect_ssh x11forwarding no "X11 forwarding disabled"
expect_ssh allowagentforwarding no "agent forwarding disabled"
expect_ssh allowtcpforwarding local "only local port forwarding (tunnels)"
expect_ssh permittunnel no "tun devices disabled"
expect_ssh port "$SSH_PORT" "listening port"
maxtries="$(awk '$1 == "maxauthtries" {print $2}' <<< "$sshd_t")"
if [ "${maxtries:-6}" -le 3 ]; then pass "MaxAuthTries $maxtries"; else fail "MaxAuthTries ${maxtries:-6} (> 3)"; fi
allow="$(awk '$1 == "allowusers" {$1 = ""; print}' <<< "$sshd_t" | xargs)"
if [ -n "$allow" ] && ! grep -qw root <<< "$allow"; then pass "AllowUsers limited to: $allow"; else fail "AllowUsers not set or includes root ($allow)"; fi
weak="$(awk '$1 ~ /^(ciphers|macs|kexalgorithms)$/ {print $2}' <<< "$sshd_t" | tr ',' '\n' | grep -E 'cbc|3des|arcfour|md5|-sha1|sha1$|group1|group14-sha1|umac-64|hmac-sha2-(256|512)$' || true)"
if [ -z "$weak" ]; then pass "no weak ciphers, MACs or key exchanges offered"; else fail "weak algorithms offered: $(echo "$weak" | tr '\n' ' ')"; fi
if grep -Eq '^hostkey .*_(dsa|ecdsa)_key' <<< "$sshd_t"; then fail "DSA/ECDSA host keys offered"; else pass "only ed25519/RSA host keys offered"; fi
rsa_bits="$(ssh-keygen -l -f /etc/ssh/ssh_host_rsa_key 2>/dev/null | awk '{print $1}')"
if [ -z "$rsa_bits" ] || [ "$rsa_bits" -ge 3072 ]; then pass "RSA host key ${rsa_bits:-absent} bits"; else fail "RSA host key only $rsa_bits bits"; fi
perm="$(stat -c '%a %U' /etc/ssh/sshd_config)"
if [ "$perm" = "644 root" ] || [ "$perm" = "600 root" ]; then pass "sshd_config owned by root ($perm)"; else fail "sshd_config permissions $perm"; fi

# ---------------------------------------------------------------------------------------------
section "4. Firewall" \
"ufw drops everything inbound except SSH (rate limited), 80 and 443. Docker bypasses ufw for \
published ports, so the DOCKER-USER chain must drop all other new connections to containers."
ufw_status="$(ufw status verbose 2>/dev/null)"
if grep -q 'Status: active' <<< "$ufw_status"; then pass "ufw active"; else fail "ufw inactive" "ufw enable"; fi
if grep -q 'deny (incoming)' <<< "$ufw_status"; then pass "default policy: deny incoming"; else fail "default incoming policy is not deny"; fi
allowed="$(awk '/^--/ {f = 1; next} f && NF && $0 !~ /\(v6\)/ {print $1}' <<< "$ufw_status" | sort -u | tr '\n' ' ')"
unexpected="$(tr ' ' '\n' <<< "$allowed" | grep -vxE "($SSH_PORT|80|443)/tcp|" || true)"
if [ -z "$unexpected" ]; then pass "allowed inbound: $allowed"; else fail "unexpected inbound rules: $(echo "$unexpected" | tr '\n' ' ')" "ufw delete allow <rule>"; fi
if grep -Eq "^$SSH_PORT/tcp +LIMIT" <<< "$(ufw status)"; then pass "SSH rate limited (ufw limit)"; else warn "SSH not rate limited" "ufw limit $SSH_PORT/tcp"; fi
if iptables -S DOCKER-USER 2>/dev/null | grep -- '-j DROP' | grep -q 'NEW'; then pass "DOCKER-USER drops non-80/443 connections to containers"; else fail "DOCKER-USER chain has no drop rule" "re-run provision.sh, then ufw reload"; fi

# ---------------------------------------------------------------------------------------------
section "5. Brute-force protection" \
"fail2ban bans IPs that fail SSH logins repeatedly (sshd jail) and bans repeat offenders for a week \
(recidive jail)."
check "fail2ban running" systemctl is-active fail2ban
jails="$(fail2ban-client status 2>/dev/null | sed -n 's/.*Jail list:\s*//p')"
for j in sshd recidive; do
    if grep -qw "$j" <<< "$jails"; then pass "jail '$j' enabled ($(fail2ban-client status "$j" 2>/dev/null | sed -n 's/.*Currently banned:\s*/currently banned: /p'))"; else fail "jail '$j' not enabled"; fi
done

# ---------------------------------------------------------------------------------------------
section "6. Listening network services" \
"Every socket listening on a non-loopback address is reachable from the network (subject to the \
firewall). Only SSH, 80 and 443 should be there; admin tools listen on 127.0.0.1 and are reached via \
SSH tunnels."
listeners="$(ss -Hlntu 2>/dev/null | awk '{print $1, $5}')"
public="$(awk '{n = split($2, a, ":"); port = a[n]; addr = substr($2, 1, length($2) - length(port) - 1);
    if (addr !~ /^(127\.|\[?::1\]?$|\[::ffff:127\.)/ && addr !~ /%lo$/) print $1 "/" port}' <<< "$listeners" | sort -u)"
unexpected="$(grep -vxE "tcp/($SSH_PORT|80|443)|udp/68|udp/546" <<< "$public" || true)"
info "public listeners: $(echo "$public" | tr '\n' ' ')"
if [ -z "$unexpected" ]; then pass "only SSH, 80 and 443 listen publicly (plus DHCP client)"; else fail "unexpected public listeners: $(echo "$unexpected" | tr '\n' ' ')" "bind them to 127.0.0.1 or stop them"; fi
for p in 3300 8081; do
    if grep -qE ":$p$" <<< "$(awk '{print $2}' <<< "$listeners")"; then
        if grep -E ":$p$" <<< "$(awk '{print $2}' <<< "$listeners")" | grep -qvE '^(127\.0\.0\.1|\[::1\]):'; then fail "port $p (admin UI) listens beyond loopback"; else pass "port $p (admin UI) loopback only"; fi
    fi
done

# ---------------------------------------------------------------------------------------------
section "7. Kernel hardening (sysctl)" \
"Kernel settings against spoofing, ICMP redirects, source routing, SYN floods, kernel pointer/log \
leaks, unprivileged BPF and ptrace abuse, and unsafe links in world-writable directories."
while read -r key want; do
    [ -z "$key" ] && continue
    have="$(sysctl -n "$key" 2>/dev/null || echo missing)"
    if [ "$have" = "$want" ]; then pass "$key = $have"; else fail "$key = $have (expected $want)" "sysctl --system"; fi
done <<'LIST'
net.ipv4.conf.all.rp_filter 1
net.ipv4.conf.all.accept_redirects 0
net.ipv4.conf.all.send_redirects 0
net.ipv4.conf.all.accept_source_route 0
net.ipv6.conf.all.accept_redirects 0
net.ipv4.conf.all.log_martians 1
net.ipv4.tcp_syncookies 1
net.ipv4.icmp_echo_ignore_broadcasts 1
kernel.kptr_restrict 2
kernel.dmesg_restrict 1
kernel.unprivileged_bpf_disabled 1
kernel.yama.ptrace_scope 1
kernel.randomize_va_space 2
kernel.sysrq 0
fs.protected_hardlinks 1
fs.protected_symlinks 1
fs.suid_dumpable 0
LIST
if mount | grep -E ' on /dev/shm ' | grep -q noexec; then pass "/dev/shm mounted noexec"; else fail "/dev/shm allows execution" "mount -o remount,noexec,nosuid,nodev /dev/shm"; fi
if [ "$(ulimit -Hc)" = 0 ] || grep -qs 'hard core 0' /etc/security/limits.d/60-no-core.conf; then pass "core dumps disabled"; else fail "core dumps allowed"; fi
if modprobe -n -v dccp 2>/dev/null | grep -q '/bin/false'; then pass "unused protocols/filesystems blacklisted (e.g. dccp)"; else fail "module blacklist missing"; fi

# ---------------------------------------------------------------------------------------------
section "8. Access control, auditing and time" \
"AppArmor confines processes (including containers), auditd records changes to accounts, sudo, SSH, \
firewall, Docker and the secrets file, and the clock is synchronised (TLS, tokens, HMAC timestamps)."
if aa-status --enabled 2>/dev/null; then pass "AppArmor enabled, $(aa-status --enforced 2>/dev/null || echo '?') profiles enforcing"; else fail "AppArmor disabled"; fi
check "auditd running" systemctl is-active auditd
rules="$(auditctl -l 2>/dev/null | grep -c -- '-k ' || true)"
if [ "$rules" -ge 10 ]; then pass "$rules audit rules loaded"; else fail "only $rules audit rules loaded" "augenrules --load"; fi
if auditctl -l 2>/dev/null | grep -q datahub-secrets; then pass ".env access is audited (ausearch -k datahub-secrets)"; else warn ".env access not audited"; fi
if [ "$(timedatectl show -p NTPSynchronized --value 2>/dev/null)" = yes ]; then pass "clock NTP-synchronised"; else warn "clock not (yet) NTP-synchronised"; fi

# ---------------------------------------------------------------------------------------------
section "9. Docker daemon" \
"daemon.json must bind unspecified port publications to loopback, forbid privilege escalation in \
containers, cap log sizes, and the Docker socket must only be accessible to root and the docker group."
if command -v docker >/dev/null; then
    dj=/etc/docker/daemon.json
    jq_eq() { [ "$(jq -r "$1" "$dj" 2>/dev/null)" = "$2" ]; }
    if jq_eq .ip 127.0.0.1; then pass "default bind address 127.0.0.1"; else fail "daemon.json ip is not 127.0.0.1"; fi
    if jq_eq '."no-new-privileges"' true; then pass "no-new-privileges for all containers"; else fail "no-new-privileges not set"; fi
    if jq_eq '."log-opts"."max-size"' 10m; then pass "container logs capped (10m x 3)"; else fail "container logs not capped"; fi
    if jq_eq .icc false; then pass "inter-container traffic on default bridge disabled"; else warn "icc not disabled"; fi
    sock="$(stat -c '%a %U:%G' /var/run/docker.sock 2>/dev/null)"
    if [ "$sock" = "660 root:docker" ]; then pass "docker.sock $sock"; else fail "docker.sock permissions $sock"; fi
    members="$(getent group docker | cut -d: -f4)"
    if [ "$members" = "$DEPLOY_USER" ] || [ -z "$members" ]; then pass "docker group members: ${members:-none}"; else warn "docker group (root-equivalent) members: $members" "gpasswd -d <user> docker"; fi
    if docker info 2>/dev/null | grep -q 'Live Restore Enabled: true'; then pass "live-restore enabled"; else warn "live-restore disabled"; fi
else
    fail "docker not installed"
fi

# ---------------------------------------------------------------------------------------------
section "10. Files and secrets" \
"The .env file holds every production secret: it must be readable by the deploy user only. \
World-writable files in /etc or unexpected SUID binaries are classic privilege-escalation paths."
env_file="$(getent passwd "$DEPLOY_USER" | cut -d: -f6)/$APP_DIR_NAME/.env"
if [ -f "$env_file" ]; then
    perm="$(stat -c '%a %U' "$env_file")"
    if [ "$perm" = "600 $DEPLOY_USER" ]; then pass ".env mode $perm"; else fail ".env mode $perm (expected 600 $DEPLOY_USER)" "chmod 600 $env_file"; fi
    if grep -q 'change-me' "$env_file"; then fail ".env still contains change-me placeholders"; else pass ".env has no placeholder secrets"; fi
else
    warn ".env not found at $env_file"
fi
ww="$(find /etc /usr /opt -xdev -type f -perm -0002 2>/dev/null | head -5)"
if [ -z "$ww" ]; then pass "no world-writable files in /etc, /usr, /opt"; else fail "world-writable files: $(echo "$ww" | tr '\n' ' ')" "chmod o-w <file>"; fi
# Container image layers are skipped: their SUID files only exist inside containers.
suid="$(find / -xdev \( -path /var/lib/docker -o -path /var/lib/containerd -o -path /snap \) -prune -o -perm -4000 -type f -print 2>/dev/null | grep -vE '^/(usr/)?(s?bin|lib|libexec)/' | head -5)"
if [ -z "$suid" ]; then pass "no SUID binaries outside system directories"; else warn "SUID binaries outside system dirs: $(echo "$suid" | tr '\n' ' ')"; fi
home_perm="$(stat -c '%a' "$(getent passwd "$DEPLOY_USER" | cut -d: -f6)")"
if [ "${home_perm: -1}" = 0 ]; then pass "$DEPLOY_USER home not readable by others ($home_perm)"; else fail "$DEPLOY_USER home mode $home_perm"; fi

# ---------------------------------------------------------------------------------------------
if [ "$RUN_LYNIS" = 1 ]; then
    section "11. Lynis audit" \
"Lynis is an independent auditing tool with hundreds of tests. The hardening index is a 0-100 score; \
above 75 is good for a single-purpose server. Details: /var/log/lynis-report.dat."
    if command -v lynis >/dev/null; then
        lynis audit system --quick --no-colors >/dev/null 2>&1 || true
        idx="$(sed -n 's/^hardening_index=//p' /var/log/lynis-report.dat 2>/dev/null)"
        warnings="$(grep -c '^warning\[\]=' /var/log/lynis-report.dat 2>/dev/null || true)"
        if [ "${idx:-0}" -ge 75 ]; then pass "Lynis hardening index $idx"; else warn "Lynis hardening index ${idx:-?} (< 75)" "grep suggestion /var/log/lynis-report.dat"; fi
        if [ "${warnings:-0}" -eq 0 ]; then pass "Lynis reports no warnings"; else warn "Lynis reports $warnings warning(s)" "grep '^warning' /var/log/lynis-report.dat"; fi
    else
        warn "lynis not installed" "apt-get install lynis"
    fi
fi

summary
