using System.ComponentModel.DataAnnotations;
using DataHub.Application.Abstractions;
using DataHub.Domain.Abstractions;
using DataHub.Domain.Exceptions;
using DomainValidationException = DataHub.Domain.Exceptions.ValidationException;

namespace DataHub.Application.Crud;

/// <summary>
/// Validation (DataAnnotations on the DTOs) and mapping on top of <see cref="IRepository{TEntity,TKey}"/>.
/// A new entity gets CRUD by subclassing this and implementing the two mapping methods.
/// </summary>
public abstract class CrudService<TEntity, TKey, TCreateDto, TUpdateDto>(IRepository<TEntity, TKey> repository)
    where TEntity : class, IEntity<TKey>
    where TCreateDto : class
    where TUpdateDto : class
{
    protected IRepository<TEntity, TKey> Repository { get; } = repository;

    public IQueryable<TEntity> Query() => Repository.Query();

    public Task<TEntity?> GetAsync(TKey id, CancellationToken ct = default) => Repository.GetByIdAsync(id, ct);

    public virtual async Task<TEntity> CreateAsync(TCreateDto dto, CancellationToken ct = default)
    {
        DtoValidator.Validate(dto);
        return await Repository.AddAsync(Map(dto), ct);
    }

    public virtual async Task<TEntity> UpdateAsync(TKey id, TUpdateDto dto, CancellationToken ct = default)
    {
        DtoValidator.Validate(dto);
        var entity = await Repository.GetByIdAsync(id, ct) ?? throw NotFoundException.For<TEntity>(id!);
        Apply(dto, entity);
        return await Repository.UpdateAsync(entity, ct);
    }

    public virtual Task DeleteAsync(TKey id, CancellationToken ct = default) => Repository.DeleteAsync(id, ct);

    protected abstract TEntity Map(TCreateDto dto);

    protected abstract void Apply(TUpdateDto dto, TEntity entity);
}

/// <summary>Runs DataAnnotations and throws the domain ValidationException with every failing member.</summary>
public static class DtoValidator
{
    public static void Validate(object dto)
    {
        var results = new List<ValidationResult>();
        if (!Validator.TryValidateObject(dto, new ValidationContext(dto), results, validateAllProperties: true))
        {
            throw new DomainValidationException(results
                .SelectMany(r => r.MemberNames.DefaultIfEmpty(string.Empty), (r, m) => (Member: m, r.ErrorMessage))
                .GroupBy(x => x.Member)
                .ToDictionary(g => g.Key, g => g.Select(x => x.ErrorMessage ?? "Invalid").ToArray()));
        }
    }
}
