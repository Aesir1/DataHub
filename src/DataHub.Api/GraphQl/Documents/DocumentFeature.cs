using DataHub.Api.GraphQl.Errors;
using DataHub.Application.Documents;
using DataHub.Auth;
using DataHub.Domain.Documents;
using HotChocolate.Authorization;

namespace DataHub.Api.GraphQl.Documents;

[ExtendObjectType(typeof(RootQuery))]
public class DocumentQueries : RootQuery
{
    /// <summary>Own documents (all for platform-admin) with their download path.</summary>
    [Authorize(Policy = Permissions.Documents.Read)]
    [UsePaging(MaxPageSize = 100, IncludeTotalCount = true)]
    [UseProjection]
    [UseFiltering]
    [UseSorting]
    public IQueryable<Document> GetDocuments([Service] DocumentService documents) =>
        documents.Query().OrderByDescending(d => d.CreatedAtUtc);

    [Authorize(Policy = Permissions.Documents.Read)]
    [UseFirstOrDefault]
    [UseProjection]
    public IQueryable<Document> GetDocument(Guid id, [Service] DocumentService documents) => documents.Query().Where(d => d.Id == id);
}

/// <summary>UploadUrl is a same-origin path the browser PUTs the file to (the web BFF forwards it to the Api).</summary>
public sealed record DocumentUploadPayload(Document Document, string UploadUrl)
{
    public static DocumentUploadPayload From(DocumentUpload upload) => new(upload.Document, upload.UploadUrl);
}

[ExtendObjectType(typeof(RootMutation))]
public class DocumentMutations : RootMutation
{
    /// <summary>Creates a Pending document and returns the path to PUT its content to (max 25 MB, allow-listed types).</summary>
    [Authorize(Policy = Permissions.Documents.Write)]
    [Error<ValidationError>]
    [Error<ForbiddenError>]
    public async Task<DocumentUploadPayload> RequestDocumentUpload(
        string fileName,
        string contentType,
        long sizeBytes,
        [Service] DocumentService documents,
        CancellationToken ct) =>
        DocumentUploadPayload.From(await documents.RequestUploadAsync(fileName, contentType, sizeBytes, ct));

    [Authorize(Policy = Permissions.Documents.Write)]
    [Error<NotFoundError>]
    [Error<ConflictError>]
    public Task<Document> ConfirmDocumentUpload(Guid id, [Service] DocumentService documents, CancellationToken ct) =>
        documents.ConfirmUploadAsync(id, ct);

    [Authorize(Policy = Permissions.Documents.Write)]
    [Error<NotFoundError>]
    [Error<ValidationError>]
    public Task<Document> RenameDocument(Guid id, string fileName, [Service] DocumentService documents, CancellationToken ct) =>
        documents.RenameAsync(id, fileName, ct);

    [Authorize(Policy = Permissions.Documents.Write)]
    [Error<NotFoundError>]
    [Error<ValidationError>]
    public async Task<DocumentUploadPayload> ReplaceDocumentContent(
        Guid id,
        string contentType,
        long sizeBytes,
        [Service] DocumentService documents,
        CancellationToken ct) =>
        DocumentUploadPayload.From(await documents.ReplaceContentAsync(id, contentType, sizeBytes, ct));

    [Authorize(Policy = Permissions.Documents.Write)]
    [Error<NotFoundError>]
    public async Task<Guid> DeleteDocument(Guid id, [Service] DocumentService documents, CancellationToken ct)
    {
        await documents.DeleteAsync(id, ct);
        return id;
    }
}

public sealed class DocumentType : ObjectType<Document>
{
    protected override void Configure(IObjectTypeDescriptor<Document> descriptor)
    {
        descriptor.Ignore(d => d.IsDeleted);
        descriptor.Ignore(d => d.DeletedAtUtc);
        descriptor.Ignore(d => d.ObjectKey);
        descriptor.Field(d => d.RowVersion).Type<NonNullType<UnsignedIntType>>();

        // downloadUrl needs these even when the client did not select them.
        descriptor.Field(d => d.Id).IsProjected(true);
        descriptor.Field(d => d.OwnerId).IsProjected(true);
        descriptor.Field(d => d.Status).IsProjected(true);
        descriptor.Field("downloadUrl")
            .Type<StringType>()
            .Description("Same-origin download path; null while the upload is pending.")
            .Resolve(ctx => DocumentService.DownloadUrl(ctx.Parent<Document>()));
    }
}
