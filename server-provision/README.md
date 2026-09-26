# Server provisioning, deployment and security tests

This folder turns a freshly created Ubuntu server into a hardened host for the DataHub production stack
(`compose.prod.yaml` + `compose.observability.yaml`). It also holds the release script that the GitHub
workflows use to deploy and roll back, and the scripts that check the security features are in place.

```
server-provision/
├── provision.sh             run once as root on the new server: hardening, Docker, users, .env
├── release.sh               run by the workflows (as deploy): activate / rollback / restore / status
└── tests/
    ├── test-host.sh         on the server (root): OS hardening
    ├── test-containers.sh   on the server: Docker networks, ports, isolation, credentials
    ├── test-external.sh     from another machine: what the Internet sees
    ├── test-smoke.sh        from anywhere: are the three public sites up?
    └── lib.sh               shared output helpers
.github/workflows/
├── deploy.yml               ci green on main -> images -> release -> smoke test -> security check
└── rollback.yml             manual: back to the previous (or any earlier) release
```

## What the server looks like afterwards

| Layer | Protection |
| --- | --- |
| Patches | Full upgrade now; unattended security updates daily, reboot at 04:00 when a kernel needs it; `needrestart` restarts services on outdated libraries |
| Accounts | `ops` (humans): SSH key plus a sudo password. `deploy` (GitHub Actions): docker group, no sudo, key restricted with `restrict` (no forwarding, no TTY). Root: no SSH, password locked. `su` only for the sudo group. sudo is logged to `/var/log/sudo.log` |
| SSH | Keys only, `AllowUsers ops deploy`, no root, 3 attempts, modern algorithms only (no SHA-1/CBC), no agent/X11 forwarding, local forwarding only (for the admin tunnels), OS not in the banner, login banner |
| Firewall | ufw: inbound only SSH (rate limited), 80, 443. Docker bypasses ufw, so the `DOCKER-USER` chain drops every other new connection to containers, and `daemon.json` binds any port published without an address to 127.0.0.1 |
| Brute force | fail2ban: `sshd` jail (aggressive mode) with increasing ban times, `recidive` jail bans repeat offenders for a week |
| Kernel | sysctl: anti-spoofing, no redirects/source routing, SYN cookies, `kptr_restrict`, `dmesg_restrict`, no unprivileged BPF, ptrace limited, protected links. No core dumps. Unused protocols and filesystems blacklisted. `/dev/shm` noexec |
| MAC and audit | AppArmor enabled; auditd records changes to accounts, sudo, SSH, firewall, Docker, cron, kernel modules, the clock and every access to the `.env` file |
| Docker | log caps, `no-new-privileges` for every container, `icc` off on the default bridge, live-restore, loopback default bind |
| Stack | Only Caddy is reachable (80/443). Api, PostgreSQL, RabbitMQ and MinIO sit on internal networks without a route out; Grafana and the Keycloak admin console listen on 127.0.0.1 and are reached over SSH tunnels. The Keycloak master realm is not published |
| Secrets | Generated on the server (`~deploy/datahub/.env`, mode 600, never in git or in GitHub) |

## Step by step

### 0. Before you start

