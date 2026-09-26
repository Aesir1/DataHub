using System.Diagnostics;
using Aspire.Hosting.ApplicationModel;
using DataHub.AppHost;
using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.User.json", optional: true).AddEnvironmentVariables().AddCommandLine(args);

const string realm = "datahub";

var postgresPassword = builder.AddParameter("postgres-password", secret: true);
var keycloakAdminPassword = builder.AddParameter("keycloak-admin-password", secret: true);
var minioPassword = builder.AddParameter("minio-password", secret: true);
var rabbitPassword = builder.AddParameter("rabbitmq-password", secret: true);
var webClientSecret = builder.AddParameter("web-client-secret", secret: true);
var webhookClientSecret = builder.AddParameter("webhook-client-secret", secret: true);
var webhookHmacSecret = builder.AddParameter("webhook-hmac-secret", secret: true);
var authSecret = builder.AddParameter("auth-secret", secret: true);
var seedAdminPassword = builder.AddParameter("seed-admin-password", secret: true);
var seedUserPassword = builder.AddParameter("seed-user-password", secret: true);

// HTTPS for Api, webhook and web uses the ASP.NET Core dev certificate (trust it once: dotnet dev-certs https --trust).
var devCert = DevCertificate.Export(
    Path.Combine(builder.AppHostDirectory, ".certs"),
    builder.Configuration["Parameters:dev-cert-password"] ?? throw new InvalidOperationException("Parameter dev-cert-password is missing."));

// pg_stat_statements must be preloaded at start-up; postgres-exporter reads query rate and slow queries from it.
var postgres = builder.AddPostgres("postgres", password: postgresPassword, port: 5432)
    .WithLifetime(ContainerLifetime.Persistent)
    .WithEndpointProxySupport(false)
    .WithDataVolume("datahub-postgres")
    .WithArgs("-c", "shared_preload_libraries=pg_stat_statements", "-c", "pg_stat_statements.max=1000", "-c", "pg_stat_statements.track=top")
    .WithStackLabels("postgres")
    .WithPgAdmin(p => p.WithLifetime(ContainerLifetime.Persistent).WithHostPort(5050).WithStackLabels("pgadmin"));
var appDb = postgres.AddDatabase("app");
var authDb = postgres.AddDatabase("auth");
var keycloakDb = postgres.AddDatabase("keycloak-db", databaseName: "keycloak");

var keycloak = builder.AddKeycloak("keycloak", 8080, adminPassword: keycloakAdminPassword)
    .WithStackLabels("keycloak")
    .WithEnvironment("KC_METRICS_ENABLED", "true")
    .WithLifetime(ContainerLifetime.Persistent)
    .WithEndpointProxySupport(false)
    .WithoutHttpsCertificate()
    .WithRealmImport("./keycloak/realms")
    .WithBindMount("./keycloak/themes", "/opt/keycloak/themes", isReadOnly: true)
    .WithEnvironment("KC_DB", "postgres")
    .WithEnvironment("KC_DB_URL", $"jdbc:postgresql://{postgres.Resource.Name}:5432/{keycloakDb.Resource.DatabaseName}")
    .WithEnvironment("KC_DB_USERNAME", "postgres")
    .WithEnvironment("KC_DB_PASSWORD", postgresPassword)
    .WithEnvironment("WEB_CLIENT_SECRET", webClientSecret)
    .WithEnvironment("WEBHOOK_CLIENT_SECRET", webhookClientSecret)
    .WithEnvironment("WEB_BASE_URL", "https://localhost:3000")
    .WaitFor(keycloakDb);
var keycloakHttp = keycloak.GetEndpoint("http");
var issuer = ReferenceExpression.Create($"{keycloakHttp}/realms/{realm}");

// minio/minio is no longer published on Docker Hub; Chainguard's build is free and multi-arch.
var minio = builder.AddMinioContainer("minio", rootPassword: minioPassword, port: 9000)
    .WithStackLabels("minio")
    .WithEnvironment("MINIO_PROMETHEUS_AUTH_TYPE", "public")
    .WithImageRegistry("cgr.dev")
    .WithImage("chainguard/minio")
    .WithImageTag("latest")
    .WithLifetime(ContainerLifetime.Persistent)
    .WithEndpointProxySupport(false)
    .WithEndpoint("console", e => e.Port = 9001)
    .WithDataVolume("datahub-minio");

var rabbitmq = builder.AddRabbitMQ("rabbitmq", password: rabbitPassword, port: 5672)
    .WithStackLabels("rabbitmq")
    .WithLifetime(ContainerLifetime.Persistent)
    .WithEndpointProxySupport(false)
    .WithManagementPlugin(port: 15672)
    .WithDataVolume("datahub-rabbitmq");

var storage = builder.AddAzureStorage("storage")
    .RunAsEmulator(e => e.WithLifetime(ContainerLifetime.Persistent).WithDataVolume("datahub-azurite").WithStackLabels("azurite"));

// Grafana, Prometheus, Loki, Tempo, Alloy, cAdvisor and postgres-exporter (see Observability.cs). Every .NET
// service and the web front end also send OTLP to Alloy via OTEL_COLLECTOR_ENDPOINT.
var observability = builder.AddObservability(postgres, appDb);

var migrate = builder.AddProject<Projects.DataHub_DbUtils>("dbutils-migrate")
    .WithEnvironment("OTEL_COLLECTOR_ENDPOINT", observability.OtlpHttp)
    .WithArgs("migrate", "--context", "All")
    .WithReference(appDb)
    .WithReference(authDb)
    .WithReference(minio)
    .WaitFor(appDb)
    .WaitFor(authDb);

