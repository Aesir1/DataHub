#!/usr/bin/env bash
#
# One-shot provisioning and hardening of a fresh Ubuntu server (22.04 / 24.04 or newer) for the
# DataHub production stack (compose.prod.yaml + compose.observability.yaml).
#
# Run it once, as root, on a freshly created server. It asks questions, so it needs a terminal:
#
#   scp -r server-provision root@<host>:
#   ssh -t root@<host> 'bash server-provision/provision.sh'
#
# Safe to re-run: every step checks its own state first, and .env is never overwritten.
#
# WARNING: this switches SSH to key-only logins, disables root login and may move SSH to another
# port. Before it does that it checks that the admin user has a public key and asks for
# confirmation. Keep your root session open until a second session as the admin user works.
#
# Unattended use: every variable in the CONFIG block and every prompt can be given as an
# environment variable (ADMIN_PUBKEY, ADMIN_PASSWORD, DEPLOY_PUBKEY, APP_DOMAIN, AUTH_DOMAIN,
# HOOKS_DOMAIN, ACME_EMAIL, SSH_HOST), plus ASSUME_YES=1 to skip the confirmations.

set -euo pipefail

# ---------------------------------------------------------------------------------------------
# CONFIG (environment variables override these defaults)
# ---------------------------------------------------------------------------------------------
ADMIN_USER="${ADMIN_USER:-ops}"          # humans: sudo with a password, SSH key only
DEPLOY_USER="${DEPLOY_USER:-deploy}"     # GitHub Actions: docker group, restricted key, no sudo
APP_DIR_NAME="${APP_DIR_NAME:-datahub}"  # ~deploy/datahub holds .env, releases/ and current
SSH_PORT="${SSH_PORT:-22}"
AUTO_REBOOT="${AUTO_REBOOT:-yes}"        # reboot at 04:00 when a security update needs it
SWAP_SIZE_GB="${SWAP_SIZE_GB:-2}"
TIMEZONE="${TIMEZONE:-UTC}"

readonly DOCKER_FALLBACK_CODENAME=noble  # newest Ubuntu LTS Docker is known to publish for
readonly MARK="Managed by server-provision/provision.sh"

# ---------------------------------------------------------------------------------------------
# Output helpers
# ---------------------------------------------------------------------------------------------
if [ -t 1 ]; then
    B=$'\033[1m'; GREEN=$'\033[32m'; YELLOW=$'\033[33m'; RED=$'\033[31m'; R=$'\033[0m'
else
    B=; GREEN=; YELLOW=; RED=; R=
fi
step() { printf '\n%s==> %s%s\n' "$B" "$*" "$R"; }
ok()   { printf '    %s✓%s %s\n' "$GREEN" "$R" "$*"; }
skip() { printf '    %s·%s %s\n' "$YELLOW" "$R" "$*"; }
warn() { printf '    %s!%s %s\n' "$YELLOW" "$R" "$*"; }
die()  { printf '\n%serror:%s %s\n' "$RED" "$R" "$*" >&2; exit 1; }

ask() { # ask VAR "prompt" [default]; no-op when VAR is already set
    local var="$1" prompt="$2" default="${3:-}" reply=""
    [ -n "${!var:-}" ] && return 0
    if [ ! -t 0 ]; then
        [ -n "$default" ] || die "$var is not set and there is no terminal to ask on."
        printf -v "$var" '%s' "$default"
        return 0
    fi
    if [ -n "$default" ]; then
        read -r -p "    $prompt [$default]: " reply
        printf -v "$var" '%s' "${reply:-$default}"
    else
        while [ -z "$reply" ]; do read -r -p "    $prompt: " reply; done
        printf -v "$var" '%s' "$reply"
    fi
}

confirm() {
    local reply
    [ "${ASSUME_YES:-0}" = 1 ] && return 0
    [ -t 0 ] || die "confirmation needed but no terminal; set ASSUME_YES=1 for unattended runs."
    read -r -p "    $1 [y/N]: " reply
    [[ "$reply" =~ ^[Yy]$ ]]
}

# Alphanumeric only: values end up in connection strings and in CREATE ROLE ... PASSWORD '...'
# (docker/postgres-init), where quotes or semicolons would break parsing.
rand_alnum() { openssl rand -hex 48 | cut -c1-"${1:-32}"; }

valid_pubkey() { printf '%s\n' "$1" | ssh-keygen -l -f - >/dev/null 2>&1; }

# ---------------------------------------------------------------------------------------------
step "Preflight"
# ---------------------------------------------------------------------------------------------
[ "$(id -u)" -eq 0 ] || die "run as root"
# shellcheck disable=SC1091
. /etc/os-release
[ "${ID:-}" = ubuntu ] || warn "written for Ubuntu; found ID=${ID:-unknown}, continuing"
ok "${PRETTY_NAME:-unknown OS}, kernel $(uname -r)"
[[ "$SSH_PORT" =~ ^[0-9]+$ ]] && [ "$SSH_PORT" -ge 1 ] && [ "$SSH_PORT" -le 65535 ] || die "SSH_PORT must be 1-65535"
export DEBIAN_FRONTEND=noninteractive NEEDRESTART_MODE=a
# The SSH client forwards its own LC_* settings, which a fresh server has no locale for.
export LC_ALL=C.UTF-8

