# Load Tests (k6)

Load-test suite for the DataHub webhook (`POST /api/webhooks/container`). One script
(`main.js`), four test types, switchable target. Needs a local [k6](https://k6.io)
binary or Docker — `run.sh` picks whichever is available.

## Quick start

```sh
./run.sh smoke compose    # 2 VUs, 1 min, against compose.prod.yaml run locally
./run.sh load local       # ramp to 20 VUs, against Aspire (https://localhost:7071)
./run.sh stress compose   # find the ceiling
./run.sh soak compose     # 10 VUs for 30 min
CONFIRM_PROD=1 HMAC_SECRET=... ./run.sh smoke https://hooks.example.org
```

| Type   | What it proves                         | Shape                                  |
|--------|-----------------------------------------|----------------------------------------|
| smoke  | Endpoint alive, auth + pipeline work    | 2 VUs, 1 min                           |
| load   | Normal traffic holds thresholds         | ramp to 20 VUs, hold 5 min             |
| stress | Where the webhook breaks                | arrival rate ramps 2→200 req/s, 8 min  |
| soak   | No leaks / exhaustion over time         | 10 VUs, 30 min                         |

## Traffic mix (per iteration)

- **90%** valid webhook POST: HMAC-signed `{containerId, readings[1..10]}` for a random
  seeded container (`SEED-CONT-00000001`…`CONTAINERS`), expects **202**.
- **5%** wrong-signature POST, expects **401** — rejection must stay cheap under load.
- **5%** `GET /api/health`, expects **200**.

## Environment variables

| Variable      | Effect                                                                  |
|---------------|--------------------------------------------------------------------------|
| `HMAC_SECRET` | Required. `compose` preset auto-reads `WEBHOOK_HMAC_SECRET` from `../.env` |
| `CONTAINERS`  | Container code space to hit (default 5000000, match the seeded count)    |
| `THINK=0`     | Disables 0.5–2s think time. Pure ceiling-hunting                          |
| `SKIP_TLS=1`  | Skip TLS verification (set automatically by the local/compose presets)    |
| `CONFIRM_PROD=1` | Required for any non-localhost target — every valid POST writes real rows |

## Prod-scale data

The webhook consumer inserts into `Containers` (5M) and `Temperatures` (15M) at prod
scale. Seed first for realistic numbers (lookups against big indexes):

```sh
psql ... -f ../SqlScripts/populate-test-data.sql
```

On an unseeded DB the consumer simply creates the containers — the suite still runs,
the DB is just small.

## What the numbers mean

The webhook answers **202 after** MinIO put + RabbitMQ publish; DB ingestion is
asynchronous. k6 therefore measures webhook latency, **not** end-to-end ingestion.
Watch the queue and consumer during a run:

- RabbitMQ queue depth + consumer throughput: Grafana (app-overview dashboard) or
  `docker exec datahub-rabbitmq-1 rabbitmqctl list_queues`
- A run is only "passed" when the queue drains back to ~0 afterwards.

## Cleanup

All k6 readings carry sentinel timestamps (Jan–Feb 2000) and `k6-…` webhook ids:

```sh
psql ... -f cleanup-loadtest.sql
```

Raw payloads also land in the MinIO `webhooks` bucket (prefix `container/`); the ids
are not k6-tagged there, so wipe the prefix as a whole if the volume bothers you:
`mc rm --recursive --force <alias>/webhooks/container/`.

## Not covered

- Bearer-token auth (Keycloak client `webhook-container`) — HMAC exercises the same
  pipeline; add if the JWKS path needs its own numbers.
- The GraphQL API — this suite targets the webhook only.

## Results

Each run prints a summary and writes `results/<type>-<timestamp>.json` (gitignored).
Optional: stream metrics into the observability stack with
`K6_PROMETHEUS_RW_SERVER_URL=http://localhost:9090/api/v1/write ./run.sh load compose -o experimental-prometheus-rw`.
