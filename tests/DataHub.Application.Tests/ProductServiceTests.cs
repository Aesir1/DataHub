using System.Linq.Expressions;
using DataHub.Application.Abstractions;
using DataHub.Application.Crud;
using DataHub.Application.Products;
using DataHub.Domain.Abstractions;
using DataHub.Domain.Exceptions;
using DataHub.Domain.Products;

namespace DataHub.Application.Tests;

public class ProductServiceTests
{
    private readonly IProductRepository repo = Substitute.For<IProductRepository>();
    private readonly ProductService service;

    public ProductServiceTests()
    {
        repo.AddAsync(Arg.Any<Product>(), Arg.Any<CancellationToken>()).Returns(c => c.Arg<Product>());
        repo.UpdateAsync(Arg.Any<Product>(), Arg.Any<CancellationToken>()).Returns(c => c.Arg<Product>());
        service = new ProductService(repo);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Create_maps_and_trims()
    {
        var product = await service.CreateAsync(new CreateProductDto("  Reefer  ", "RF-40", 10m), Ct);

        product.Name.ShouldBe("Reefer");
        product.Sku.ShouldBe("RF-40");
        product.Price.ShouldBe(10m);
        await repo.Received(1).AddAsync(product, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("", "RF-40", 1, "Name")]
    [InlineData("Reefer", "rf 40", 1, "Sku")]
    [InlineData("Reefer", "RF-40", -1, "Price")]
    public async Task Create_rejects_invalid_input(string name, string sku, decimal price, string field)
    {
        var ex = await Should.ThrowAsync<ValidationException>(() => service.CreateAsync(new CreateProductDto(name, sku, price), Ct));

        ex.Errors.Keys.ShouldContain(field);
        await repo.DidNotReceive().AddAsync(Arg.Any<Product>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Update_applies_fields_and_client_row_version()
    {
        var existing = new Product { Name = "Old", Sku = "RF-40", Price = 1m, RowVersion = 7 };
        repo.GetByIdAsync(existing.Id, Arg.Any<CancellationToken>()).Returns(existing);

        var updated = await service.UpdateAsync(existing.Id, new UpdateProductDto("New", 2m, IsArchived: true, RowVersion: 5), Ct);

        (updated.Name, updated.Price, updated.IsArchived, updated.RowVersion).ShouldBe(("New", 2m, true, 5u));
    }

    [Fact]
    public async Task Update_of_missing_product_throws_not_found() =>
        await Should.ThrowAsync<NotFoundException>(() => service.UpdateAsync(Guid.CreateVersion7(), new UpdateProductDto("New", 2m, false, 1), Ct));

    [Fact]
    public async Task Delete_and_reads_delegate_to_repository()
    {
        var id = Guid.CreateVersion7();
        await service.DeleteAsync(id, Ct);
        await service.GetAsync(id, Ct);
        _ = service.Query();

        await repo.Received(1).DeleteAsync(id, Arg.Any<CancellationToken>());
        await repo.Received(1).GetByIdAsync(id, Arg.Any<CancellationToken>());
        repo.Received(1).Query();
    }

    [Fact]
    public void Validator_groups_errors_per_member()
    {
        var ex = Should.Throw<ValidationException>(() => DtoValidator.Validate(new CreateProductDto(string.Empty, "x", -5m)));

        ex.Errors.Keys.ShouldBe(["Name", "Sku", "Price"], ignoreOrder: true);
        ex.Message.ShouldStartWith("Validation failed:");
    }

    [Fact]
    public async Task Audit_handler_stores_once_per_message_id()
    {
        var audits = Substitute.For<IRepository<ProductAudit, Guid>>();
        IReadOnlyList<ProductAudit> none = [];
        IReadOnlyList<ProductAudit> stored = [new ProductAudit { Name = "x", MessageId = "m1" }];
        audits.ListAsync(Arg.Any<Expression<Func<ProductAudit, bool>>>(), Arg.Any<CancellationToken>()).Returns(none, stored);
        var handler = new ProductAuditHandler(audits);
        var message = new ProductChanged(Guid.CreateVersion7(), ChangeType.Updated, "Reefer", "RF-40", 9m, DateTimeOffset.UtcNow);
        var context = new MessageContext("m1", null, "product.updated", false);

        await handler.HandleAsync(message, context, Ct);
        await handler.HandleAsync(message, context, Ct);

        await audits.Received(1).AddAsync(
            Arg.Is<ProductAudit>(a => a.MessageId == "m1" && a.ProductId == message.ProductId && a.Change == ChangeType.Updated && a.Price == 9m),
            Arg.Any<CancellationToken>());
    }
}
