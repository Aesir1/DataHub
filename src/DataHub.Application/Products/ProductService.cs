using System.ComponentModel.DataAnnotations;
using DataHub.Application.Abstractions;
using DataHub.Application.Crud;
using DataHub.Domain.Products;

namespace DataHub.Application.Products;

public sealed record CreateProductDto(
    [property: Required, StringLength(200, MinimumLength = 1)] string Name,
    [property: Required, RegularExpression("^[A-Z0-9-]{3,32}$", ErrorMessage = "SKU must be 3-32 characters A-Z, 0-9 or '-'.")] string Sku,
    [property: Range(0, 1_000_000)] decimal Price);

/// <summary><paramref name="RowVersion"/> is the value read by the client; a mismatch raises ConcurrencyException.</summary>
public sealed record UpdateProductDto(
    [property: Required, StringLength(200, MinimumLength = 1)] string Name,
    [property: Range(0, 1_000_000)] decimal Price,
    bool IsArchived,
    uint RowVersion);

public interface IProductRepository : IRepository<Product, Guid>;

public sealed class ProductService(IProductRepository products)
    : CrudService<Product, Guid, CreateProductDto, UpdateProductDto>(products)
{
    protected override Product Map(CreateProductDto dto) => new() { Name = dto.Name.Trim(), Sku = dto.Sku, Price = dto.Price };

    protected override void Apply(UpdateProductDto dto, Product entity)
    {
        entity.Name = dto.Name.Trim();
        entity.Price = dto.Price;
        entity.IsArchived = dto.IsArchived;
        entity.RowVersion = dto.RowVersion;
    }
}
