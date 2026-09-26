using System.Linq.Expressions;
using DataHub.Application.Abstractions;
using DataHub.Domain.Abstractions;
using DataHub.Domain.Exceptions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DataHub.Infrastructure.Persistence;

/// <summary>Full CRUD for any entity; entity-specific repositories inherit and override only what differs.</summary>
public class Repository<TEntity, TKey>(AppDbContext db) : IRepository<TEntity, TKey>
    where TEntity : class, IEntity<TKey>
{
    protected AppDbContext Db { get; } = db;

    protected DbSet<TEntity> Set => Db.Set<TEntity>();

    public virtual IQueryable<TEntity> Query() => Set.AsNoTracking();

    public virtual Task<TEntity?> GetByIdAsync(TKey id, CancellationToken ct = default) => Set.FindAsync([id], ct).AsTask();

    public virtual async Task<IReadOnlyList<TEntity>> ListAsync(Expression<Func<TEntity, bool>>? predicate = null, CancellationToken ct = default) =>
        await (predicate is null ? Query() : Query().Where(predicate)).ToListAsync(ct);

    public virtual async Task<TEntity> AddAsync(TEntity entity, CancellationToken ct = default)
    {
        Set.Add(entity);
        await SaveAsync(ct);
        return entity;
    }

    public virtual async Task<TEntity> UpdateAsync(TEntity entity, CancellationToken ct = default)
    {
        var entry = Db.Entry(entity);
        if (entry.State == EntityState.Detached)
        {
            if (!await ExistsAsync(entity.Id, ct))
            {
                throw NotFoundException.For<TEntity>(entity.Id!);
            }

            Set.Update(entity);
        }

        if (entity is AuditableEntity auditable)
        {
            // Check against the version the caller read, not the one just loaded. The early check also catches
            // stale no-op updates (EF sends no UPDATE, so the xmin WHERE clause would never run).
            var version = entry.Property(nameof(AuditableEntity.RowVersion));
            if (entry.State != EntityState.Modified && entry.State != EntityState.Unchanged)
            {
                version.OriginalValue = auditable.RowVersion;
            }
            else if (!Equals(version.OriginalValue, auditable.RowVersion))
            {
                throw new ConcurrencyException($"{typeof(TEntity).Name} was changed by someone else. Reload and try again.");
            }

            version.IsModified = false;
        }

        await SaveAsync(ct);
        return entity;
    }

    public virtual async Task DeleteAsync(TKey id, CancellationToken ct = default)
    {
        var entity = await GetByIdAsync(id, ct) ?? throw NotFoundException.For<TEntity>(id!);
        if (entity is ISoftDelete soft)
        {
            soft.IsDeleted = true;
            soft.DeletedAtUtc = DateTimeOffset.UtcNow;
        }
        else
        {
            Set.Remove(entity);
        }

        await SaveAsync(ct);
    }

    public virtual Task<bool> ExistsAsync(TKey id, CancellationToken ct = default) =>
        Set.AnyAsync(e => EF.Property<TKey>(e, nameof(IEntity<TKey>.Id))!.Equals(id), ct);

    /// <summary>SaveChanges with EF/Npgsql failures translated to domain exceptions.</summary>
    protected async Task SaveAsync(CancellationToken ct)
    {
        try
        {
            await Db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new ConcurrencyException($"{typeof(TEntity).Name} was changed by someone else. Reload and try again. ({ex.Entries.Count} row)");
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg)
        {
            throw new ConflictException($"{typeof(TEntity).Name} violates unique constraint {pg.ConstraintName}.");
        }
    }
}
