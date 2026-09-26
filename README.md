# DataHub

DataHub receives container temperature events over a webhook, stores every raw payload in S3-compatible
object storage, queues it in RabbitMQ, and persists it in PostgreSQL (always in °C). Signed-in users browse
containers and their readings in a Next.js front end. The solution also carries the platform-template
samples: Products (generic CRUD repository, outbox, audit consumer), Documents (uploads/downloads streamed through the Api; MinIO is never public)
and a RabbitMQ queue admin page.

```
sender ──POST /api/webhooks/container──▶ webhook (Azure Functions) ──▶ MinIO webhooks/container/<timestamp>.json
                                              │
                                              └─▶ RabbitMQ exchange "webhooks" ──▶ Api WebhookInboxConsumer ──▶ PostgreSQL
browser ──▶ web (Next.js, Auth.js) ──/api/graphql (BFF, bearer)──▶ Api (HotChocolate) ──▶ PostgreSQL
```

## Quickstart

1. Install Docker, the .NET 10 SDK, [Bun](https://bun.sh), Node.js 22 and Azure Functions Core Tools v4
   (`npm i -g azure-functions-core-tools@4`). `bun` and `func` must be on the PATH your IDE sees
   (for a desktop-launched Rider that is `~/.profile`, e.g. link them into `~/.local/bin`).
2. `dotnet tool restore && (cd web && bun install)` and trust the HTTPS dev certificate once:
   `dotnet dev-certs https --trust` (on Linux this covers .NET and the Chrome/Firefox NSS stores; curl needs
   `--cacert ~/.aspnet/dev-certs/trust/*.pem`).
3. `dotnet run --project aspire/DataHub.AppHost` (launch profile `https`, dashboard https://localhost:17180) and
   open the dashboard link it prints. The `http` profile still exists for the dashboard only.
4. In the dashboard start **dbutils-seed** (resets and seeds App, Auth and the Keycloak dev users).
5. Open https://localhost:3000 and sign in as `user@local.test` / `User123!` (or `admin@local.test` / `Admin123!`
   for the queue admin page). Dev passwords and secrets are AppHost parameters in
   `aspire/DataHub.AppHost/appsettings.json`; override them with user secrets or a git-ignored
   `appsettings.User.json`.

Send a signed webhook (secret = parameter `webhook-hmac-secret`):

```bash
body='{"containerId":"MSCU1234567","readings":[{"timestamp":"2026-09-25T10:00:00Z","value":41,"unit":"F"}]}'
ts=$(date +%s); sig=$(printf '%s.%s' "$ts" "$body" | openssl dgst -sha256 -hmac datahub-hmac-dev-secret -hex | awk '{print $NF}')
curl -i --cacert ~/.aspnet/dev-certs/trust/*.pem -X POST https://localhost:7071/api/webhooks/container \
  -H "X-Webhook-Timestamp: $ts" -H "X-Webhook-Signature: sha256=$sig" -H "X-Webhook-Id: demo-1" -d "$body"
```

Payload: `{"containerId": string, "readings": [{"timestamp": ISO-8601, "value": number, "unit": "C"|"F"|"K"}]}`.
Optional headers: `X-Webhook-Id` (message-id, duplicates are dropped) and `X-Webhook-Event`
(routing key `webhook.<sender>.<event>`, default `received`). Bearer senders use a Keycloak
client-credentials token of client `webhook-container` (audience `webhook`).

## Resources (Aspire AppHost)

| Resource | Port | Notes |
| --- | --- | --- |
| postgres (+ `app`, `auth`, `keycloak`) | 5432, pgAdmin 5050 | persistent volume |
| keycloak | 8080 | realm `datahub` imported from `aspire/DataHub.AppHost/keycloak/realms`, theme `datahub` |
| minio | 9000 / 9001 | image `cgr.dev/chainguard/minio` (MinIO no longer publishes to Docker Hub) |
| rabbitmq | 5672 / 15672 | management plugin |
| storage (Azurite) | dynamic | Functions host state only |
| dbutils-migrate | — | `migrate --context All`, runs before the Api |
| dbutils-seed | — | `reseed`, explicit start (destructive) |
| api | https 5101, http 5100 | GraphQL at `/graphql`, `/health`, `/alive` |
| webhook | https 7071 | `/api/webhooks/{sender}`, `/api/health`, `/api/alive` |
| web | https 3000 | dashboard command **Regenerate GraphQL types** |

HTTPS uses the ASP.NET Core dev certificate. The AppHost exports it on start to the git-ignored
`aspire/DataHub.AppHost/.certs/` (PFX for Azure Functions Core Tools, PEM for Next.js). Keycloak, MinIO and
RabbitMQ stay on plain HTTP on localhost.

## Observability

The AppHost also starts the Grafana stack from DieWikinger (`aspire/DataHub.AppHost/Observability.cs`,
configuration in `docker/` at the repository root, shared with `compose.observability.yaml`):

| Resource | Role |
| --- | --- |
| grafana | http://localhost:3001 (`admin` / parameter `grafana-admin-password`); dashboards and alert rules in folder *DataHub* |
| alloy | OTLP collector on localhost:4317 (gRPC) / 4318 (HTTP); tails container logs; probes api, webhook, web, Keycloak, MinIO |
| prometheus · loki · tempo | metrics · logs · traces (retention 30 d · 7 d · 2 d) |
| cadvisor · postgres-exporter | per-container resources · PostgreSQL (`pg_stat_statements` is preloaded) |

The .NET services and the web front end export OTLP twice: to the Aspire dashboard as before, and to Alloy
(`OTEL_COLLECTOR_ENDPOINT`). Prometheus also scrapes MinIO, RabbitMQ and Keycloak. Every container carries
`com.docker.compose.project=datahub` / `com.docker.compose.service=<resource>` labels, which is what
cAdvisor, Alloy, the dashboards and the alert rules select on. Aspire only recreates a persistent container
when its image or environment changes; bump `Observability.StackRevision` after changing container arguments.

## Production (Docker Compose)

`compose.prod.yaml` (+ the included `compose.observability.yaml`) runs everything behind Caddy, which
obtains the certificates. Configuration: `.env` (copy `.env.example`), `docker/` (Caddy, init scripts,
observability) and the Keycloak realm/theme under `aspire/DataHub.AppHost/keycloak/`.

| Public host | Service | Notes |
| --- | --- | --- |
| `APP_DOMAIN` | web | the Api is not public; web's BFF routes (`/api/graphql`, `/api/documents/*`, `/api/branding/*`) call it |
| `AUTH_DOMAIN` | keycloak | `/admin` answers 404; the admin console is on the tunnel |
| `HOOKS_DOMAIN` | webhook | only `/api/webhooks/*` and `/api/health` |

MinIO has no public host and is not on Caddy's network. All buckets are private: document bytes go
browser → web → Api → MinIO (the Api checks ownership, type and the 25 MB limit), branding assets are
served by the Api at `/api/branding/<key>`, and raw webhook payloads are written by the webhook.

Networks: `ingress` (Caddy only; 80/443 and the only route to the Internet), `proxy` and `backend`
(internal: no gateway, no NAT), `observability` (internal), and `tunnel` (Grafana and the Keycloak admin
console, published on 127.0.0.1 only, IP masquerading off). On a server:
`ssh -L 3300:localhost:3300 -L 8081:localhost:8081 <server>`, then Grafana at http://localhost:3300 and
the Keycloak admin console at http://localhost:8081/admin/.

Least privilege: the api connects as a DML-only role (`docker/postgres-init`), migrations run as the owner
in the one-shot `dbutils-migrate`, and the api and webhook each get their own MinIO account scoped to their
buckets (`docker/minio-init`). `dbutils-seed` upserts master data and the realm's login theme and default
role; it creates no users.

Local test on this machine (`*.localhost` resolves to 127.0.0.1, `CADDY_TLS=internal`):

```bash
cp .env.example .env                                  # change the secrets for anything but a local test
docker compose -f compose.prod.yaml build
docker compose -f compose.prod.yaml up -d
# Trust Caddy's local CA in Chrome/Chromium (NSS) once:
docker compose -f compose.prod.yaml cp caddy:/data/caddy/pki/authorities/local/root.crt ./caddy-root.crt
certutil -d sql:$HOME/.pki/nssdb -A -t "C,," -n "Caddy Local Authority (DataHub)" -i ./caddy-root.crt
```

Then open https://app.datahub.localhost and register, or create users in the admin console
(http://localhost:8081/admin/). Caddy needs ports 80 (redirect to HTTPS, ACME) and 443; if another
web server holds 80 locally, stop it or move `HTTP_PORT`. On a server set real domains, `CADDY_TLS=<acme e-mail>`, an empty
`WEB_EXTRA_CA_CERTS`, and `IMAGE_REPO`/`IMAGE_TAG` of your registry.

## Solution layout

```
src/     Domain · Application · Infrastructure · Auth · DbUtils · Api · Webhook · ServiceDefaults
aspire/  DataHub.AppHost
tests/   <Project>.Tests for every production project + DataHub.IntegrationTests (Aspire.Hosting.Testing)
web/     Next.js (App Router, Bun, Tailwind v4, Auth.js, GraphQL Code Generator)
```

References point inward only; `tests/DataHub.Domain.Tests/ArchitectureTests.cs` fails the build otherwise.
Package versions live in `Directory.Packages.props`; analyzers and test packages in `Directory.Build.props`.

## Database utility

```bash
dotnet run --project src/DataHub.DbUtils -- migrate --context All [--check]
dotnet run --project src/DataHub.DbUtils -- reset --context App [--force]
dotnet run --project src/DataHub.DbUtils -- seed --fixtures --master-data --keycloak-users
dotnet run --project src/DataHub.DbUtils -- maintain --vacuum-analyze --purge-soft-deleted --older-than 90d --orphan-objects [--delete]
dotnet run --project src/DataHub.DbUtils -- reseed
```

Schema changes are code-first only:

```bash
dotnet ef migrations add <Name> --context AppDbContext --project src/DataHub.Infrastructure --startup-project src/DataHub.DbUtils --output-dir Persistence/Migrations
dotnet ef migrations add <Name> --context AuthDbContext --project src/DataHub.Auth --startup-project src/DataHub.DbUtils --output-dir Persistence/Migrations
```

Raw SQL (views, functions) goes in `src/DataHub.Infrastructure/Persistence/Sql/*.sql` (embedded) and is
applied from a migration with `migrationBuilder.Sql(SqlScripts.Read("<file>.sql"))`.

## How to add a feature

Example: an `Invoice` entity with GraphQL CRUD and a page.

1. **Entity** – `src/DataHub.Domain/Invoices/Invoice.cs`, deriving from `AuditableEntity` (UUID v7 id,
   audit columns, `xmin` row version) and optionally `ISoftDelete` / `IPublishesChanges` (domain events via
   the outbox).
2. **Persistence** – add `DbSet<Invoice>` to `AppDbContext`, an `IEntityTypeConfiguration<Invoice>` in
   `Persistence/Configurations`, then `dotnet ef migrations add AddInvoices ...` (above).
3. **Repository** – nothing to do: `IRepository<Invoice, Guid>` is registered open-generic. Only when behaviour
   differs, add `IInvoiceRepository : IRepository<Invoice, Guid>` in Application and
   `InvoiceRepository : Repository<Invoice, Guid>` in Infrastructure overriding the virtual members
   (see `ProductRepository`), and register it in `Infrastructure/DependencyInjection.cs`.
4. **Use case** – `InvoiceService : CrudService<Invoice, Guid, CreateInvoiceDto, UpdateInvoiceDto>` in
   Application with DataAnnotations on the DTOs; register it in `Application/DependencyInjection.cs`.
5. **Permissions** – add `Permissions.Invoices.Read/Write` in `src/DataHub.Auth/Permissions.cs` (a policy is
   generated per constant) and grant them in `src/DataHub.DbUtils/MasterData/auth-roles.json`.
6. **GraphQL** – `src/DataHub.Api/GraphQl/Invoices/InvoiceQueries.cs` (`[ExtendObjectType(typeof(RootQuery))]`,
   `[UsePaging][UseProjection][UseFiltering][UseSorting]` over `IRepository.Query()`) and
   `InvoiceMutations.cs` (`[ExtendObjectType(typeof(RootMutation))]`, `[Authorize(Policy = ...)]`,
   `[Error<ValidationError>]` etc.). They are registered by the source generator; `Program.cs` never changes.
   Reference fields use a `[DataLoader]` (see `ProductDataLoaders`).
7. **Contract** – `dotnet run --project src/DataHub.Api -- schema export --output web/schema.graphql`, then
   `cd web && bun run codegen` (or the dashboard command). Commit both; CI fails on any diff.
8. **Page** – write the operation with `graphql(...)` in the component, call it through `execute()` from
   `web/src/lib/graphql.ts`, add the page under `web/src/app/app/`.
9. **Tests** – unit tests in `tests/DataHub.Application.Tests`, repository overrides in
   `tests/DataHub.Infrastructure.Tests` (Testcontainers), GraphQL in `tests/DataHub.Api.Tests`, UI in
   `web` (Vitest + MSW, Playwright).

## Tests

| Level | Command | Needs Docker |
| --- | --- | --- |
| Unit (backend) | `dotnet test --project tests/DataHub.Domain.Tests` / `...Application.Tests` | no |
| Unit (frontend) | `cd web && bun run test` (`test:coverage` for v8 coverage) | no |
| Contract | `tests/DataHub.Api.Tests` `SchemaTests` + CI codegen diff | no |
| Integration | `dotnet test --project tests/DataHub.<Infrastructure\|Auth\|DbUtils\|Api\|Webhook>.Tests` | yes |
| End to end | `dotnet test --project tests/DataHub.IntegrationTests` (boots the AppHost, seeds, runs Playwright) | yes |

The end-to-end suite uses the same fixed ports as a running AppHost; stop your dev AppHost first.
Coverage gate: `python3 build/coverage-gate.py 0.8 DataHub.Domain,DataHub.Application <cobertura files>`.

## CI

`.github/workflows/ci.yml`: build with warnings as errors → unit tests + coverage gate → contract check
(schema export + codegen diff, `graphql-inspector diff` on PRs, `has-pending-model-changes` per context) →
gitleaks → Testcontainers integration tests → Aspire + Playwright end to end → multi-arch images
(`src/DataHub.Api/Dockerfile`, `src/DataHub.DbUtils/Dockerfile`, `src/DataHub.Webhook/Dockerfile`, `web/Dockerfile`).
