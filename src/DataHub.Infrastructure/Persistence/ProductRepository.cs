using DataHub.Application.Products;
using DataHub.Domain.Products;

namespace DataHub.Infrastructure.Persistence;

/// <summary>Override example: archived products are hidden from every query.</summary>
public sealed class ProductRepository(AppDbContext db) : Repository<Product, Guid>(db), IProductRepository
{
    public override IQueryable<Product> Query() => base.Query().Where(p => !p.IsArchived);
}
