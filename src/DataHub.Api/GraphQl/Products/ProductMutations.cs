using DataHub.Api.GraphQl.Errors;
using DataHub.Application.Products;
using DataHub.Auth;
using DataHub.Domain.Products;
using HotChocolate.Authorization;

namespace DataHub.Api.GraphQl.Products;

[ExtendObjectType(typeof(RootMutation))]
public class ProductMutations : RootMutation
{
    [Authorize(Policy = Permissions.Products.Write)]
    [Error<ValidationError>]
    [Error<ConflictError>]
    public Task<Product> CreateProduct(string name, string sku, decimal price, [Service] ProductService products, CancellationToken ct) =>
        products.CreateAsync(new CreateProductDto(name, sku, price), ct);

    /// <summary>Pass the <c>rowVersion</c> you read; a concurrent change returns ConcurrencyError.</summary>
    [Authorize(Policy = Permissions.Products.Write)]
    [Error<NotFoundError>]
    [Error<ValidationError>]
    [Error<ConcurrencyError>]
    public Task<Product> UpdateProduct(
        Guid id,
        string name,
        decimal price,
        bool isArchived,
        [GraphQLType<NonNullType<UnsignedIntType>>] uint rowVersion,
        [Service] ProductService products,
        CancellationToken ct) =>
        products.UpdateAsync(id, new UpdateProductDto(name, price, isArchived, rowVersion), ct);

    [Authorize(Policy = Permissions.Products.Write)]
    [Error<NotFoundError>]
    public async Task<Guid> DeleteProduct(Guid id, [Service] ProductService products, CancellationToken ct)
    {
        await products.DeleteAsync(id, ct);
        return id;
    }
}
