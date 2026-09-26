using System.Diagnostics;
using Aspire.Hosting.ApplicationModel;
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

var postgres = builder.AddPostgres("postgres", password: postgresPassword, port: 5432)
    .WithLifetime(ContainerLifetime.Persistent)
    .WithEndpointProxySupport(false)
    .WithDataVolume("datahub-postgres")
    .WithPgAdmin(p => p.WithLifetime(ContainerLifetime.Persistent).WithHostPort(5050));
var appDb = postgres.AddDatabase("app");
var authDb = postgres.AddDatabase("auth");
var keycloakDb = postgres.AddDatabase("keycloak-db", databaseName: "keycloak");

var keycloak = builder.AddKeycloak("keycloak", 8080, adminPassword: keycloakAdminPassword)
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
    .WaitFor(keycloakDb);
var keycloakHttp = keycloak.GetEndpoint("http");
var issuer = ReferenceExpression.Create($"{keycloakHttp}/realms/{realm}");

// minio/minio is no longer published on Docker Hub; Chainguard's build is free and multi-arch.
var minio = builder.AddMinioContainer("minio", rootPassword: minioPassword, port: 9000)
    .WithImageRegistry("cgr.dev")
    .WithImage("chainguard/minio")
    .WithImageTag("latest")
    .WithLifetime(ContainerLifetime.Persistent)
    .WithEndpointProxySupport(false)
    .WithEndpoint("console", e => e.Port = 9001)
    .WithDataVolume("datahub-minio");

var rabbitmq = builder.AddRabbitMQ("rabbitmq", password: rabbitPassword, port: 5672)
    .WithLifetime(ContainerLifetime.Persistent)
    .WithEndpointProxySupport(false)
    .WithManagementPlugin(port: 15672)
    .WithDataVolume("datahub-rabbitmq");

var storage = builder.AddAzureStorage("storage")
    .RunAsEmulator(e => e.WithLifetime(ContainerLifetime.Persistent).WithDataVolume("datahub-azurite"));

var migrate = builder.AddProject<Projects.DataHub_DbUtils>("dbutils-migrate")
    .WithArgs("migrate", "--context", "All")
    .WithReference(appDb)
    .WithReference(authDb)
    .WithReference(minio)
    .WaitFor(appDb)
    .WaitFor(authDb);

// Destructive: drops and reseeds App, Auth and the Keycloak dev users. Start it from the dashboard.
builder.AddProject<Projects.DataHub_DbUtils>("dbutils-seed")
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

var api = builder.AddProject<Projects.DataHub_Api>("api")
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
builder.AddAzureFunctionsProject<Projects.DataHub_Webhook>("webhook")
    .WithHostStorage(storage)
    .WithReference(rabbitmq)
    .WithReference(minio)
    .WithEnvironment("Webhook__Authority", issuer)
    .WithEnvironment("Webhook__Senders__container__Schemes__0", "hmac")
    .WithEnvironment("Webhook__Senders__container__Schemes__1", "bearer")
    .WithEnvironment("Webhook__Senders__container__Secrets__0", webhookHmacSecret)
    .WithEnvironment("Webhook__Senders__container__ClientId", "webhook-container")
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/api/health")
    .WaitFor(storage)
    .WaitFor(rabbitmq)
    .WaitFor(minio)
    .WaitFor(keycloak)
    .WaitFor(api);

var repoRoot = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", ".."));
builder.AddNextJsApp("web", "../../web")
    .WithBun()
    .WithEndpoint("http", e =>
    {
        e.Port = 3000;
        e.IsProxied = false;
    })
    .WithEnvironment("API_URL", api.GetEndpoint("http"))
    .WithEnvironment("AUTH_URL", "http://localhost:3000")
    .WithEnvironment("AUTH_TRUST_HOST", "true")
    .WithEnvironment("AUTH_SECRET", authSecret)
    .WithEnvironment("AUTH_KEYCLOAK_ID", "web")
    .WithEnvironment("AUTH_KEYCLOAK_SECRET", webClientSecret)
    .WithEnvironment("AUTH_KEYCLOAK_ISSUER", issuer)
    .WithEnvironment("MINIO_PUBLIC_URL", minio.GetEndpoint("http"))
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
