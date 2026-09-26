using DataHub.Application.Abstractions;
using DataHub.Domain.Documents;
using DataHub.Domain.Exceptions;

namespace DataHub.Application.Documents;

public sealed record DocumentUpload(Document Document, Uri UploadUrl, DateTimeOffset ExpiresAtUtc);

/// <summary>Document metadata lives in PostgreSQL; browsers move the bytes directly through presigned URLs.</summary>
public sealed class DocumentService(IRepository<Document, Guid> documents, IObjectStorage storage, ICurrentUser user, TimeProvider time)
{
    public static readonly TimeSpan UploadUrlLifetime = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan DownloadUrlLifetime = TimeSpan.FromMinutes(5);

    /// <summary>Documents the caller may see: own ones, or all for platform admins.</summary>
    public IQueryable<Document> Query()
    {
        var ownerId = RequireUser();
        return CanSeeAll ? documents.Query() : documents.Query().Where(d => d.OwnerId == ownerId);
    }

    public async Task<DocumentUpload> RequestUploadAsync(string fileName, string contentType, long sizeBytes, CancellationToken ct = default)
    {
        var ownerId = RequireUser();
        ValidateContent(fileName, contentType, sizeBytes);
        var document = await documents.AddAsync(
            new Document { OwnerId = ownerId, FileName = fileName.Trim(), ContentType = contentType, SizeBytes = sizeBytes },
            ct);
        return UploadFor(document);
    }

    public async Task<Document> ConfirmUploadAsync(Guid id, CancellationToken ct = default)
    {
        var document = await GetOwnedAsync(id, ct);
        var head = await storage.HeadAsync(Document.Bucket, document.ObjectKey, ct)
            ?? throw new ConflictException("The file has not been uploaded yet.");
        if (head.SizeBytes > Document.MaxSizeBytes)
        {
            await storage.DeleteAsync(Document.Bucket, document.ObjectKey, ct);
            throw new ConflictException("The uploaded file exceeds 25 MB and was removed.");
        }

        document.SizeBytes = head.SizeBytes;
        document.Status = DocumentStatus.Available;
        return await documents.UpdateAsync(document, ct);
    }

    public async Task<Document> RenameAsync(Guid id, string fileName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > 255)
        {
            throw Invalid(nameof(fileName), "File name is required (max 255 characters).");
        }

        var document = await GetOwnedAsync(id, ct);
        document.FileName = fileName.Trim();
        return await documents.UpdateAsync(document, ct);
    }

    /// <summary>New presigned PUT on the same key; the document is Pending until confirmed again.</summary>
    public async Task<DocumentUpload> ReplaceContentAsync(Guid id, string contentType, long sizeBytes, CancellationToken ct = default)
    {
        var document = await GetOwnedAsync(id, ct);
        ValidateContent(document.FileName, contentType, sizeBytes);
        document.ContentType = contentType;
        document.SizeBytes = sizeBytes;
        document.Status = DocumentStatus.Pending;
        return UploadFor(await documents.UpdateAsync(document, ct));
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var document = await GetOwnedAsync(id, ct);
        await documents.DeleteAsync(id, ct);

        // A failure here leaves an orphan object; `dbutils maintain --orphan-objects --delete` cleans it up.
        await storage.DeleteAsync(Document.Bucket, document.ObjectKey, ct);
    }

    public Uri? DownloadUrl(Document document) =>
        document.Status == DocumentStatus.Available
            ? storage.GetPresignedUrl(Document.Bucket, document.ObjectKey, HttpVerb.Get, DownloadUrlLifetime)
            : null;

    private bool CanSeeAll => user.Roles.Contains("platform-admin");

    private static Domain.Exceptions.ValidationException Invalid(string field, string message) =>
        new Domain.Exceptions.ValidationException(new Dictionary<string, string[]> { [field] = [message] });

    private static void ValidateContent(string fileName, string contentType, long sizeBytes)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > 255)
        {
            throw Invalid(nameof(fileName), "File name is required (max 255 characters).");
        }

        if (!Document.AllowedContentTypes.Contains(contentType))
        {
            throw Invalid(nameof(contentType), $"Content type must be one of: {string.Join(", ", Document.AllowedContentTypes)}.");
        }

        if (sizeBytes is <= 0 or > Document.MaxSizeBytes)
        {
            throw Invalid(nameof(sizeBytes), "Size must be between 1 byte and 25 MB.");
        }
    }

    private DocumentUpload UploadFor(Document document) => new(
        document,
        storage.GetPresignedUrl(Document.Bucket, document.ObjectKey, HttpVerb.Put, UploadUrlLifetime, document.ContentType),
        time.GetUtcNow() + UploadUrlLifetime);

    private Guid RequireUser() => user.Id ?? throw new ForbiddenException("Sign in required.");

    private async Task<Document> GetOwnedAsync(Guid id, CancellationToken ct)
    {
        var ownerId = RequireUser();
        var document = await documents.GetByIdAsync(id, ct);
        if (document is null || (document.OwnerId != ownerId && !CanSeeAll))
        {
            // Same answer for "missing" and "not yours", so ids cannot be probed.
            throw NotFoundException.For<Document>(id);
        }

        return document;
    }
}
