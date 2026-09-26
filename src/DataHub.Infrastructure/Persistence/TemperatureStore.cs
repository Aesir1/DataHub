using DataHub.Application.Ingestion;
using DataHub.Domain.Containers;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DataHub.Infrastructure.Persistence;

public sealed class TemperatureStore(AppDbContext db) : ITemperatureStore
{
    public async Task<bool> SaveAsync(string messageId, ContainerReadings readings, CancellationToken ct = default)
    {
        try
        {
            return await SaveOnceAsync(messageId, readings, ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // A concurrent consumer created the same container (or processed the same message) first; the retry sees its row.
            db.ChangeTracker.Clear();
            return await SaveOnceAsync(messageId, readings, ct);
        }
    }

    private async Task<bool> SaveOnceAsync(string messageId, ContainerReadings readings, CancellationToken ct)
    {
        if (await db.ProcessedMessages.AnyAsync(m => m.Id == messageId, ct))
        {
            return false;
        }

        var container = await db.Containers.SingleOrDefaultAsync(c => c.Code == readings.ContainerCode, ct);
        if (container is null)
        {
            container = new Container { Code = readings.ContainerCode };
            db.Containers.Add(container);
        }

        db.Temperatures.AddRange(readings.Readings.Select(r => new Temperature
        {
            ContainerId = container.Id,
            TimestampUtc = r.TimestampUtc,
            Celsius = r.Celsius,
        }));
        db.ProcessedMessages.Add(new ProcessedMessage { Id = messageId });

        // SaveChanges wraps all inserts in one transaction.
        await db.SaveChangesAsync(ct);
        return true;
    }
}
