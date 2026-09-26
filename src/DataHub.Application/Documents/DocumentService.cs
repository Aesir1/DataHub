using DataHub.Application.Abstractions;
using DataHub.Domain.Documents;
using DataHub.Domain.Exceptions;

namespace DataHub.Application.Documents;

public sealed record DocumentUpload(Document Document, string UploadUrl);

/// <summary>
/// Document metadata lives in PostgreSQL, the bytes in S3. S3 is never reachable from browsers: they upload and
/// download through the Api's content endpoint (proxied by the web BFF at the same path), which checks ownership.
/// </summary>
public sealed class DocumentService(IRepository<Document, Guid> documents, IObjectStorage storage, ICurrentUser user)
{
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

    /// <summary>Same key, new content; the document is Pending until confirmed again.</summary>
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

    /// <summary>Browser path of the content endpoint (web BFF route, forwarded to the Api with the user's token).</summary>
    public static string ContentPath(Guid id) => $"/api/documents/{id}/content";

    public static string? DownloadUrl(Document document) =>
        document.Status == DocumentStatus.Available ? ContentPath(document.Id) : null;

    /// <summary>Stores the bytes of a Pending document; the caller enforces the 25 MB body limit.</summary>
    public async Task UploadContentAsync(Guid id, Stream content, string contentType, CancellationToken ct = default)
    {
        var document = await GetOwnedAsync(id, ct);
        if (document.Status != DocumentStatus.Pending)
        {
            throw new ConflictException("Request an upload (or content replacement) first.");
        }

        if (!string.Equals(contentType, document.ContentType, StringComparison.OrdinalIgnoreCase))
        {
            throw Invalid(nameof(contentType), $"Content type must be {document.ContentType}.");
        }

        await storage.PutAsync(Document.Bucket, document.ObjectKey, content, document.ContentType, ct);
    }

    public async Task<(Document Document, StoredObject Content)> OpenContentAsync(Guid id, CancellationToken ct = default)
    {
        var document = await GetOwnedAsync(id, ct);
        var content = document.Status == DocumentStatus.Available
            ? await storage.GetAsync(Document.Bucket, document.ObjectKey, ct)
            : null;
        return (document, content ?? throw NotFoundException.For<Document>(id));
    }

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

    private static DocumentUpload UploadFor(Document document) => new(document, ContentPath(document.Id));

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
