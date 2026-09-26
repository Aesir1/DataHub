using DataHub.Domain.Abstractions;

namespace DataHub.Domain.Products;

public class Product : AuditableEntity, ISoftDelete, IPublishesChanges
{
    public required string Name { get; set; }

    public required string Sku { get; set; }

    public decimal Price { get; set; }

    /// <summary>Archived products are hidden from queries by <c>ProductRepository</c>.</summary>
    public bool IsArchived { get; set; }

    public bool IsDeleted { get; set; }

    public DateTimeOffset? DeletedAtUtc { get; set; }

    public string EventName => "product";

    public object ToChangedEvent(ChangeType change) => new ProductChanged(Id, change, Name, Sku, Price, DateTimeOffset.UtcNow);
}

/// <summary>Published to exchange <c>domain-events</c> with routing key <c>product.created|updated|deleted</c>.</summary>
public sealed record ProductChanged(Guid ProductId, ChangeType Change, string Name, string Sku, decimal Price, DateTimeOffset OccurredAtUtc);

/// <summary>Stored by the audit consumer, one row per consumed <see cref="ProductChanged"/>.</summary>
public class ProductAudit : IEntity<Guid>
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid ProductId { get; set; }

    public ChangeType Change { get; set; }

    public required string Name { get; set; }

    public decimal Price { get; set; }

    /// <summary>RabbitMQ message-id; unique, so redelivery is a no-op.</summary>
    public required string MessageId { get; set; }

    public DateTimeOffset OccurredAtUtc { get; set; }
}