# ---------------------------------------------------------------------------------------------
step "Security patches and base packages"
# ---------------------------------------------------------------------------------------------
apt-get update -qq
apt-get -y -qq -o Dpkg::Options::=--force-confold full-upgrade
apt-get install -y -qq \
    ca-certificates curl gnupg jq rsync openssl \
    ufw fail2ban python3-systemd \
    unattended-upgrades apt-listchanges needrestart \
    auditd audispd-plugins apparmor apparmor-utils lynis
ok "system upgraded, base and security packages installed"
removed=()
for pkg in telnet telnetd ftp rsh-client rsh-server talk talkd nis xinetd; do
    if dpkg -s "$pkg" >/dev/null 2>&1; then apt-get purge -y -qq "$pkg"; removed+=("$pkg"); fi
done
apt-get autoremove -y -qq
if [ "${#removed[@]}" -gt 0 ]; then ok "removed legacy clear-text services: ${removed[*]}"; else skip "no legacy clear-text services installed"; fi

# ---------------------------------------------------------------------------------------------
step "Time"
# ---------------------------------------------------------------------------------------------
# Correct time matters for TLS, JWT lifetimes, webhook HMAC timestamps and log correlation.
timedatectl set-timezone "$TIMEZONE"
timedatectl set-ntp true
ok "timezone $TIMEZONE, NTP synchronisation on"

# ---------------------------------------------------------------------------------------------
step "Users"
# ---------------------------------------------------------------------------------------------
add_key() { # add_key user "key line"
    local home auth
    home="$(getent passwd "$1" | cut -d: -f6)"
    auth="$home/.ssh/authorized_keys"
    install -o "$1" -g "$1" -m 700 -d "$home/.ssh"
    touch "$auth"
    grep -qxF "$2" "$auth" || printf '%s\n' "$2" >> "$auth"
    chown "$1:$1" "$auth"
    chmod 600 "$auth"
}
key_count() {
    local f
    f="$(getent passwd "$1" | cut -d: -f6)/.ssh/authorized_keys"
    [ -f "$f" ] || { echo 0; return; }
    grep -c '^[^#[:space:]]' "$f" || true
}

# Admin: the account humans use. sudo requires its password, so a stolen SSH key alone is not root.
if id -u "$ADMIN_USER" >/dev/null 2>&1; then
    skip "admin user $ADMIN_USER exists"
else
    adduser --disabled-password --gecos "DataHub admin" "$ADMIN_USER" >/dev/null
    ok "created admin user $ADMIN_USER"
fi
usermod -aG sudo "$ADMIN_USER"
if [ "$(key_count "$ADMIN_USER")" -eq 0 ]; then
    if [ -z "${ADMIN_PUBKEY:-}" ] && [ -s /root/.ssh/authorized_keys ]; then
        printf '    No key given for %s; root has %s key(s).\n' "$ADMIN_USER" "$(grep -c '^[^#]' /root/.ssh/authorized_keys)"
        if confirm "copy root's authorized_keys to $ADMIN_USER?"; then
            ADMIN_PUBKEY="$(grep '^[^#]' /root/.ssh/authorized_keys)"
        fi
    fi
    ask ADMIN_PUBKEY "public SSH key for $ADMIN_USER (one line, from your own machine's ~/.ssh/*.pub)"
    while IFS= read -r line; do
        [ -z "$line" ] && continue
        valid_pubkey "$line" || die "not a valid SSH public key: ${line:0:40}..."
        add_key "$ADMIN_USER" "$line"
    done <<< "$ADMIN_PUBKEY"
    ok "$ADMIN_USER has $(key_count "$ADMIN_USER") key(s)"
else
    skip "$ADMIN_USER already has $(key_count "$ADMIN_USER") key(s)"
fi
if passwd -S "$ADMIN_USER" | awk '{exit ($2 == "P") ? 0 : 1}'; then
    skip "$ADMIN_USER already has a sudo password"
else
    if [ -z "${ADMIN_PASSWORD:-}" ]; then
        [ -t 0 ] || die "ADMIN_PASSWORD is not set and there is no terminal to ask on."
        while :; do
            read -r -s -p "    sudo password for $ADMIN_USER (min 14 characters): " ADMIN_PASSWORD; echo
            read -r -s -p "    repeat: " again; echo
            [ "$ADMIN_PASSWORD" = "$again" ] && [ "${#ADMIN_PASSWORD}" -ge 14 ] && break
            warn "passwords differ or are shorter than 14 characters"
        done
    fi
    printf '%s:%s\n' "$ADMIN_USER" "$ADMIN_PASSWORD" | chpasswd
    ok "sudo password set for $ADMIN_USER (it is never accepted over SSH)"
fi

# Deploy: GitHub Actions. docker group (needed for compose), no sudo, and a "restrict"ed key:
# no port/agent/X11 forwarding and no TTY, so a leaked deploy key cannot be used to tunnel.
if id -u "$DEPLOY_USER" >/dev/null 2>&1; then
    skip "deploy user $DEPLOY_USER exists"
else
    adduser --disabled-password --gecos "DataHub deploy (GitHub Actions)" "$DEPLOY_USER" >/dev/null
    ok "created deploy user $DEPLOY_USER"
