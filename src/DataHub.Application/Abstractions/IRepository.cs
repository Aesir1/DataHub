using System.Linq.Expressions;
using DataHub.Domain.Abstractions;

namespace DataHub.Application.Abstractions;

public interface IRepository<TEntity, in TKey>
    where TEntity : class, IEntity<TKey>
{
    /// <summary>No-tracking query for GraphQL paging, filtering and projection.</summary>
    IQueryable<TEntity> Query();

    /// <summary>Tracked entity, or null when missing.</summary>
    Task<TEntity?> GetByIdAsync(TKey id, CancellationToken ct = default);

    Task<IReadOnlyList<TEntity>> ListAsync(Expression<Func<TEntity, bool>>? predicate = null, CancellationToken ct = default);

    Task<TEntity> AddAsync(TEntity entity, CancellationToken ct = default);

    /// <summary>Throws NotFoundException when missing and ConcurrencyException when the row version changed.</summary>
    Task<TEntity> UpdateAsync(TEntity entity, CancellationToken ct = default);

    /// <summary>Soft delete when <typeparamref name="TEntity"/> is <see cref="ISoftDelete"/>; NotFoundException when missing.</summary>
    Task DeleteAsync(TKey id, CancellationToken ct = default);

    Task<bool> ExistsAsync(TKey id, CancellationToken ct = default);
}