- An Ubuntu 22.04 or 24.04 (or newer) server with at least 4 GB RAM, 2 vCPUs and 40 GB disk, reachable
  as `root` over SSH (with a key or the provider's initial password).
- Three DNS names, for example `app.example.com`, `auth.example.com`, `hooks.example.com`, as **A records**
  pointing to the server's public IPv4 address. Add no AAAA records: the stack publishes IPv4 only.
- Two SSH key pairs on **your own machine** (the private halves never go to the server):

  ```bash
  ssh-keygen -t ed25519 -f ~/.ssh/datahub_admin  -C "you@datahub"   # you (user ops)
  ssh-keygen -t ed25519 -f ~/.ssh/datahub_deploy -C gha-deploy -N "" # GitHub Actions (user deploy)
  ```

- The GitHub repository with this code, and permission to create environments and secrets.

### 1. Provision the server (once)

From the repository root on your machine:

```bash
scp -r server-provision root@<server-ip>:
ssh -t root@<server-ip> 'bash server-provision/provision.sh'
```

The script asks for:

| Prompt | Answer |
| --- | --- |
| public SSH key for `ops` | contents of `~/.ssh/datahub_admin.pub` (or accept copying root's keys) |
| sudo password for `ops` | a long password (14+ characters); only asked for by `sudo`, never accepted by SSH |
| public deploy key | contents of `~/.ssh/datahub_deploy.pub` |
| confirmation before the SSH change | `y` once the numbers of keys shown are right |
| web app / Keycloak / webhook domain, Let's Encrypt e-mail | your three DNS names and an address for expiry notices |
| host GitHub connects to | the server's IP or host name |

Settings you can change with environment variables (put them before `bash`): `SSH_PORT` (default 22),
`ADMIN_USER` (ops), `DEPLOY_USER` (deploy), `AUTO_REBOOT` (yes), `SWAP_SIZE_GB` (2), `TIMEZONE` (UTC).
Example: `ssh -t root@<ip> 'SSH_PORT=2222 bash server-provision/provision.sh'`.

The script is idempotent; run it again after changing it. It never overwrites an existing `.env`.

At the end it prints the values for GitHub (step 3). **Keep the root session open** and continue with step 2.

### 2. Verify access, then close the root session

In a new terminal on your machine:

```bash
ssh -i ~/.ssh/datahub_admin -p 22 ops@<server-ip>
sudo -v                                   # asks for the ops password
sudo bash server-provision/tests/test-host.sh
```

Only when this works, close the root session. From now on root cannot log in over SSH. If the
provisioning printed "a reboot is required", run `sudo reboot` now.

`test-host.sh` checks everything the provisioning did. At this point the stack is not deployed yet, so
only section 6 might mention fewer listeners; everything should be PASS. (The files in
`~ops/server-provision` are only needed for this check; later runs use the copy shipped with each release.)

### 3. Configure GitHub

Repository -> Settings -> Environments -> **New environment** `production`. Recommended: add yourself
as a required reviewer, so every deploy waits for an approval, and limit deployment branches to `main`.

| Kind | Name | Value |
| --- | --- | --- |
| Secret | `SSH_HOST` | server IP or host name (as printed) |
| Secret | `SSH_PORT` | SSH port (as printed) |
| Secret | `SSH_USER` | `deploy` |
| Secret | `SSH_KEY` | the whole **private** key file `~/.ssh/datahub_deploy` |
| Secret | `SSH_KNOWN_HOSTS` | the host key lines printed by provision.sh (pins the server's identity) |
| Variable | `APP_DOMAIN`, `AUTH_DOMAIN`, `HOOKS_DOMAIN` | the three DNS names |
| Variable (optional, repository level) | `SERVER_PLATFORMS` | `linux/arm64` for an ARM server (default `linux/amd64`) |

Images are pushed to GitHub Container Registry (`ghcr.io/<owner>/<repo>/<service>:<sha>`) with the
workflow's own short-lived token; no registry password is stored anywhere.

### 4. First deployment

Push to `main` (or Actions -> **deploy** -> Run workflow). The pipeline:

1. `ci` builds and tests the commit.
2. `deploy` builds the five images (api, webhook, dbutils, web, minio-init) with SBOM and provenance,
   tagged with the commit SHA, and pushes them.
3. It uploads the stack files to `~deploy/datahub/releases/<sha>/` and runs
   `release.sh activate <sha>`: pull images, `docker compose up`, wait until every container is healthy
   and every one-shot job (migrations, seeding, bucket setup) finished successfully, then point
   `~/datahub/current` at the release. Old releases beyond the last 5 and their images are pruned.
4. `test-smoke.sh` checks the three public sites through Caddy with valid certificates.
5. `test-external.sh` runs the external security checks (a failure turns the run red but does not roll back).

The first deploy takes longer: Caddy obtains the Let's Encrypt certificates (ports 80 and 443 must be
reachable from the Internet) and Keycloak imports the realm.

### 5. After the first deployment

On the server as `ops`:

```bash
sudo -u deploy bash ~deploy/datahub/current/server-provision/tests/test-containers.sh
sudo bash ~deploy/datahub/current/server-provision/tests/test-host.sh --lynis
```

From your machine (not the server):

```bash
APP_DOMAIN=app.example.com AUTH_DOMAIN=auth.example.com HOOKS_DOMAIN=hooks.example.com \
SSH_HOST=<server-ip> bash server-provision/tests/test-external.sh
```

Then create the first platform admin:

```bash
ssh -i ~/.ssh/datahub_admin -L 8081:localhost:8081 -L 3300:localhost:3300 ops@<server-ip>
sudo grep -E '^(KEYCLOAK|GRAFANA)_ADMIN' ~deploy/datahub/.env
```

- Keycloak admin console: http://localhost:8081/admin/ -> realm `datahub` -> Users -> your user (register
  first at `https://<APP_DOMAIN>`) -> Role mapping -> assign `platform-admin`. Then create a permanent
  admin user in the master realm and delete the bootstrap `admin` account.
- Grafana: http://localhost:3300.

### 6. Rollback

- **Automatic**: if a new release does not become healthy, the deploy restores the running one; if the
  smoke test fails after the switch, it rolls back to the previous release.
- **From GitHub**: Actions -> **rollback** -> Run workflow. Empty `sha` = the release before the current
  one; or give any earlier commit SHA (its files are uploaded again if they were pruned).
- **On the server, without GitHub** (e.g. GitHub is down):

  ```bash
  sudo -iu deploy bash -lc 'datahub/current/server-provision/release.sh status'
  sudo -iu deploy bash -lc 'datahub/current/server-provision/release.sh rollback'          # previous
  sudo -iu deploy bash -lc 'datahub/current/server-provision/release.sh rollback <sha>'    # a specific one
  ```

Database migrations are **not** rolled back; `dbutils-migrate` only moves forward. Write migrations
backwards compatible (add columns/tables first, remove old ones in a later release), so the previous
release still works on the new schema.

## The security tests

Every test prints, per section, **what** it checks and **why**, then one line per check:
`[PASS]`, `[FAIL]` (with a `fix:` hint), `[WARN]` or `[INFO]`, and a summary with the list of failures.
The exit code is the number of failures, so `0` means everything passed.

| Script | Where / as whom | What it proves |
| --- | --- | --- |
| `tests/test-host.sh [--lynis]` | server, root | 1 security updates, 2 accounts and privilege, 3 effective SSH configuration (`sshd -T`), 4 firewall incl. `DOCKER-USER`, 5 fail2ban jails, 6 public listening sockets, 7 sysctl/kernel, 8 AppArmor, auditd, NTP, 9 Docker daemon, 10 `.env` permissions, world-writable files, SUID binaries, 11 optional Lynis hardening index |
| `tests/test-containers.sh` | server, deploy or root (stack running) | 1 internal networks have no route out, 2 which service is on which network, 3 host-published ports, 4 lateral movement: probe containers show the Api, DB, queue and S3 are unreachable from Caddy's network and nothing reaches the Internet from internal networks, 5 Api/MinIO/RabbitMQ/PostgreSQL refuse anonymous access, 6 privileged containers, docker.sock, host namespaces, capabilities, 7 floating image tags |
| `tests/test-external.sh` | any other machine | 1 port scan of 40 common ports, 2 http->https redirects and certificate validity/expiry, 3 TLS 1.0/1.1 refused, 1.2/1.3 accepted, 4 HSTS and security headers, no version disclosure, TRACE, 5 Keycloak admin/master realm, Api, Functions admin API hidden; BFF, document and webhook routes answer 401 without credentials, 6 SSH: publickey only, weak algorithms refused, banner |
| `tests/test-smoke.sh` | anywhere (used by the workflows) | web app, Keycloak discovery and webhook health answer 2xx over valid HTTPS |

`test-external.sh` never tries to log in over SSH; it opens one connection during the port scan and
one that reads the server's algorithm proposal and offered login methods. fail2ban (aggressive mode)
still records about 3 findings per run, and bans an IP after 5 within 10 minutes, including your own
mistyped logins. So run it at most once per 10 minutes from the same address, or you are locked out for
an hour (`sudo fail2ban-client set sshd unbanip <ip>` from another address or the provider's console).
The deploy workflow skips the SSH part.

Local test against the compose stack on a developer machine (Caddy's internal CA; port and SSH results
differ from a server because everything runs on 127.0.0.1):

```bash
docker cp datahub-caddy-1:/data/caddy/pki/authorities/local/root.crt /tmp/caddy-root.crt
export APP_DOMAIN=app.datahub.localhost AUTH_DOMAIN=auth.datahub.localhost HOOKS_DOMAIN=hooks.datahub.localhost
CA_FILE=/tmp/caddy-root.crt bash server-provision/tests/test-smoke.sh
CA_FILE=/tmp/caddy-root.crt SKIP_SSH=1 bash server-provision/tests/test-external.sh
bash server-provision/tests/test-containers.sh
```

## Maintenance

| Task | How |
| --- | --- |
| New key in `.env.example` | Add it to `~deploy/datahub/.env` on the server first; `release.sh` refuses to deploy and names missing keys |
| Rotate a secret | Edit `.env` (`sudo -u deploy nano ~deploy/datahub/.env`), then `release.sh restore`. Database and MinIO passwords must also be changed inside PostgreSQL / MinIO |
| See who touched the secrets | `sudo ausearch -k datahub-secrets -i` |
| Banned IPs | `sudo fail2ban-client status sshd`, unban: `sudo fail2ban-client set sshd unbanip <ip>` |
| Rotate the deploy key | Add the new public key with `restrict ` in front to `~deploy/.ssh/authorized_keys`, update `SSH_KEY`, remove the old line |
| Server re-created | Provision again and update `SSH_KNOWN_HOSTS`; restore the `pgdata` and `miniodata` volumes from backup |

Not covered here, plan them separately: **backups** of the `pgdata` and `miniodata` volumes (off the
server), provider-level firewall/DDoS protection (a cloud firewall allowing only 22/80/443 adds a second
layer in front of ufw), and log shipping off the host.