fi
if [ "$(key_count "$DEPLOY_USER")" -eq 0 ]; then
    cat <<TXT

    GitHub Actions logs in as $DEPLOY_USER. Create a dedicated key pair on your own machine, so the
    private half never touches this server:

        ssh-keygen -t ed25519 -f ~/.ssh/datahub_deploy -C gha-deploy -N ""
        cat ~/.ssh/datahub_deploy.pub

TXT
    ask DEPLOY_PUBKEY "public deploy key (the one-line .pub)"
    valid_pubkey "$DEPLOY_PUBKEY" || die "not a valid SSH public key"
    add_key "$DEPLOY_USER" "restrict $DEPLOY_PUBKEY"
    ok "installed the deploy key with the 'restrict' option"
else
    skip "$DEPLOY_USER already has $(key_count "$DEPLOY_USER") key(s)"
fi

passwd -l root >/dev/null
ok "root password locked (console and su to root need sudo now)"

# Only members of sudo may use su at all.
if ! grep -qE '^auth\s+required\s+pam_wheel.so.*group=sudo' /etc/pam.d/su; then
    sed -i '0,/^#\s*auth\s\+required\s\+pam_wheel.so\s*$/s//auth       required   pam_wheel.so use_uid group=sudo/' /etc/pam.d/su
    grep -qE '^auth\s+required\s+pam_wheel.so' /etc/pam.d/su ||
        printf 'auth       required   pam_wheel.so use_uid group=sudo\n' >> /etc/pam.d/su
fi
ok "su restricted to the sudo group"

cat > /etc/sudoers.d/10-hardening <<CONF
# $MARK
Defaults use_pty
Defaults logfile="/var/log/sudo.log"
Defaults timestamp_timeout=5
Defaults passwd_tries=3
CONF
chmod 440 /etc/sudoers.d/10-hardening
visudo -cq || die "sudoers syntax error in /etc/sudoers.d/10-hardening"
ok "sudo: own pty, logged to /var/log/sudo.log, 5 minute timeout"

# ---------------------------------------------------------------------------------------------
step "Firewall (ufw)"
# ---------------------------------------------------------------------------------------------
# The SSH rule goes in before ufw is enabled, or this session would be cut off.
ufw default deny incoming >/dev/null
ufw default allow outgoing >/dev/null
ufw default deny routed >/dev/null
ufw limit "$SSH_PORT"/tcp comment 'ssh (rate limited)' >/dev/null
ufw allow 80/tcp comment 'http: ACME challenge, redirect to https' >/dev/null
ufw allow 443/tcp comment 'https' >/dev/null
ufw logging low >/dev/null
# ufw applies its own sysctl file after systemd-sysctl on every boot; it would switch martian
# logging off again.
sed -i -E 's#^(net/ipv4/conf/(all|default)/log_martians)=0#\1=1#' /etc/ufw/sysctl.conf

# Docker publishes ports through its own iptables chains, which bypass ufw's INPUT rules. The
# DOCKER-USER chain is evaluated first for traffic to containers: allow 80/443, drop any other
# new connection coming in from the public interface. Belt and braces with daemon.json's
# "ip": "127.0.0.1" below, which already makes a forgotten port binding loopback-only.
EXT_IF="$(ip route get 1.1.1.1 2>/dev/null | awk '{for (i = 1; i < NF; i++) if ($i == "dev") { print $(i + 1); exit }}')"
[ -n "$EXT_IF" ] || die "cannot determine the public network interface"
if grep -q 'BEGIN datahub-docker-user' /etc/ufw/after.rules; then
    sed -i '/BEGIN datahub-docker-user/,/END datahub-docker-user/d' /etc/ufw/after.rules
fi
cat >> /etc/ufw/after.rules <<RULES
# BEGIN datahub-docker-user ($MARK)
*filter
:DOCKER-USER - [0:0]
-A DOCKER-USER -m conntrack --ctstate RELATED,ESTABLISHED -j RETURN
-A DOCKER-USER -i $EXT_IF -p tcp -m conntrack --ctorigdstport 80 --ctdir ORIGINAL -j RETURN
-A DOCKER-USER -i $EXT_IF -p tcp -m conntrack --ctorigdstport 443 --ctdir ORIGINAL -j RETURN
-A DOCKER-USER -i $EXT_IF -m conntrack --ctstate NEW,INVALID -j DROP
-A DOCKER-USER -j RETURN
COMMIT
# END datahub-docker-user
RULES
ufw --force enable >/dev/null
ufw reload >/dev/null
ok "incoming: $SSH_PORT/tcp (rate limited), 80/tcp, 443/tcp; everything else dropped"
ok "containers: only 80/443 reachable from $EXT_IF (DOCKER-USER chain)"

# ---------------------------------------------------------------------------------------------
step "SSH hardening"
# ---------------------------------------------------------------------------------------------
SSHD_DROPIN=/etc/ssh/sshd_config.d/00-datahub-hardening.conf
[ "$(key_count "$ADMIN_USER")" -gt 0 ] || die "$ADMIN_USER has no SSH key; refusing to lock SSH down"
[ "$(key_count "$DEPLOY_USER")" -gt 0 ] || die "$DEPLOY_USER has no SSH key; refusing to lock SSH down"

