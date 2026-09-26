using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DataHub.DbUtils.Contexts;

public sealed record MaintainOptions(bool VacuumAnalyze, TimeSpan? PurgeSoftDeletedOlderThan, bool OrphanObjects, bool DeleteOrphans);

/// <summary>
/// DU-2: one subclass per DbContext. Migrate/check/reset/vacuum are shared; seeding and maintenance are virtual.
/// Every operation returns the number of affected rows or objects.
/// </summary>
public abstract class ContextCommands<TDbContext>(TDbContext db, ILogger logger)
    where TDbContext : DbContext
{
    public abstract string Name { get; }

    protected TDbContext Db { get; } = db;

    protected ILogger Logger { get; } = logger;

    public async Task<IReadOnlyList<string>> PendingMigrationsAsync(CancellationToken ct) =>
        (await Db.Database.GetPendingMigrationsAsync(ct)).ToList();

    public async Task<int> MigrateAsync(CancellationToken ct)
    {
        var pending = await PendingMigrationsAsync(ct);
        await Db.Database.MigrateAsync(ct);
        return pending.Count;
    }

    public async Task<int> ResetAsync(CancellationToken ct) => await Db.Database.EnsureDeletedAsync(ct) ? 1 : 0;

    public virtual Task<int> SeedFixturesAsync(CancellationToken ct) => Task.FromResult(0);

    public virtual Task<int> SeedMasterDataAsync(CancellationToken ct) => Task.FromResult(0);

    public virtual async Task<int> MaintainAsync(MaintainOptions options, CancellationToken ct) =>
        options.VacuumAnalyze ? await VacuumAnalyzeAsync(ct) : 0;

    protected async Task<int> VacuumAnalyzeAsync(CancellationToken ct)
    {
        var tables = Db.Model.GetEntityTypes()
            .Where(t => t.GetTableName() is not null && t.GetViewName() is null)
            .Select(t => (Schema: t.GetSchema(), Table: t.GetTableName()!))
            .Distinct()
            .ToList();
        foreach (var (schema, table) in tables)
        {
            // Identifiers come from the EF model, never from user input.
#pragma warning disable EF1002
            await Db.Database.ExecuteSqlRawAsync(
                schema is null ? $"VACUUM (ANALYZE) \"{table}\"" : $"VACUUM (ANALYZE) \"{schema}\".\"{table}\"",
                ct);
#pragma warning restore EF1002
        }

        return tables.Count;
    }

    protected static T ReadMasterData<T>(string fileName)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"DataHub.DbUtils.MasterData.{fileName}")
            ?? throw new FileNotFoundException($"Embedded master data {fileName} not found.");
        return JsonSerializer.Deserialize<T>(stream, JsonSerializerOptions.Web) ?? throw new InvalidDataException($"{fileName} is empty.");
    }
}
