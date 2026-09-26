using DataHub.Domain.Abstractions;

namespace DataHub.Domain.Documents;

public enum DocumentStatus
{
    Pending,
    Available,
}

/// <summary>Metadata in PostgreSQL; bytes in object storage under <see cref="ObjectKey"/>.</summary>
public class Document : AuditableEntity, ISoftDelete
{
    public const string Bucket = "documents";
    public const long MaxSizeBytes = 25 * 1024 * 1024;

    public static readonly IReadOnlySet<string> AllowedContentTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "application/pdf", "image/png", "image/jpeg", "text/plain", "text/csv", "application/json",
    };

    public Guid OwnerId { get; set; }

    public required string FileName { get; set; }

    public required string ContentType { get; set; }

    public long SizeBytes { get; set; }

    public DocumentStatus Status { get; set; } = DocumentStatus.Pending;

    public bool IsDeleted { get; set; }

    public DateTimeOffset? DeletedAtUtc { get; set; }

    /// <summary>Authorization checks the row, never the key.</summary>
    public string ObjectKey => $"{OwnerId}/{Id}";
}