# Weak RSA host key (< 3072 bits) -> regenerate. DSA/ECDSA host keys are not offered at all.
if [ -f /etc/ssh/ssh_host_rsa_key ] && [ "$(ssh-keygen -l -f /etc/ssh/ssh_host_rsa_key | awk '{print $1}')" -lt 3072 ]; then
    rm -f /etc/ssh/ssh_host_rsa_key /etc/ssh/ssh_host_rsa_key.pub
    ssh-keygen -q -t rsa -b 4096 -N "" -f /etc/ssh/ssh_host_rsa_key
    ok "regenerated the RSA host key with 4096 bits"
fi
[ -f /etc/ssh/ssh_host_ed25519_key ] || ssh-keygen -q -t ed25519 -N "" -f /etc/ssh/ssh_host_ed25519_key

cat > /etc/issue.net <<'TXT'
Authorized access only. All activity on this system is logged and monitored.
Disconnect now if you are not an authorized user.
TXT

NEW_SSHD="$(mktemp)"
# 00- on purpose: sshd uses the FIRST value it reads for most keywords, sshd_config includes this
# directory at the top, and cloud images ship 50-cloud-init.conf with "PasswordAuthentication yes".
cat > "$NEW_SSHD" <<CONF
# $MARK
Port $SSH_PORT
HostKey /etc/ssh/ssh_host_ed25519_key
HostKey /etc/ssh/ssh_host_rsa_key

# Who and how: keys only, two named accounts, never root.
AllowUsers $ADMIN_USER $DEPLOY_USER
PermitRootLogin no
PubkeyAuthentication yes
AuthenticationMethods publickey
PasswordAuthentication no
KbdInteractiveAuthentication no
PermitEmptyPasswords no
PermitUserEnvironment no
HostbasedAuthentication no
IgnoreRhosts yes

# Brute force and resource limits.
MaxAuthTries 3
MaxSessions 4
MaxStartups 10:30:60
LoginGraceTime 20
ClientAliveInterval 300
ClientAliveCountMax 2

# Forwarding: only local forwards (ssh -L, used for the Grafana and Keycloak admin tunnels).
AllowTcpForwarding local
AllowAgentForwarding no
AllowStreamLocalForwarding no
X11Forwarding no
GatewayPorts no
PermitTunnel no

# Modern algorithms only (no SHA-1, CBC, 3DES or small DH groups).
KexAlgorithms sntrup761x25519-sha512@openssh.com,curve25519-sha256,curve25519-sha256@libssh.org
Ciphers chacha20-poly1305@openssh.com,aes256-gcm@openssh.com,aes128-gcm@openssh.com
MACs hmac-sha2-512-etm@openssh.com,hmac-sha2-256-etm@openssh.com
HostKeyAlgorithms ssh-ed25519,rsa-sha2-512,rsa-sha2-256
PubkeyAcceptedAlgorithms ssh-ed25519,sk-ssh-ed25519@openssh.com,rsa-sha2-512,rsa-sha2-256

LogLevel VERBOSE
Banner /etc/issue.net
DebianBanner no
CONF

if [ -f "$SSHD_DROPIN" ] && cmp -s "$NEW_SSHD" "$SSHD_DROPIN"; then
    skip "$SSHD_DROPIN is up to date"
    rm -f "$NEW_SSHD"
else
    cat <<TXT

    About to apply the SSH configuration:
      port $SSH_PORT, key-only, root login disabled, AllowUsers $ADMIN_USER $DEPLOY_USER
      $ADMIN_USER has $(key_count "$ADMIN_USER") key(s), $DEPLOY_USER has $(key_count "$DEPLOY_USER") key(s)

    Keep THIS session open and test a new one afterwards:  ssh -p $SSH_PORT $ADMIN_USER@<host>
