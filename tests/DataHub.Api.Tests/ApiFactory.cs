extern alias dbutils;

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using DataHub.Api.Tests;
using DataHub.Auth.Persistence;
using DataHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Testcontainers.Minio;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using AuthContextCommands = dbutils::DataHub.DbUtils.Contexts.AuthContextCommands;

[assembly: AssemblyFixture(typeof(ApiFactory))]

namespace DataHub.Api.Tests;

/// <summary>
/// The real Api (WebApplicationFactory&lt;Program&gt;) against PostgreSQL, RabbitMQ and MinIO containers.
/// Keycloak is replaced by a local RSA key: tokens are signed here and validated by the real JwtBearer handler.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string Issuer = "http://keycloak.test/realms/datahub";

    private static readonly RsaSecurityKey Key = new(RSA.Create(2048)) { KeyId = "test" };

    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private readonly RabbitMqContainer rabbit = new RabbitMqBuilder("rabbitmq:4-management").WithPortBinding(15672, true).Build();
    private readonly MinioContainer minio = new MinioBuilder("cgr.dev/chainguard/minio:latest").Build();

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(postgres.StartAsync(), rabbit.StartAsync(), minio.StartAsync());
        await using (var connection = new NpgsqlConnection(postgres.GetConnectionString()))
        {
            await connection.OpenAsync();
            await using var create = new NpgsqlCommand("CREATE DATABASE auth", connection);
            await create.ExecuteNonQueryAsync();
        }

        await using (var app = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(Db("postgres")).Options))
        {
            await app.Database.MigrateAsync();
        }

        await using var auth = new AuthDbContext(new DbContextOptionsBuilder<AuthDbContext>()
            .UseNpgsql(Db("auth"), o => o.MigrationsHistoryTable("__EFMigrationsHistory", AuthDbContext.Schema)).Options);
        await auth.Database.MigrateAsync();
        await new AuthContextCommands(auth, NullLogger<AuthContextCommands>.Instance).SeedMasterDataAsync(CancellationToken.None);
    }

    public new async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await Task.WhenAll(postgres.DisposeAsync().AsTask(), rabbit.DisposeAsync().AsTask(), minio.DisposeAsync().AsTask());
    }

    public string AppConnectionString => Db("postgres");

    /// <summary>HttpClient with a bearer token for a user holding <paramref name="roles"/>.</summary>
    public HttpClient ClientFor(string email, params string[] roles)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(email, roles));
        return client;
    }

    public static string Token(string email, string[] roles, string audience = "api") =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = audience,
            Expires = DateTime.UtcNow.AddMinutes(5),
            Claims = new Dictionary<string, object>
            {
                ["sub"] = "kc-" + email,
                ["email"] = email,
                ["preferred_username"] = email,
                ["realm_access"] = new Dictionary<string, object> { ["roles"] = roles },
            },
            SigningCredentials = new SigningCredentials(Key, SecurityAlgorithms.RsaSha256),
        });

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:app", Db("postgres"));
        builder.UseSetting("ConnectionStrings:auth", Db("auth"));
        builder.UseSetting("ConnectionStrings:rabbitmq", rabbit.GetConnectionString());
        builder.UseSetting("ConnectionStrings:minio", $"Endpoint={minio.GetConnectionString().TrimEnd('/')};AccessKey={minio.GetAccessKey()};SecretKey={minio.GetSecretKey()}");
        builder.UseSetting("Messaging:ManagementUrl", $"http://{rabbit.Hostname}:{rabbit.GetMappedPublicPort(15672)}");
        builder.UseSetting("services:keycloak:http:0", "http://keycloak.test");
        builder.ConfigureServices(services => services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o =>
        {
            var config = new OpenIdConnectConfiguration { Issuer = Issuer };
            config.SigningKeys.Add(Key);
            o.Configuration = config;
            o.ConfigurationManager = null;
            o.TokenValidationParameters.ValidIssuer = Issuer;
            o.TokenValidationParameters.IssuerSigningKey = Key;
        }));
    }

    private string Db(string name) => new NpgsqlConnectionStringBuilder(postgres.GetConnectionString()) { Database = name }.ConnectionString;
}

public static class GraphQlClient
{
    public static async Task<JsonNode> Gql(this HttpClient client, string query, object? variables = null)
    {
        using var response = await client.PostAsJsonAsync("/graphql", new { query, variables }, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return JsonNode.Parse(body) ?? throw new InvalidOperationException($"{response.StatusCode}: {body}");
    }

    /// <summary>The <c>data</c> object; fails with the whole response when there are top-level errors.</summary>
    public static JsonNode Data(this JsonNode response)
    {
        response["errors"].ShouldBeNull(response.Json());
        return response["data"]!;
    }

    public static string Json(this JsonNode? node) => node?.ToJsonString(new JsonSerializerOptions { WriteIndented = false }) ?? "null";
}
