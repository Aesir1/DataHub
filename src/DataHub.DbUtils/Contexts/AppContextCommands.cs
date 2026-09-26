using System.Globalization;
using DataHub.Application.Abstractions;
using DataHub.Domain.Containers;
using DataHub.Domain.Documents;
using DataHub.Domain.Products;
using DataHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DataHub.DbUtils.Contexts;

public sealed class AppContextCommands(AppDbContext db, IObjectStorage storage, TimeProvider time, ILogger<AppContextCommands> logger)
    : ContextCommands<AppDbContext>(db, logger)
{
    private static readonly DateTimeOffset Start = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    public override string Name => "App";

    /// <summary>Two containers with 24 hourly readings each; fixed ids, so a second run inserts nothing.</summary>
    public override async Task<int> SeedFixturesAsync(CancellationToken ct)
    {
        var containers = new[]
        {
            new Container { Id = Guid.Parse("0199a000-0000-7000-8000-000000000001"), Code = "MSCU1234565", CreatedAtUtc = Start },
            new Container { Id = Guid.Parse("0199a000-0000-7000-8000-000000000002"), Code = "MAEU7654321", CreatedAtUtc = Start },
        };

        var inserted = 0;
        for (var i = 0; i < containers.Length; i++)
        {
            var container = containers[i];
            if (await Db.Containers.AnyAsync(c => c.Id == container.Id, ct))
            {
                continue;
            }

            for (var hour = 0; hour < 24; hour++)
            {
                container.Temperatures.Add(new Temperature
                {
                    Id = Guid.Parse($"0199a000-0001-7000-8{i + 1:D3}-{hour:D12}", CultureInfo.InvariantCulture),
                    TimestampUtc = Start.AddHours(hour),
                    Celsius = Math.Round(4m + (decimal)Math.Sin(hour / 4.0), 3),
                });
            }

            Db.Containers.Add(container);
            inserted += 1 + container.Temperatures.Count;
        }

        await Db.SaveChangesAsync(ct);
        return inserted;
    }

    /// <summary>Upserts the reference product catalog from embedded <c>products.json</c>.</summary>
    public override async Task<int> SeedMasterDataAsync(CancellationToken ct)
    {
        var changed = 0;
        foreach (var item in ReadMasterData<List<ProductSeed>>("products.json"))
        {
            var product = await Db.Products.IgnoreQueryFilters().SingleOrDefaultAsync(p => p.Id == item.Id, ct);
            if (product is null)
            {
                Db.Products.Add(new Product { Id = item.Id, Name = item.Name, Sku = item.Sku, Price = item.Price });
                changed++;
            }
            else if (product.Name != item.Name || product.Sku != item.Sku || product.Price != item.Price || product.IsDeleted)
            {
                (product.Name, product.Sku, product.Price, product.IsDeleted, product.DeletedAtUtc) = (item.Name, item.Sku, item.Price, false, null);
                changed++;
            }
        }

        await Db.SaveChangesAsync(ct);
        return changed;
    }

    public override async Task<int> MaintainAsync(MaintainOptions options, CancellationToken ct)
    {
        var affected = await base.MaintainAsync(options, ct);
        if (options.PurgeSoftDeletedOlderThan is { } age)
        {
            var cutoff = time.GetUtcNow() - age;
            var products = await Db.Products.IgnoreQueryFilters().Where(p => p.IsDeleted && p.DeletedAtUtc < cutoff).ExecuteDeleteAsync(ct);
            var documents = await Db.Documents.IgnoreQueryFilters().Where(d => d.IsDeleted && d.DeletedAtUtc < cutoff).ExecuteDeleteAsync(ct);
            Logger.LogInformation("Purged {Products} products and {Documents} documents deleted before {Cutoff}", products, documents, cutoff);
            affected += products + documents;
        }

        if (options.OrphanObjects)
        {
            affected += await OrphanObjectsAsync(options.DeleteOrphans, ct);
        }

        return affected;
    }

    /// <summary>Objects in the documents bucket that no live Document row references.</summary>
    public async Task<int> OrphanObjectsAsync(bool delete, CancellationToken ct)
    {
        var objects = await storage.ListAsync(Document.Bucket, ct: ct);
        var live = (await Db.Documents.Select(d => new { d.OwnerId, d.Id }).ToListAsync(ct))
            .Select(d => $"{d.OwnerId}/{d.Id}")
            .ToHashSet(StringComparer.Ordinal);
        var orphans = objects.Where(o => !live.Contains(o.Key)).ToList();
        foreach (var orphan in orphans)
        {
            Logger.LogInformation("Orphan object {Key} ({Bytes} bytes){Action}", orphan.Key, orphan.SizeBytes, delete ? " deleted" : string.Empty);
            if (delete)
            {
                await storage.DeleteAsync(Document.Bucket, orphan.Key, ct);
            }
        }

        return orphans.Count;
    }

    private sealed record ProductSeed(Guid Id, string Name, string Sku, decimal Price);
}
