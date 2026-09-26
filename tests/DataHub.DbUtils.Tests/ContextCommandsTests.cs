using DataHub.Application.Abstractions;
using DataHub.Auth;
using DataHub.Auth.Persistence;
using DataHub.DbUtils.Contexts;
using DataHub.Domain.Documents;
using DataHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Testcontainers.PostgreSql;

namespace DataHub.DbUtils.Tests;

/// <summary>DU-2/DU-3: migrate, check, reset, idempotent seeds and maintenance for both contexts.</summary>
public sealed class ContextCommandsTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private readonly IObjectStorage storage = Substitute.For<IObjectStorage>();
    private readonly FakeTimeProvider time = new(new DateTimeOffset(2026, 9, 25, 10, 0, 0, TimeSpan.Zero));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await postgres.StartAsync(Ct);

    public async ValueTask DisposeAsync() => await postgres.DisposeAsync();

    private string Db(string name) => new NpgsqlConnectionStringBuilder(postgres.GetConnectionString()) { Database = name }.ConnectionString;

    private AppContextCommands App(out AppDbContext db)
    {
        db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(Db("app")).Options);
        return new AppContextCommands(db, storage, time, NullLogger<AppContextCommands>.Instance);
    }

    private AuthContextCommands Auth(out AuthDbContext db)
    {
        db = new AuthDbContext(new DbContextOptionsBuilder<AuthDbContext>()
            .UseNpgsql(Db("auth"), o => o.MigrationsHistoryTable("__EFMigrationsHistory", AuthDbContext.Schema)).Options);
        return new AuthContextCommands(db, NullLogger<AuthContextCommands>.Instance);
    }

    [Fact]
    public async Task App_migrate_check_seed_twice_and_reset()
    {
        var commands = App(out var db);
        await using var owned = db;

        (await commands.PendingMigrationsAsync(Ct)).ShouldNotBeEmpty();
        (await commands.MigrateAsync(Ct)).ShouldBeGreaterThan(0);
        (await commands.PendingMigrationsAsync(Ct)).ShouldBeEmpty();

        (await commands.SeedFixturesAsync(Ct)).ShouldBe(50); // 2 containers + 48 readings
        (await commands.SeedFixturesAsync(Ct)).ShouldBe(0);
        (await commands.SeedMasterDataAsync(Ct)).ShouldBe(3);
        (await commands.SeedMasterDataAsync(Ct)).ShouldBe(0);

        var product = await db.Products.FirstAsync(Ct);
        product.Price = 1m;
        await db.SaveChangesAsync(Ct);
        (await commands.SeedMasterDataAsync(Ct)).ShouldBe(1); // upsert restores reference data

        (await commands.MaintainAsync(new MaintainOptions(VacuumAnalyze: true, null, false, false), Ct)).ShouldBeGreaterThan(0);

        (await commands.ResetAsync(Ct)).ShouldBe(1);
        (await commands.PendingMigrationsAsync(Ct)).ShouldNotBeEmpty();
    }

    [Fact]
    public async Task App_purges_old_soft_deleted_rows_and_reports_orphans()
    {
        var commands = App(out var db);
        await using var owned = db;
        await commands.MigrateAsync(Ct);
        var owner = Guid.CreateVersion7();
        var kept = new Document { OwnerId = owner, FileName = "kept.pdf", ContentType = "application/pdf" };
        var old = new Document { OwnerId = owner, FileName = "old.pdf", ContentType = "application/pdf", IsDeleted = true, DeletedAtUtc = time.GetUtcNow().AddDays(-100) };
        var recent = new Document { OwnerId = owner, FileName = "recent.pdf", ContentType = "application/pdf", IsDeleted = true, DeletedAtUtc = time.GetUtcNow().AddDays(-1) };
        db.Documents.AddRange(kept, old, recent);
        await db.SaveChangesAsync(Ct);
        storage.ListAsync(Document.Bucket, null, Arg.Any<CancellationToken>()).Returns([new ObjectInfo(kept.ObjectKey, 1, null), new ObjectInfo("x/orphan", 2, null)]);

        (await commands.MaintainAsync(new MaintainOptions(false, TimeSpan.FromDays(90), OrphanObjects: true, DeleteOrphans: true), Ct)).ShouldBe(2);

        (await db.Documents.IgnoreQueryFilters().Select(d => d.FileName).OrderBy(n => n).ToListAsync(Ct)).ShouldBe(["kept.pdf", "recent.pdf"]);
        await storage.Received(1).DeleteAsync(Document.Bucket, "x/orphan", Arg.Any<CancellationToken>());
        await storage.DidNotReceive().DeleteAsync(Document.Bucket, kept.ObjectKey, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Auth_master_data_seeds_roles_and_permissions_idempotently()
    {
        var commands = Auth(out var db);
        await using var owned = db;
        await commands.MigrateAsync(Ct);

        (await commands.SeedMasterDataAsync(Ct)).ShouldBeGreaterThan(0);
        (await commands.SeedMasterDataAsync(Ct)).ShouldBe(0);

        var admin = await db.Roles.SingleAsync(r => r.Name == Roles.PlatformAdmin, Ct);
        (await db.RoleClaims.Where(c => c.RoleId == admin.Id).Select(c => c.ClaimValue).ToListAsync(Ct)).ShouldBe(Permissions.All, ignoreOrder: true);
        (await commands.SeedFixturesAsync(Ct)).ShouldBe(0);
    }
}
