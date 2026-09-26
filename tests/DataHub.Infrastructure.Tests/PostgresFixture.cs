using DataHub.Application.Abstractions;
using DataHub.Infrastructure.Persistence;
using DataHub.Infrastructure.Persistence.Interceptors;
using DataHub.Infrastructure.Tests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Respawn;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(PostgresFixture))]

namespace DataHub.Infrastructure.Tests;

/// <summary>TE-3: one PostgreSQL container per test assembly, migrated once; <see cref="ResetAsync"/> empties it between tests.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private Respawner respawner = null!;

    public string ConnectionString => container.GetConnectionString();

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 9, 25, 10, 0, 0, TimeSpan.Zero));

    public async ValueTask InitializeAsync()
    {
        await container.StartAsync();
        await using (var db = Create())
        {
            await db.Database.MigrateAsync();
        }

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        respawner = await Respawner.CreateAsync(connection, new RespawnerOptions
        {
            DbAdapter = DbAdapter.Postgres,
            TablesToIgnore = ["__EFMigrationsHistory"],
        });
    }

    /// <summary>A context wired like production: audit + outbox interceptors.</summary>
    public AppDbContext Create(ICurrentUser? user = null) => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseNpgsql(ConnectionString)
        .AddInterceptors(new AuditInterceptor(user ?? new SystemCurrentUser(), Time), new OutboxInterceptor(Time))
        .Options);

    public async Task ResetAsync()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await respawner.ResetAsync(connection);
    }

    public ValueTask DisposeAsync() => container.DisposeAsync();
}
