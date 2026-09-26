# DataHub

DataHub receives container temperature events over a webhook, stores every raw payload in S3-compatible
object storage, queues it in RabbitMQ, and persists it in PostgreSQL (always in °C). Signed-in users browse
containers and their readings in a Next.js front end. The solution also carries the platform-template
samples: Products (generic CRUD repository, outbox, audit consumer), Documents (MinIO presigned uploads)
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
2. `dotnet tool restore && (cd web && bun install)`
3. `dotnet run --project aspire/DataHub.AppHost` and open the dashboard link it prints.
4. In the dashboard start **dbutils-seed** (resets and seeds App, Auth and the Keycloak dev users).
5. Open http://localhost:3000 and sign in as `user@local.test` / `User123!` (or `admin@local.test` / `Admin123!`
   for the queue admin page). Dev passwords and secrets are AppHost parameters in
   `aspire/DataHub.AppHost/appsettings.json`; override them with user secrets or a git-ignored
   `appsettings.User.json`.

Send a signed webhook (secret = parameter `webhook-hmac-secret`):

```bash
body='{"containerId":"MSCU1234567","readings":[{"timestamp":"2026-09-25T10:00:00Z","value":41,"unit":"F"}]}'
ts=$(date +%s); sig=$(printf '%s.%s' "$ts" "$body" | openssl dgst -sha256 -hmac datahub-hmac-dev-secret -hex | awk '{print $NF}')
curl -i -X POST localhost:7071/api/webhooks/container \
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
| api | 5100 | GraphQL at `/graphql`, `/health`, `/alive` |
| webhook | 7071 | `/api/webhooks/{sender}`, `/api/health`, `/api/alive` |
| web | 3000 | dashboard command **Regenerate GraphQL types** |

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
