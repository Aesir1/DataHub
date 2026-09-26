using DataHub.Application.Abstractions;
using DataHub.Domain.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace DataHub.Infrastructure.Persistence.Interceptors;

/// <summary>Sets Created*/Updated* on every <see cref="AuditableEntity"/> (CR-3). Repositories never touch these.</summary>
public sealed class AuditInterceptor(ICurrentUser user, TimeProvider time) : SaveChangesInterceptor
{
    public const string System = "system";

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Stamp(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Stamp(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private void Stamp(DbContext? db)
    {
        if (db is null)
        {
            return;
        }

        var now = time.GetUtcNow();
        var who = user.Email ?? user.Id?.ToString() ?? System;
        foreach (var entry in db.ChangeTracker.Entries<AuditableEntity>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    if (entry.Entity.CreatedAtUtc == default)
                    {
                        entry.Entity.CreatedAtUtc = now;
                    }

                    entry.Entity.CreatedBy ??= who;
                    break;
                case EntityState.Modified:
                    entry.Entity.UpdatedAtUtc = now;
                    entry.Entity.UpdatedBy = who;
                    entry.Property(e => e.CreatedAtUtc).IsModified = false;
                    entry.Property(e => e.CreatedBy).IsModified = false;
                    break;
            }
        }
    }
}
