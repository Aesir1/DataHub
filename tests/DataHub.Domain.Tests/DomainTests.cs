using DataHub.Domain.Abstractions;
using DataHub.Domain.Documents;
using DataHub.Domain.Exceptions;
using DataHub.Domain.Products;

namespace DataHub.Domain.Tests;

public class DomainTests
{
    [Fact]
    public void New_entities_get_uuid_v7_ids()
    {
        new Product { Name = "a", Sku = "b" }.Id.Version.ShouldBe(7);
        new Document { FileName = "a", ContentType = "b" }.Id.Version.ShouldBe(7);
    }

    [Fact]
    public void Product_change_event_carries_current_state()
    {
        var product = new Product { Name = "Reefer", Sku = "RF-1", Price = 5m };

        var changed = product.ToChangedEvent(ChangeType.Updated).ShouldBeOfType<ProductChanged>();

        (changed.ProductId, changed.Change, changed.Name, changed.Sku, changed.Price).ShouldBe((product.Id, ChangeType.Updated, "Reefer", "RF-1", 5m));
        product.EventName.ShouldBe("product");
    }

    [Fact]
    public void Document_object_key_is_owner_then_document()
    {
        var owner = Guid.CreateVersion7();
        var document = new Document { OwnerId = owner, FileName = "a", ContentType = "b" };

        document.ObjectKey.ShouldBe($"{owner}/{document.Id}");
        Document.AllowedContentTypes.ShouldContain("APPLICATION/PDF");
        Document.MaxSizeBytes.ShouldBe(25 * 1024 * 1024);
    }

    [Fact]
    public void Exceptions_have_safe_messages()
    {
        NotFoundException.For<Product>(42).Message.ShouldBe("Product 42 was not found.");
        new ForbiddenException().Message.ShouldBe("You are not allowed to do this.");
        var validation = new ValidationException(new Dictionary<string, string[]> { ["Name"] = ["required"], ["Sku"] = ["bad", "short"] });
        validation.Message.ShouldBe("Validation failed: Name: required; Sku: bad, short");
        validation.Errors["Sku"].Length.ShouldBe(2);
        new ConflictException("c").ShouldBeAssignableTo<DomainException>();
        new ConcurrencyException("c").ShouldBeAssignableTo<DomainException>();
    }

    [Fact]
    public void Webhook_routing_key_includes_sender_and_event()
    {
        Messaging.WebhookReceived.RoutingKey("container").ShouldBe("webhook.container.received");
        Messaging.WebhookReceived.RoutingKey("container", "alarm").ShouldBe("webhook.container.alarm");
    }
}
