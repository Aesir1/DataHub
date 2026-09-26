namespace DataHub.Domain.Abstractions;

public interface IEntity<out TKey>
{
    TKey Id { get; }
}

/// <summary>Rows that are hidden by a global query filter and flagged instead of removed.</summary>
public interface ISoftDelete
{
    bool IsDeleted { get; set; }

    DateTimeOffset? DeletedAtUtc { get; set; }
}

/// <summary>
/// Base for aggregate roots: UUID v7 id generated in the app, audit columns set by the audit interceptor
/// (never by repositories) and PostgreSQL <c>xmin</c> as optimistic concurrency token.
/// </summary>
public abstract class AuditableEntity : IEntity<Guid>
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public DateTimeOffset CreatedAtUtc { get; set; }

    public string? CreatedBy { get; set; }

    public DateTimeOffset? UpdatedAtUtc { get; set; }

    public string? UpdatedBy { get; set; }

    /// <summary>PostgreSQL xmin. Send back the value you read to detect concurrent updates.</summary>
    public uint RowVersion { get; set; }
}

public enum ChangeType
{
    Created,
    Updated,
    Deleted,
}

/// <summary>Entities whose changes are written to the outbox and published to <c>domain-events</c>.</summary>
public interface IPublishesChanges
{
    /// <summary>Routing-key prefix, for example <c>product</c> gives <c>product.created</c>.</summary>
    string EventName { get; }

    object ToChangedEvent(ChangeType change);
}
