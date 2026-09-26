using DataHub.Application.Abstractions;
using DataHub.Application.Products;
using DataHub.Auth;
using DataHub.Domain.Products;
using DataHub.Infrastructure.Persistence;
using GreenDonut;
using HotChocolate.Authorization;
using Microsoft.EntityFrameworkCore;

namespace DataHub.Api.GraphQl.Products;

[ExtendObjectType(typeof(RootQuery))]
public class ProductQueries : RootQuery
{
    [Authorize(Policy = Permissions.Products.Read)]
    [UsePaging(MaxPageSize = 100, IncludeTotalCount = true)]
    [UseProjection]
    [UseFiltering]
    [UseSorting]
    public IQueryable<Product> GetProducts([Service] IProductRepository products) => products.Query().OrderBy(p => p.Name);

    [Authorize(Policy = Permissions.Products.Read)]
    [UseFirstOrDefault]
    [UseProjection]
    public IQueryable<Product> GetProduct(Guid id, [Service] IProductRepository products) => products.Query().Where(p => p.Id == id);

    /// <summary>Changes of one product as recorded by the audit consumer, newest first.</summary>
    [Authorize(Policy = Permissions.Products.Read)]
    [UsePaging(MaxPageSize = 100)]
    [UseProjection]
    public IQueryable<ProductAudit> GetProductAudit(Guid productId, [Service] IRepository<ProductAudit, Guid> audits) =>
        audits.Query().Where(a => a.ProductId == productId).OrderByDescending(a => a.OccurredAtUtc).ThenByDescending(a => a.Id);
}

public sealed class ProductType : ObjectType<Product>
{
    protected override void Configure(IObjectTypeDescriptor<Product> descriptor)
    {
        descriptor.Ignore(p => p.IsDeleted);
        descriptor.Ignore(p => p.DeletedAtUtc);
        descriptor.Ignore(p => p.EventName);
        descriptor.Ignore(p => p.ToChangedEvent(default));
        descriptor.Field(p => p.RowVersion).Type<NonNullType<UnsignedIntType>>();
    }
}

public sealed class ProductAuditType : ObjectType<ProductAudit>
{
    protected override void Configure(IObjectTypeDescriptor<ProductAudit> descriptor)
    {
        descriptor.Field(a => a.ProductId).IsProjected(true);
        descriptor.Field("product")
            .Type<ProductType>()
            .Resolve(ctx => ctx.DataLoader<IProductByIdDataLoader>().LoadAsync(ctx.Parent<ProductAudit>().ProductId, ctx.RequestAborted));
    }
}

public static class ProductDataLoaders
{
    /// <summary>Batches ProductAudit.product; includes archived and soft-deleted products (history must still resolve).</summary>
    [DataLoader]
    public static async Task<IReadOnlyDictionary<Guid, Product>> GetProductByIdAsync(
        IReadOnlyList<Guid> ids,
        IServiceScopeFactory scopes,
        CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Products.AsNoTracking().IgnoreQueryFilters()
            .Where(p => ids.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, ct);
    }
}
