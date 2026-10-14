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

With the web flow enabled, **20%** goes to a signed-in web user instead (webhook drops
to 70%): one Keycloak OIDC login per VU (Auth.js authorization-code flow), then the
`Containers`/`Temperatures` GraphQL queries through the `/api/graphql` BFF — exactly
what the containers page does in the browser. The run aborts on a failed login.

## Web user flow

Enabled when `WEB_URL`, `LOGIN_EMAIL` and `LOGIN_PASSWORD` are set. The `compose`
preset sets `WEB_URL=https://app.datahub.localhost` and reads the password from
`.k6user` (gitignored, `K6PW=<password>`), email defaulting to `k6@local.test`.

Create the test user once per Keycloak instance (do **not** use
`dbutils seed --keycloak-users` against compose — it rewrites the web client's
redirect URIs to localhost:3000):

```sh
docker exec datahub-keycloak-1 /opt/keycloak/bin/kcadm.sh config credentials \
  --server http://localhost:8080 --realm master --user admin --password <KEYCLOAK_ADMIN_PASSWORD>
docker exec datahub-keycloak-1 /opt/keycloak/bin/kcadm.sh create users -r datahub \
  -s username=k6@local.test -s email=k6@local.test -s enabled=true \
  -s emailVerified=true -s firstName=k6 -s lastName=loadtest
docker exec datahub-keycloak-1 /opt/keycloak/bin/kcadm.sh set-password -r datahub \
  --username k6@local.test --new-password <password>
docker exec datahub-keycloak-1 /opt/keycloak/bin/kcadm.sh add-roles -r datahub \
  --uusername k6@local.test --rolename user
echo 'K6PW=<password>' > .k6user && chmod 600 .k6user
```

`firstName`/`lastName` matter: without them Keycloak interrupts the login with a
`VERIFY_PROFILE` required action and the flow never reaches the app.

## Environment variables

| Variable      | Effect                                                                  |
|---------------|--------------------------------------------------------------------------|
| `HMAC_SECRET` | Required. `compose` preset auto-reads `WEBHOOK_HMAC_SECRET` from `../.env` |
| `CONTAINERS`  | Container code space to hit (default 5000000, match the seeded count)    |
| `WEB_URL` / `LOGIN_EMAIL` / `LOGIN_PASSWORD` | Enables the signed-in web flow (see below) |
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
- GraphQL beyond the containers page (`Containers`/`Temperatures`): products,
  documents, admin queries.
- Client-side rendering cost — k6 fetches pages and GraphQL, it does not run React.

## Results

Each run prints a summary and writes `results/<type>-<timestamp>.json` (gitignored).
Optional: stream metrics into the observability stack with
`K6_PROMETHEUS_RW_SERVER_URL=http://localhost:9090/api/v1/write ./run.sh load compose -o experimental-prometheus-rw`.