TXT
    confirm "apply the SSH hardening now?" || die "stopped before the SSH change; nothing after this step ran"
    [ -f "$SSHD_DROPIN" ] && cp "$SSHD_DROPIN" "$SSHD_DROPIN.bak"
    install -m 644 "$NEW_SSHD" "$SSHD_DROPIN"
    rm -f "$NEW_SSHD"
    for f in /etc/ssh/sshd_config.d/*.conf; do
        [ "$f" = "$SSHD_DROPIN" ] && continue
        sed -i -E 's/^[[:space:]]*(PasswordAuthentication|PermitRootLogin|KbdInteractiveAuthentication)/# disabled by provision.sh: &/I' "$f"
    done
    if ! sshd -t; then
        if [ -f "$SSHD_DROPIN.bak" ]; then mv "$SSHD_DROPIN.bak" "$SSHD_DROPIN"; else rm -f "$SSHD_DROPIN"; fi
        die "sshd rejected the configuration; restored the previous one, nothing restarted"
    fi
    rm -f "$SSHD_DROPIN.bak"
    # Ubuntu 24.04+ starts sshd from ssh.socket; its generator turns "Port" into the socket's
    # ListenStream, so a port change needs daemon-reload plus a socket restart.
    systemctl daemon-reload
    if systemctl is-active --quiet ssh.socket; then
        systemctl restart ssh.socket
        systemctl restart ssh.service 2>/dev/null || true
    else
        systemctl restart ssh 2>/dev/null || systemctl restart sshd
    fi
    ok "SSH hardened. Open a SECOND session now to verify before closing this one."
fi
if [ "$SSH_PORT" != 22 ] && ufw status | grep -qE '^22/tcp'; then
    ufw delete limit 22/tcp >/dev/null 2>&1 || true
    ok "closed the old SSH port 22 in ufw"
fi

# ---------------------------------------------------------------------------------------------
step "fail2ban"
# ---------------------------------------------------------------------------------------------
# backend = systemd: Ubuntu has no /var/log/auth.log without rsyslog, so "auto" would watch nothing.
cat > /etc/fail2ban/jail.local <<CONF
# $MARK
[DEFAULT]
backend = systemd
banaction = ufw
bantime = 1h
bantime.increment = true
bantime.maxtime = 1w
findtime = 10m
maxretry = 5
ignoreip = 127.0.0.1/8 ::1

[sshd]
enabled = true
mode = aggressive
port = $SSH_PORT

# Hosts banned again and again are banned for a week.
[recidive]
enabled = true
backend = auto
logpath = /var/log/fail2ban.log
bantime = 1w
findtime = 1d
maxretry = 3
CONF
systemctl enable fail2ban >/dev/null 2>&1
systemctl restart fail2ban
if systemctl is-active --quiet fail2ban; then ok "fail2ban active: sshd (aggressive) and recidive jails"; else warn "fail2ban did not start: systemctl status fail2ban"; fi

# ---------------------------------------------------------------------------------------------
step "Kernel and network hardening (sysctl)"
# ---------------------------------------------------------------------------------------------
# net.ipv4.ip_forward is deliberately left alone: Docker needs it.
cat > /etc/sysctl.d/60-datahub-hardening.conf <<CONF
# $MARK
# Spoofing, redirects and source routing
net.ipv4.conf.all.rp_filter = 1
net.ipv4.conf.default.rp_filter = 1
net.ipv4.conf.all.accept_redirects = 0
net.ipv4.conf.default.accept_redirects = 0
net.ipv4.conf.all.secure_redirects = 0
net.ipv4.conf.default.secure_redirects = 0
net.ipv4.conf.all.send_redirects = 0
net.ipv4.conf.default.send_redirects = 0
net.ipv4.conf.all.accept_source_route = 0
net.ipv4.conf.default.accept_source_route = 0
net.ipv6.conf.all.accept_redirects = 0
net.ipv6.conf.default.accept_redirects = 0
net.ipv6.conf.all.accept_source_route = 0
net.ipv6.conf.default.accept_source_route = 0
net.ipv4.conf.all.log_martians = 1
net.ipv4.conf.default.log_martians = 1
net.ipv4.icmp_echo_ignore_broadcasts = 1
net.ipv4.icmp_ignore_bogus_error_responses = 1
# SYN flood and TIME-WAIT assassination
net.ipv4.tcp_syncookies = 1
net.ipv4.tcp_rfc1337 = 1
# Kernel information leaks and attack surface
kernel.kptr_restrict = 2
kernel.dmesg_restrict = 1
kernel.unprivileged_bpf_disabled = 1
net.core.bpf_jit_harden = 2
kernel.yama.ptrace_scope = 1
kernel.kexec_load_disabled = 1
kernel.sysrq = 0
kernel.perf_event_paranoid = 3
kernel.randomize_va_space = 2
dev.tty.ldisc_autoload = 0
vm.unprivileged_userfaultfd = 0
# Filesystem
fs.protected_hardlinks = 1
fs.protected_symlinks = 1
fs.protected_fifos = 2
fs.protected_regular = 2
fs.suid_dumpable = 0
CONF
sysctl -q --system >/dev/null 2>&1 || warn "some sysctl keys are unknown to this kernel: sysctl --system | grep -i error"
ok "sysctl hardening applied"

# No core dumps: they can contain secrets (connection strings, tokens) from process memory.
# apport (Ubuntu's crash reporter) sets fs.suid_dumpable=2 at every boot and collects dumps.
if systemctl list-unit-files apport.service >/dev/null 2>&1; then
    systemctl disable --now apport >/dev/null 2>&1 || true
    [ -f /etc/default/apport ] && sed -i 's/^enabled=1/enabled=0/' /etc/default/apport
fi
printf '# %s\n* hard core 0\n' "$MARK" > /etc/security/limits.d/60-no-core.conf
install -d /etc/systemd/coredump.conf.d
printf '# %s\n[Coredump]\nStorage=none\nProcessSizeMax=0\n' "$MARK" > /etc/systemd/coredump.conf.d/60-disable.conf
ok "core dumps disabled"

# Rarely used protocols and filesystems: a long history of kernel CVEs, no use on this server.
{
    printf '# %s\n' "$MARK"
    for m in dccp sctp rds tipc n-hdlc ax25 netrom x25 rose decnet econet af_802154 ipx appletalk psnap p8022 p8023 \
             cramfs freevxfs jffs2 hfs hfsplus udf firewire-core thunderbolt usb-storage; do
        printf 'install %s /bin/false\nblacklist %s\n' "$m" "$m"
    done
} > /etc/modprobe.d/60-datahub-blacklist.conf
ok "unused kernel modules blacklisted"

# /dev/shm without exec/suid/dev: a classic place to drop and run payloads.
if ! grep -qE '^\S+\s+/dev/shm\s' /etc/fstab; then
    printf 'tmpfs /dev/shm tmpfs defaults,noexec,nosuid,nodev 0 0\n' >> /etc/fstab
fi
mount -o remount,noexec,nosuid,nodev /dev/shm
ok "/dev/shm mounted noexec,nosuid,nodev"

# ---------------------------------------------------------------------------------------------
step "Mandatory access control and auditing"
# ---------------------------------------------------------------------------------------------
systemctl enable --now apparmor >/dev/null 2>&1 || true
if aa-status --enabled 2>/dev/null; then ok "AppArmor enabled ($(aa-status --profiled 2>/dev/null || echo '?') profiles loaded)"; else warn "AppArmor is not enabled (kernel boot parameter?)"; fi

cat > /etc/audit/rules.d/60-datahub.rules <<CONF
# $MARK
# Who changed accounts, sudo, SSH, the firewall, Docker or the stack's secrets.
-w /etc/passwd -p wa -k identity
-w /etc/group -p wa -k identity
-w /etc/shadow -p wa -k identity
-w /etc/gshadow -p wa -k identity
-w /etc/sudoers -p wa -k sudoers
-w /etc/sudoers.d/ -p wa -k sudoers
-w /etc/ssh/sshd_config -p wa -k sshd
-w /etc/ssh/sshd_config.d/ -p wa -k sshd
-w /etc/ufw/ -p wa -k firewall
-w /etc/docker/ -p wa -k docker
-w /usr/bin/docker -p x -k docker
-w /etc/sysctl.conf -p wa -k sysctl
-w /etc/sysctl.d/ -p wa -k sysctl
-w /etc/cron.d/ -p wa -k cron
-w /var/spool/cron/ -p wa -k cron
-w /var/log/sudo.log -p wa -k sudo-log
# Kernel modules and clock changes.
-a always,exit -F arch=b64 -S init_module,finit_module,delete_module -k modules
-a always,exit -F arch=b64 -S adjtimex,settimeofday,clock_settime -k time-change
CONF
systemctl enable --now auditd >/dev/null 2>&1 || true
augenrules --load >/dev/null 2>&1 || warn "augenrules failed; check /etc/audit/rules.d/60-datahub.rules"
ok "auditd running with $(auditctl -l 2>/dev/null | wc -l) rules (search: ausearch -k <key>)"

# ---------------------------------------------------------------------------------------------
step "Automatic security updates"
# ---------------------------------------------------------------------------------------------
cat > /etc/apt/apt.conf.d/51datahub-unattended <<CONF
// $MARK
APT::Periodic::Update-Package-Lists "1";
APT::Periodic::Unattended-Upgrade "1";
APT::Periodic::AutocleanInterval "7";
Unattended-Upgrade::Remove-Unused-Kernel-Packages "true";
Unattended-Upgrade::Remove-Unused-Dependencies "true";
Unattended-Upgrade::Automatic-Reboot "$([ "$AUTO_REBOOT" = yes ] && echo true || echo false)";
Unattended-Upgrade::Automatic-Reboot-Time "04:00";
CONF
# needrestart: restart services using outdated libraries automatically, never prompt.
install -d /etc/needrestart/conf.d
printf "# %s\n\$nrconf{restart} = 'a';\n" "$MARK" > /etc/needrestart/conf.d/60-datahub.conf
systemctl enable --now unattended-upgrades >/dev/null 2>&1 || true
ok "security updates install daily$([ "$AUTO_REBOOT" = yes ] && echo ', reboot at 04:00 when required')"

# ---------------------------------------------------------------------------------------------
step "Swap"
# ---------------------------------------------------------------------------------------------
if [ -n "$(swapon --show --noheadings 2>/dev/null)" ]; then
    skip "swap already configured"
elif [ "$SWAP_SIZE_GB" = 0 ]; then
    skip "SWAP_SIZE_GB=0"
else
    # The stack (Postgres, Keycloak, RabbitMQ, MinIO, .NET, Next.js, Grafana stack) wants >= 4 GB RAM;
    # swap is a cushion against the OOM killer, not a substitute.
    avail_gb=$(df --output=avail -BG / | tail -1 | tr -dc '0-9')
    if [ "$avail_gb" -lt $((SWAP_SIZE_GB + 5)) ]; then
        warn "only ${avail_gb}G free on /; skipping swap"
    else
        fallocate -l "${SWAP_SIZE_GB}G" /swapfile || dd if=/dev/zero of=/swapfile bs=1M count=$((SWAP_SIZE_GB * 1024)) status=none
        chmod 600 /swapfile
        mkswap /swapfile >/dev/null
        swapon /swapfile
        grep -q '^/swapfile' /etc/fstab || echo '/swapfile none swap sw 0 0' >> /etc/fstab
        printf 'vm.swappiness = 10\n' > /etc/sysctl.d/61-swappiness.conf
        sysctl -q --system
        ok "${SWAP_SIZE_GB}G swapfile, swappiness 10"
    fi
fi

# ---------------------------------------------------------------------------------------------
step "Docker Engine"
# ---------------------------------------------------------------------------------------------
if command -v docker >/dev/null 2>&1 && docker compose version >/dev/null 2>&1; then
    skip "$(docker --version), compose $(docker compose version --short)"
else
    install -m 0755 -d /etc/apt/keyrings
    curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc
    chmod a+r /etc/apt/keyrings/docker.asc
    codename="${UBUNTU_CODENAME:-${VERSION_CODENAME:-}}"
    if ! curl -fsI --max-time 15 "https://download.docker.com/linux/ubuntu/dists/$codename/Release" >/dev/null 2>&1; then
        warn "Docker publishes nothing for '$codename' yet; using '$DOCKER_FALLBACK_CODENAME'"
        codename="$DOCKER_FALLBACK_CODENAME"
    fi
    printf 'deb [arch=%s signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/ubuntu %s stable\n' \
        "$(dpkg --print-architecture)" "$codename" > /etc/apt/sources.list.d/docker.list
    apt-get update -qq
    apt-get install -y -qq docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
    ok "$(docker --version), compose $(docker compose version --short)"
fi

# Merged into an existing daemon.json, never replacing unrelated settings.
#   ip 127.0.0.1         a port published without an address ("9000:9000") binds to loopback only;
#                        the public 80/443 are published explicitly on 0.0.0.0 (HTTP_PORT/HTTPS_PORT)
#   no-new-privileges    setuid binaries inside containers cannot gain privileges
#   icc false            no traffic between containers on the default bridge (compose uses its own)
#   log caps             container logs cannot fill the disk (Alloy reads json-file logs)
#   live-restore         containers keep running while dockerd restarts (updates)
install -m 0755 -d /etc/docker
DESIRED='{"ip":"127.0.0.1","no-new-privileges":true,"icc":false,"live-restore":true,"log-driver":"json-file","log-opts":{"max-size":"10m","max-file":"3"}}'
CURRENT="$( [ -s /etc/docker/daemon.json ] && cat /etc/docker/daemon.json || echo '{}')"
MERGED="$(jq -S -n --argjson a "$CURRENT" --argjson b "$DESIRED" '$a * $b')"
if [ "$(jq -S . <<< "$CURRENT")" = "$MERGED" ]; then
    skip "daemon.json already hardened"
else
    printf '%s\n' "$MERGED" > /etc/docker/daemon.json
    systemctl restart docker
    ok "daemon.json: loopback default bind, no-new-privileges, icc off, log caps, live-restore"
fi
systemctl enable --now docker >/dev/null 2>&1 || true

usermod -aG docker "$DEPLOY_USER"
ok "$DEPLOY_USER is in the docker group"
warn "docker group membership is root-equivalent. Only the restricted deploy key can log in as $DEPLOY_USER."

# ---------------------------------------------------------------------------------------------
step "Application directory and .env"
# ---------------------------------------------------------------------------------------------
DEPLOY_HOME="$(getent passwd "$DEPLOY_USER" | cut -d: -f6)"
APP_DIR="$DEPLOY_HOME/$APP_DIR_NAME"
install -o "$DEPLOY_USER" -g "$DEPLOY_USER" -m 750 -d "$APP_DIR" "$APP_DIR/releases"
chmod 750 "$DEPLOY_HOME"
# Every read of the secrets file is audited: ausearch -k datahub-secrets
printf -- '-w %s/.env -p rwa -k datahub-secrets\n' "$APP_DIR" > /etc/audit/rules.d/61-datahub-secrets.rules
augenrules --load >/dev/null 2>&1 || warn "augenrules failed for 61-datahub-secrets.rules"
ok "$APP_DIR (releases/ plus current -> releases/<sha>, created by the first deploy)"

ENV_FILE="$APP_DIR/.env"
if [ -s "$ENV_FILE" ]; then
    skip ".env exists; not touching it (it holds the live credentials)"
else
    ask APP_DOMAIN   "web app domain (e.g. app.example.com)"
    ask AUTH_DOMAIN  "Keycloak domain" "auth.${APP_DOMAIN#*.}"
    ask HOOKS_DOMAIN "webhook domain" "hooks.${APP_DOMAIN#*.}"
    ask ACME_EMAIL   "e-mail for Let's Encrypt (certificate expiry notices)"
    umask 077
    cat > "$ENV_FILE" <<CONF
# Generated by server-provision/provision.sh on $(date -Iseconds). Never commit or copy this file.
# Keys mirror .env.example; release.sh refuses to deploy when a key from .env.example is missing here.

APP_DOMAIN=$APP_DOMAIN
AUTH_DOMAIN=$AUTH_DOMAIN
HOOKS_DOMAIN=$HOOKS_DOMAIN
CADDY_TLS=$ACME_EMAIL
WEB_EXTRA_CA_CERTS=
DEPLOYMENT_ENVIRONMENT=production
# Explicit 0.0.0.0: dockerd binds everything else to 127.0.0.1 (daemon.json "ip").
HTTP_PORT=0.0.0.0:80
HTTPS_PORT=0.0.0.0:443

# Set per release by release.sh (the workflow passes the registry path and commit SHA).
IMAGE_REPO=
IMAGE_TAG=

POSTGRES_USER=datahub
POSTGRES_PASSWORD=$(rand_alnum 40)
APP_DB_USER=datahub_app
APP_DB_PASSWORD=$(rand_alnum 40)
KEYCLOAK_DB_USER=keycloak
KEYCLOAK_DB_PASSWORD=$(rand_alnum 40)
PG_EXPORTER_USER=datahub_metrics
PG_EXPORTER_PASSWORD=$(rand_alnum 40)

KEYCLOAK_ADMIN_USER=admin
KEYCLOAK_ADMIN_PASSWORD=$(rand_alnum 32)
KEYCLOAK_ADMIN_BIND=127.0.0.1:8081
KEYCLOAK_ADMIN_URL=http://localhost:8081
WEB_CLIENT_SECRET=$(rand_alnum 48)
WEBHOOK_CLIENT_SECRET=$(rand_alnum 48)

AUTH_SECRET=$(rand_alnum 64)

WEBHOOK_HMAC_SECRET=$(rand_alnum 64)
WEBHOOK_HMAC_SECRET_PREVIOUS=

RABBITMQ_USER=datahub
RABBITMQ_PASSWORD=$(rand_alnum 40)

MINIO_ROOT_USER=minioadmin
MINIO_ROOT_PASSWORD=$(rand_alnum 40)
API_S3_ACCESS_KEY=datahub-api
API_S3_SECRET_KEY=$(rand_alnum 40)
WEBHOOK_S3_ACCESS_KEY=datahub-webhook
WEBHOOK_S3_SECRET_KEY=$(rand_alnum 40)

GRAFANA_ADMIN_USER=admin
GRAFANA_ADMIN_PASSWORD=$(rand_alnum 32)
GRAFANA_BIND=127.0.0.1:3300
GRAFANA_ROOT_URL=http://localhost:3300
PROMETHEUS_RETENTION=30d
PROMETHEUS_RETENTION_SIZE=2GB
CONF
    chown "$DEPLOY_USER:$DEPLOY_USER" "$ENV_FILE"
    chmod 600 "$ENV_FILE"
    umask 022
    ok "wrote $ENV_FILE (mode 600) with freshly generated secrets"
    printf '    Admin passwords (Keycloak, Grafana, MinIO) are in that file: sudo cat %s\n' "$ENV_FILE"
fi
env_value() { sed -n "s/^$1=//p" "$ENV_FILE" | tail -1; }
APP_DOMAIN="$(env_value APP_DOMAIN)"
AUTH_DOMAIN="$(env_value AUTH_DOMAIN)"
HOOKS_DOMAIN="$(env_value HOOKS_DOMAIN)"

# ---------------------------------------------------------------------------------------------
step "GitHub repository settings"
# ---------------------------------------------------------------------------------------------
PUBLIC_IP4="$(curl -4fsS --max-time 5 https://api.ipify.org 2>/dev/null || ip -4 -o addr show scope global | awk '{print $4}' | cut -d/ -f1 | head -1)"
SSH_HOST="${SSH_HOST:-}"
ask SSH_HOST "host name or IP GitHub Actions connects to" "$PUBLIC_IP4"
KNOWN_HOSTS=""
for t in ed25519 rsa; do
    kf="/etc/ssh/ssh_host_${t}_key.pub"
    [ -r "$kf" ] || continue
    if [ "$SSH_PORT" = 22 ]; then host="$SSH_HOST"; else host="[$SSH_HOST]:$SSH_PORT"; fi
    KNOWN_HOSTS+="$host $(cut -d' ' -f1,2 "$kf")"$'\n'
done
resolved() { getent ahostsv4 "$1" 2>/dev/null | awk '{print $1; exit}'; }

cat <<TXT

    ${B}Settings -> Environments -> production${R} (create it; add yourself as required reviewer if
    every deploy should be approved)

      Secrets
        SSH_HOST         $SSH_HOST
        SSH_PORT         $SSH_PORT
        SSH_USER         $DEPLOY_USER
        SSH_KEY          the PRIVATE deploy key from your machine (~/.ssh/datahub_deploy), whole file
        SSH_KNOWN_HOSTS  exactly these lines:
$(printf '%s' "$KNOWN_HOSTS" | sed 's/^/                         /')
      Variables
        APP_DOMAIN       $APP_DOMAIN
        AUTH_DOMAIN      $AUTH_DOMAIN
        HOOKS_DOMAIN     $HOOKS_DOMAIN

    ${B}DNS${R} (A records -> $PUBLIC_IP4), needed before the first deploy (ACME challenge):
      $APP_DOMAIN    now resolves to ${B}$(resolved "$APP_DOMAIN" || true)${R}
      $AUTH_DOMAIN   now resolves to ${B}$(resolved "$AUTH_DOMAIN" || true)${R}
      $HOOKS_DOMAIN  now resolves to ${B}$(resolved "$HOOKS_DOMAIN" || true)${R}
    Add no AAAA records: the stack publishes 80/443 on IPv4 only.

    ${B}Next${R}
      1. From your machine, in a NEW terminal:  ssh -p $SSH_PORT $ADMIN_USER@$SSH_HOST   then  sudo -v
      2. Only then close this root session. Root can no longer log in over SSH.
      3. Run the host checks:  sudo bash server-provision/tests/test-host.sh
      4. Configure GitHub as above and run the deploy workflow (see server-provision/README.md).
TXT
[ -f /var/run/reboot-required ] && warn "a reboot is required for the installed kernel/library updates: sudo reboot (after step 1)"
step "Done"