// Destructive: drops and reseeds App, Auth and the Keycloak dev users. Start it from the dashboard.
builder.AddProject<Projects.DataHub_DbUtils>("dbutils-seed")
    .WithEnvironment("OTEL_COLLECTOR_ENDPOINT", observability.OtlpHttp)
    .WithArgs("reseed")
    .WithReference(appDb)
    .WithReference(authDb)
    .WithReference(minio)
    .WithReference(keycloak)
    .WithEnvironment("Seed__KeycloakAdminPassword", keycloakAdminPassword)
    .WithEnvironment("Seed__AdminPassword", seedAdminPassword)
    .WithEnvironment("Seed__UserPassword", seedUserPassword)
    .WithEnvironment("Seed__LoginTheme", "datahub")
    .WithExplicitStart()
    .WaitFor(appDb)
    .WaitFor(keycloak);

var api = builder.AddProject<Projects.DataHub_Api>("api", launchProfileName: "https")
    .WithEnvironment("OTEL_COLLECTOR_ENDPOINT", observability.OtlpHttp)
    .WithReference(appDb)
    .WithReference(authDb)
    .WithReference(keycloak)
    .WithReference(rabbitmq)
    .WithReference(minio)
    .WithEnvironment("Messaging__ManagementUrl", rabbitmq.GetEndpoint("management"))
    .WaitForCompletion(migrate)
    .WaitFor(keycloak)
    .WaitFor(rabbitmq)
    .WaitFor(minio);

// Waits for the Api so the queue is declared and bound before the first webhook is published.
// --useHttps is also in the launch profile (Aspire reads it for the endpoint scheme), but Rider's Aspire plugin
// starts `func host start` without the profile's arguments, so it is passed here too; func accepts it twice.
var webhook = builder.AddAzureFunctionsProject<Projects.DataHub_Webhook>("webhook")
    .WithEnvironment("OTEL_COLLECTOR_ENDPOINT", observability.OtlpHttp)
    .WithHostStorage(storage)
    .WithReference(rabbitmq)
    .WithReference(minio)
    .WithEnvironment("Webhook__Authority", issuer)
    .WithEnvironment("Webhook__Senders__container__Schemes__0", "hmac")
    .WithEnvironment("Webhook__Senders__container__Schemes__1", "bearer")
    .WithEnvironment("Webhook__Senders__container__Secrets__0", webhookHmacSecret)
    .WithEnvironment("Webhook__Senders__container__ClientId", "webhook-container")
    .WithArgs("--useHttps", "--cert", devCert.PfxPath, "--password", devCert.Password)
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/api/health")
    .WaitFor(storage)
    .WaitFor(rabbitmq)
    .WaitFor(minio)
    .WaitFor(keycloak)
    .WaitFor(api);

var repoRoot = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", ".."));

// Next.js serves HTTPS with the dev certificate. --experimental-https-ca is required too: without it Next drops
// NODE_EXTRA_CA_CERTS when it forks its server process, and the BFF could not call the Api over HTTPS.
var web = builder.AddNextJsApp("web", "../../web")
    .WithEnvironment("OTEL_COLLECTOR_ENDPOINT", observability.OtlpHttp)
    .WithBun()
    .WithEndpoint("http", e =>
    {
        e.UriScheme = "https";
        e.Port = 3000;
        e.IsProxied = false;
    })
    .WithArgs("--experimental-https", "--experimental-https-key", devCert.KeyPath, "--experimental-https-cert", devCert.PemPath, "--experimental-https-ca", devCert.PemPath)
    .WithEnvironment("API_URL", api.GetEndpoint("https"))
    .WithEnvironment("AUTH_URL", "https://localhost:3000")
    .WithEnvironment("AUTH_TRUST_HOST", "true")
    .WithEnvironment("AUTH_SECRET", authSecret)
    .WithEnvironment("AUTH_KEYCLOAK_ID", "web")
    .WithEnvironment("AUTH_KEYCLOAK_SECRET", webClientSecret)
    .WithEnvironment("AUTH_KEYCLOAK_ISSUER", issuer)
    .WithEnvironment("BRANDING_FILE", "config/branding.json")
    .WithExternalHttpEndpoints()
    .WithCommand(
        "regenerate-graphql",
        "Regenerate GraphQL types",
        async context =>
        {
            var export = await RunAsync("dotnet", "run --project src/DataHub.Api -- schema export --output web/schema.graphql", repoRoot, context.CancellationToken);
            if (export is not null)
            {
                return CommandResults.Failure(export);
            }

            var codegen = await RunAsync("bun", "run codegen", Path.Combine(repoRoot, "web"), context.CancellationToken);
            return codegen is null ? CommandResults.Success() : CommandResults.Failure(codegen);
        },
        new CommandOptions { IconName = "ArrowSync", Description = "Export the Api schema and run bun run codegen" })
    .WaitFor(api)
    .WaitFor(keycloak);

observability
    .Probe("api", api, "http", "/health")
    .Probe("webhook", webhook, "https", "/api/health")
    .Probe("web", web, "http", "/");

await builder.Build().RunAsync();

// Returns null on success, otherwise the tail of the output.
static async Task<string?> RunAsync(string file, string arguments, string workingDirectory, CancellationToken ct)
{
    using var process = Process.Start(new ProcessStartInfo(file, arguments)
    {
        WorkingDirectory = workingDirectory,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    })!;
    var output = process.StandardOutput.ReadToEndAsync(ct);
    var error = process.StandardError.ReadToEndAsync(ct);
    await process.WaitForExitAsync(ct);
    if (process.ExitCode == 0)
    {
        return null;
    }

    var log = await error + await output;
    return $"{file} exited {process.ExitCode}: {log[^Math.Min(log.Length, 1000)..]}";
}
