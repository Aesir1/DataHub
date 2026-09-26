using System.Diagnostics;
using System.Text.Json;
using DataHub.Domain.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace DataHub.Infrastructure.Persistence.Interceptors;

/// <summary>
/// Transactional outbox (MQ-4): every change to an <see cref="IPublishesChanges"/> entity adds an
/// <see cref="OutboxMessage"/> to the same SaveChanges, so the event exists if and only if the change committed.
/// </summary>
public sealed class OutboxInterceptor(TimeProvider time) : SaveChangesInterceptor
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Collect(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Collect(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public static ChangeType? ChangeOf(EntityState state, object entity, bool softDeleteTurnedOn) => state switch
    {
        EntityState.Added => ChangeType.Created,
        EntityState.Deleted => ChangeType.Deleted,
        EntityState.Modified when softDeleteTurnedOn => ChangeType.Deleted,
        EntityState.Modified when entity is ISoftDelete { IsDeleted: true } => null,
        EntityState.Modified => ChangeType.Updated,
        _ => null,
    };

    private void Collect(DbContext? db)
    {
        if (db is null)
        {
            return;
        }

        var messages = new List<OutboxMessage>();
        foreach (var entry in db.ChangeTracker.Entries().Where(e => e.Entity is IPublishesChanges).ToList())
        {
            var softDeleteTurnedOn = entry.Entity is ISoftDelete
                && entry.State == EntityState.Modified
                && entry.Property(nameof(ISoftDelete.IsDeleted)) is { IsModified: true, CurrentValue: true };
            if (ChangeOf(entry.State, entry.Entity, softDeleteTurnedOn) is not { } change)
            {
                continue;
            }

            var entity = (IPublishesChanges)entry.Entity;
            var payload = entity.ToChangedEvent(change);
            messages.Add(new OutboxMessage
            {
                Type = payload.GetType().Name,
                RoutingKey = $"{entity.EventName}.{change.ToString().ToLowerInvariant()}",
                Payload = JsonSerializer.Serialize(payload, payload.GetType(), Json),
                TraceParent = Activity.Current?.Id,
                OccurredAtUtc = time.GetUtcNow(),
            });
        }

        db.Set<OutboxMessage>().AddRange(messages);
    }
}
