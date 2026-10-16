# Load Test Results — 2026-10-14 / 2026-10-16

Stress and breakpoint runs against DataHub, local compose stack and production (`CONFIRM_PROD=1` runs).
Raw k6 summaries in `results/`. All times UTC.

## Runs

| # | Start (UTC) | Type | Env | Max VU | Duration | HTTP reqs | Webhooks 202 (pass/fail) | Fail rate | Latency med / p95 | Dropped iter | Result / key info |
|---|---|---|---|---|---|---|---|---|---|---|---|
| 1 | 14th 09:18 | stress | local | 262 | 8m | 38,247 | 34,369/0 | 0% | 5ms / 9ms | 62 | Baseline pass |
| 2 | 14th 13:52 | stress | local | 394 | 2m14s | 4,166 | 2,062/0 | 2.35% | 854ms / 20s | 207 | Exposed Auth.js refresh-cookie bug: `containers ok` 33/175 fails |
| 3 | 14th 14:23 | stress | local | 361 | 1m45s | 2,689 | 1,359/0 | 2.11% | 349ms / 18.5s | 167 | Same bug confirmed; fixed afterwards (`auth()` wrapper + `totalCount` removal) |
| 4 | 14th 17:49 | breakpoint | local | 2000 | 7m | 231,523 | 231,523/0 | 0% | 91ms / 3.6s | 123,967 | No webhook HTTP ceiling up to ~2,000 req/s; left 161,748-msg RabbitMQ backlog — consumer limit ~100–120 msg/s |
| 5 | 14th 18:15 | stress | **PROD** | 600 | 3m53s | 9,617 | 5,454/0 | 0.03% | 210ms / 8.9s | 619 | Functional pass, tail latency bad: p95 8.9s, max 23.2s |
| 6 | 14th 20:52 | stress | local | 265 | 8m | 41,636 | 26,694/0 | 0% | 11ms / 18ms | 65 | Validated OTEL 0.1 sampling + Alloy memory_limiter; queues drained to 0 |
| 7 | 14th 21:07 | stress | **PROD** | 600 | 5m6s | 15,156 | 8,855/0 | 0.01% | 153ms / 9.2s | 2,073 | Post-fix prod check: p95 still ~9s, drops rising |
| 8 | 14th 21:24 | stress | **PROD** | 600 | 5m18s | 16,291 | 9,284/0 | 0.01% | 155ms / 8.9s | 2,763 | Repeat run, same picture — prod tail latency persists despite fixes |
| 9 | 15th 08:13 | stress 2x | local | 518 | 8m | 83,069 | 53,480/0 | 0% | 6ms / 15ms | 118 | Doubled profile (maxVUs 1200) validates clean locally |
| 10 | 15th 09:13 | stress 2x | **PROD** | 766 | **aborted 2m10s** | 7,853 | 3,899/0 | 0.01% | 665ms / 12.2s | 368 | Abort: `login failed — check LOGIN_EMAIL/LOGIN_PASSWORD at ensureLogin`. Keycloak is the first saturation point; webhooks stayed fine |
| 11 | 15th 09:32 | breakpoint | **PROD** | 500 | **aborted 31s** | 2,873 | 0/2,873 | **100%** | 21ms / 33ms | 0 | Every webhook rejected fast — server refusing, not slow. Host still unrecovered from run 10. Auto-abort at 5% threshold |
| 12 | 15th 10:05 | breakpoint | **PROD** | 2000 | **aborted 1m43s** | 8,243 | 7,809/434 | 5.27% | 9.2s / 25.7s | 9,273 | Accepts load but saturated: median 9.2s. Abort at 5% threshold |
| 13 | 15th 12:39 | stress 2x | **PROD** | 1200 | 8m30s | 20,961 | 8,192/3,131 | **27.7%** | 6.9s / 60s (timeout ceiling) | 59,867 (78.7%) | Collapse: login 71.8% fail, CSRF extraction 100% fail, p95 pinned at 60s timeout. This run pushed the host into swap-thrash (2Gi swap full, load avg ~30, 82 zombie healthcheck processes) |
| 14 | 16th 13:12 | stress | **PROD** | 400 (planned) | **aborted 18s** | 361 | 113/0 | 0.3% | 85ms / 635ms | 0 | First prod run after rate-limit deploy. Healthy until login #28: Caddy `auth_login` zone (30 req/1m per IP) returned 429 → `login ok` check failed → abort. Webhooks, health, signature checks all green |
| 15 | 16th 13:12 | stress | **PROD** | 400 (planned) | **aborted <1s** | 4 | — | 25% | 26ms / 43ms | 0 | Retry 37s later: first login 429 — runner IP still inside the same 1-minute window. Aborted after 4 requests |

Not listed: six smoke and five load runs on Oct 14 (20 VU, suite development and `totalCount`-fix validation, all local).

## Summary

- **Webhook HTTP layer was never the bottleneck on prod until run 13.** Zero webhook failures through 600 VU. The real webhook ceiling is the consumer (~100–120 msg/s); bursts buffer in RabbitMQ (161K backlog after run 4).
- **Prod knee is between 600 and 766 VU.** 600 VU passes functionally (runs 5, 7, 8) with ~9s p95; at 766 VU login breaks (run 10).
- **Keycloak/login is the single point of failure.** Every prod failure from run 10 onward starts at auth and cascades to CSRF and GraphQL.
- **Local ≠ prod.** The same doubled profile gives p95 15ms locally (run 9) but total collapse on prod (run 13). The 3.8Gi prod VM carries the app stack plus the full observability stack; under load the sum of container memory limits overcommits the host, swap fills, and everything I/O-thrashes.
- **Run 11 anomaly:** 100% rejection with 21ms responses means fast errors, not load-induced slowness — the host had not recovered from run 10.
- **Fixes shipped during this campaign:** `auth()` wrapper for rotated session cookies, `totalCount` removed from Containers query (eliminated 137ms `COUNT(*)` on 5M rows), OTEL trace sampling 0.1, Alloy memory_limiter, observability memory-limit rebalance.
- **Caddy rate limiting verified on prod (runs 14–15).** The `auth_login` zone (30 req/1m per IP) cuts a single-IP login flood exactly at the cap and keeps rejecting until the window rolls — the behaviour that would have stopped run 13's collapse at the front door. Side effect: a stress test that logs in per iteration from one IP now kills itself by design. To load-test past it, log in once in `setup()` and share the token across VUs (do not raise the limit for tests).

## Open items

- Rebalance container memory limits to fit the 3.8Gi prod host (or grow the VM) — current limits overcommit and cause swap-thrash under load. *(done 2026-10-16: per-service mem_limits deployed; minio's 160m then exposed page-cache thrash from 53K leftover load-test objects — cleanup pending)*
- Keycloak capacity: tune or scale before any test beyond ~600 VU on prod.
- `init: true` on keycloak and grafana compose services to reap healthcheck zombies. *(done 2026-10-16)*
- `main.js`: single login in `setup()`, token shared across VUs — required for any further prod stress runs now that the auth rate limit is live.
- Delete the ~53K load-test payload objects from the `webhooks` MinIO bucket (419MB; keeps minio's scanner working set inside its 160m limit).
