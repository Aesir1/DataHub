using DataHub.Application.Abstractions;
using DataHub.Domain.Abstractions;
using DataHub.Domain.Documents;
using DataHub.Domain.Exceptions;
using DataHub.Domain.Products;
using DataHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DataHub.Infrastructure.Tests;

/// <summary>CR-8: Repository&lt;,&gt; CRUD, soft delete, audit, concurrency and outbox against real PostgreSQL.</summary>
public sealed class RepositoryTests(PostgresFixture db) : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly ICurrentUser alice = User("alice@local.test");

    public async ValueTask InitializeAsync() => await db.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static ICurrentUser User(string email)
    {
        var user = Substitute.For<ICurrentUser>();
        user.Email.Returns(email);
        user.Id.Returns(Guid.CreateVersion7());
        return user;
    }

    private static Product NewProduct(string sku = "SKU-1") => new() { Name = "Reefer", Sku = sku, Price = 10m };

    private async Task<Product> Seed(Product product)
    {
        await using var ctx = db.Create(alice);
        return await new Repository<Product, Guid>(ctx).AddAsync(product, Ct);
    }

    [Fact]
    public async Task Add_sets_audit_fields_and_row_version()
    {
        var product = await Seed(NewProduct());

        product.CreatedBy.ShouldBe("alice@local.test");
        product.CreatedAtUtc.ShouldBe(db.Time.GetUtcNow());
        product.RowVersion.ShouldNotBe(0u);
        product.UpdatedAtUtc.ShouldBeNull();
    }

    [Fact]
    public async Task Get_by_id_returns_null_for_missing_row()
    {
        await using var ctx = db.Create();
        (await new Repository<Product, Guid>(ctx).GetByIdAsync(Guid.CreateVersion7(), Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task List_and_exists()
    {
        var a = await Seed(NewProduct("A-1"));
        await Seed(NewProduct("B-1"));

        await using var ctx = db.Create();
        var repo = new Repository<Product, Guid>(ctx);
        (await repo.ListAsync(ct: Ct)).Count.ShouldBe(2);
        (await repo.ListAsync(p => p.Sku == "A-1", Ct)).Single().Id.ShouldBe(a.Id);
        (await repo.ExistsAsync(a.Id, Ct)).ShouldBeTrue();
        (await repo.ExistsAsync(Guid.CreateVersion7(), Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task Update_sets_updated_audit_and_keeps_created()
    {
        var product = await Seed(NewProduct());
        db.Time.Advance(TimeSpan.FromMinutes(5));

        await using var ctx = db.Create(User("bob@local.test"));
        var repo = new Repository<Product, Guid>(ctx);
        var loaded = (await repo.GetByIdAsync(product.Id, Ct))!;
        loaded.Name = "Renamed";
        await repo.UpdateAsync(loaded, Ct);

        await using var check = db.Create();
        var saved = await check.Products.SingleAsync(Ct);
        saved.Name.ShouldBe("Renamed");
        saved.CreatedBy.ShouldBe("alice@local.test");
        saved.UpdatedBy.ShouldBe("bob@local.test");
        saved.UpdatedAtUtc.ShouldBe(db.Time.GetUtcNow());
        saved.RowVersion.ShouldNotBe(product.RowVersion);
    }

    [Fact]
    public async Task Update_with_stale_row_version_throws_concurrency()
    {
        var product = await Seed(NewProduct());

        await using (var first = db.Create())
        {
            var repo = new Repository<Product, Guid>(first);
            var loaded = (await repo.GetByIdAsync(product.Id, Ct))!;
            loaded.Name = "First";
            await repo.UpdateAsync(loaded, Ct);
        }

        await using var second = db.Create();
        var secondRepo = new Repository<Product, Guid>(second);
        var stale = (await secondRepo.GetByIdAsync(product.Id, Ct))!;
        stale.Name = "Second";
        stale.RowVersion = product.RowVersion; // the version this client read before "First" was saved

        await Should.ThrowAsync<ConcurrencyException>(() => secondRepo.UpdateAsync(stale, Ct));
    }

    [Fact]
    public async Task Update_of_detached_missing_row_throws_not_found()
    {
        await using var ctx = db.Create();
        await Should.ThrowAsync<NotFoundException>(() => new Repository<Product, Guid>(ctx).UpdateAsync(NewProduct(), Ct));
    }

    [Fact]
    public async Task Delete_of_missing_row_throws_not_found()
    {
        await using var ctx = db.Create();
        await Should.ThrowAsync<NotFoundException>(() => new Repository<Product, Guid>(ctx).DeleteAsync(Guid.CreateVersion7(), Ct));
    }

    [Fact]
    public async Task Delete_soft_deletes_and_global_filter_hides_row()
    {
        var product = await Seed(NewProduct());

        await using (var ctx = db.Create())
        {
            await new Repository<Product, Guid>(ctx).DeleteAsync(product.Id, Ct);
        }

        await using var check = db.Create();
        (await check.Products.CountAsync(Ct)).ShouldBe(0);
        var raw = await check.Products.IgnoreQueryFilters().SingleAsync(Ct);
        raw.IsDeleted.ShouldBeTrue();
        raw.DeletedAtUtc.ShouldNotBeNull();
        await using var fresh = db.Create();
        (await new Repository<Product, Guid>(fresh).GetByIdAsync(product.Id, Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Delete_removes_rows_without_soft_delete()
    {
        await using var ctx = db.Create();
        var repo = new Repository<ProductAudit, Guid>(ctx);
        var audit = await repo.AddAsync(new ProductAudit { Name = "x", MessageId = "m1" }, Ct);

        await repo.DeleteAsync(audit.Id, Ct);

        (await ctx.ProductAudits.IgnoreQueryFilters().CountAsync(Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Unique_violation_throws_conflict()
    {
        await Seed(NewProduct("DUP-1"));

        await Should.ThrowAsync<ConflictException>(() => Seed(NewProduct("DUP-1")));
    }

    [Fact]
    public async Task Soft_deleted_sku_can_be_reused()
    {
        var first = await Seed(NewProduct("REUSE-1"));
        await using (var ctx = db.Create())
        {
            await new Repository<Product, Guid>(ctx).DeleteAsync(first.Id, Ct);
        }

        (await Seed(NewProduct("REUSE-1"))).Id.ShouldNotBe(first.Id);
    }

    [Fact]
    public async Task Product_changes_are_written_to_the_outbox_in_the_same_save()
    {
        var product = await Seed(NewProduct());
        await using (var ctx = db.Create())
        {
            var repo = new Repository<Product, Guid>(ctx);
            var loaded = (await repo.GetByIdAsync(product.Id, Ct))!;
            loaded.Price = 20m;
            await repo.UpdateAsync(loaded, Ct);
            await repo.DeleteAsync(product.Id, Ct);
        }

        await using var check = db.Create();
        var outbox = await check.OutboxMessages.OrderBy(m => m.OccurredAtUtc).ThenBy(m => m.Id).ToListAsync(Ct);
        outbox.Select(m => m.RoutingKey).ShouldBe(["product.created", "product.updated", "product.deleted"]);
        outbox.ShouldAllBe(m => m.Type == nameof(ProductChanged) && m.PublishedAtUtc == null);
        outbox[1].Payload.Replace(" ", string.Empty, StringComparison.Ordinal).ShouldContain("\"price\":20");
    }

    [Fact]
    public async Task Entities_without_events_write_no_outbox_rows()
    {
        await using var ctx = db.Create();
        await new Repository<Document, Guid>(ctx).AddAsync(new Document { FileName = "a.pdf", ContentType = "application/pdf", OwnerId = Guid.CreateVersion7() }, Ct);

        (await ctx.OutboxMessages.CountAsync(Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Product_repository_override_hides_archived_products()
    {
        await Seed(NewProduct("LIVE-1"));
        var archived = NewProduct("OLD-1");
        archived.IsArchived = true;
        await Seed(archived);

        await using var ctx = db.Create();
        (await new ProductRepository(ctx).Query().Select(p => p.Sku).ToListAsync(Ct)).ShouldBe(["LIVE-1"]);
        (await new Repository<Product, Guid>(ctx).Query().CountAsync(Ct)).ShouldBe(2);
        (await new ProductRepository(ctx).GetByIdAsync(archived.Id, Ct)).ShouldNotBeNull(); // only Query() is overridden
    }

    [Theory]
    [InlineData(EntityState.Added, false, ChangeType.Created)]
    [InlineData(EntityState.Modified, false, ChangeType.Updated)]
    [InlineData(EntityState.Modified, true, ChangeType.Deleted)]
    [InlineData(EntityState.Deleted, false, ChangeType.Deleted)]
    public void Outbox_change_type(EntityState state, bool softDeleteTurnedOn, ChangeType expected) =>
        Persistence.Interceptors.OutboxInterceptor.ChangeOf(state, NewProduct(), softDeleteTurnedOn).ShouldBe(expected);

    [Fact]
    public void Edits_to_an_already_deleted_row_publish_nothing() =>
        Persistence.Interceptors.OutboxInterceptor.ChangeOf(EntityState.Modified, new Product { Name = "x", Sku = "x", IsDeleted = true }, false).ShouldBeNull();
}
