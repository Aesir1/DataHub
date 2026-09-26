using DataHub.Application.Abstractions;
using DataHub.Application.Documents;
using DataHub.Domain.Documents;
using DataHub.Domain.Exceptions;

namespace DataHub.Application.Tests;

/// <summary>ST-5 unit level: DocumentService with a faked IObjectStorage.</summary>
public class DocumentServiceTests
{
    private readonly IRepository<Document, Guid> repo = Substitute.For<IRepository<Document, Guid>>();
    private readonly IObjectStorage storage = Substitute.For<IObjectStorage>();
    private readonly ICurrentUser user = Substitute.For<ICurrentUser>();
    private readonly Guid me = Guid.CreateVersion7();
    private readonly DocumentService service;

    public DocumentServiceTests()
    {
        user.Id.Returns(me);
        user.Roles.Returns([]);
        repo.AddAsync(Arg.Any<Document>(), Arg.Any<CancellationToken>()).Returns(c => c.Arg<Document>());
        repo.UpdateAsync(Arg.Any<Document>(), Arg.Any<CancellationToken>()).Returns(c => c.Arg<Document>());
        service = new DocumentService(repo, storage, user);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Document Owned(Guid? owner = null, DocumentStatus status = DocumentStatus.Pending)
    {
        var document = new Document { OwnerId = owner ?? me, FileName = "a.pdf", ContentType = "application/pdf", SizeBytes = 10, Status = status };
        repo.GetByIdAsync(document.Id, Arg.Any<CancellationToken>()).Returns(document);
        return document;
    }

    [Fact]
    public async Task Request_upload_creates_pending_row_and_same_origin_put_path()
    {
        var upload = await service.RequestUploadAsync(" report.pdf ", "application/pdf", 1024, Ct);

        upload.Document.OwnerId.ShouldBe(me);
        upload.Document.FileName.ShouldBe("report.pdf");
        upload.Document.Status.ShouldBe(DocumentStatus.Pending);
        upload.UploadUrl.ShouldBe($"/api/documents/{upload.Document.Id}/content");
    }

    [Theory]
    [InlineData("a.exe", "application/x-msdownload", 10, "contentType")]
    [InlineData("a.pdf", "application/pdf", 0, "sizeBytes")]
    [InlineData("a.pdf", "application/pdf", (25 * 1024 * 1024) + 1, "sizeBytes")]
    [InlineData(" ", "application/pdf", 10, "fileName")]
    public async Task Request_upload_validates(string fileName, string contentType, long size, string field)
    {
        var ex = await Should.ThrowAsync<ValidationException>(() => service.RequestUploadAsync(fileName, contentType, size, Ct));

        ex.Errors.Keys.ShouldContain(field);
    }

    [Fact]
    public async Task Anonymous_caller_is_forbidden()
    {
        user.Id.Returns((Guid?)null);

        await Should.ThrowAsync<ForbiddenException>(() => service.RequestUploadAsync("a.pdf", "application/pdf", 1, Ct));
        Should.Throw<ForbiddenException>(() => service.Query());
    }

    [Fact]
    public async Task Confirm_marks_available_with_stored_size()
    {
        var document = Owned();
        storage.HeadAsync(Document.Bucket, document.ObjectKey, Arg.Any<CancellationToken>()).Returns(new ObjectInfo(document.ObjectKey, 2048, null));

        var confirmed = await service.ConfirmUploadAsync(document.Id, Ct);

        confirmed.Status.ShouldBe(DocumentStatus.Available);
        confirmed.SizeBytes.ShouldBe(2048);
    }

    [Fact]
    public async Task Confirm_without_object_is_conflict()
    {
        var document = Owned();

        await Should.ThrowAsync<ConflictException>(() => service.ConfirmUploadAsync(document.Id, Ct));
    }

    [Fact]
    public async Task Confirm_of_oversized_object_deletes_it()
    {
        var document = Owned();
        storage.HeadAsync(Document.Bucket, document.ObjectKey, Arg.Any<CancellationToken>()).Returns(new ObjectInfo(document.ObjectKey, Document.MaxSizeBytes + 1, null));

        await Should.ThrowAsync<ConflictException>(() => service.ConfirmUploadAsync(document.Id, Ct));
        await storage.Received(1).DeleteAsync(Document.Bucket, document.ObjectKey, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Someone_elses_document_looks_missing()
    {
        var document = Owned(owner: Guid.CreateVersion7());

        await Should.ThrowAsync<NotFoundException>(() => service.RenameAsync(document.Id, "x.pdf", Ct));
        await Should.ThrowAsync<NotFoundException>(() => service.DeleteAsync(document.Id, Ct));
    }

    [Fact]
    public async Task Platform_admin_can_manage_any_document()
    {
        user.Roles.Returns(["platform-admin"]);
        var document = Owned(owner: Guid.CreateVersion7());

        (await service.RenameAsync(document.Id, "x.pdf", Ct)).FileName.ShouldBe("x.pdf");
    }

    [Fact]
    public async Task Rename_validates_name()
    {
        var document = Owned();

        await Should.ThrowAsync<ValidationException>(() => service.RenameAsync(document.Id, new string('a', 256), Ct));
    }

    [Fact]
    public async Task Replace_content_resets_to_pending_with_new_put_url()
    {
        var document = Owned(status: DocumentStatus.Available);

        var upload = await service.ReplaceContentAsync(document.Id, "image/png", 99, Ct);

        (upload.Document.Status, upload.Document.ContentType, upload.Document.SizeBytes).ShouldBe((DocumentStatus.Pending, "image/png", 99L));
        upload.UploadUrl.ShouldBe($"/api/documents/{document.Id}/content");
    }

    [Fact]
    public async Task Delete_soft_deletes_row_then_object()
    {
        var document = Owned(status: DocumentStatus.Available);

        await service.DeleteAsync(document.Id, Ct);

        Received.InOrder(() =>
        {
            repo.DeleteAsync(document.Id, Arg.Any<CancellationToken>());
            storage.DeleteAsync(Document.Bucket, document.ObjectKey, Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public void Download_url_only_for_available_documents()
    {
        DocumentService.DownloadUrl(new Document { FileName = "a", ContentType = "text/plain", Status = DocumentStatus.Pending }).ShouldBeNull();
        var available = new Document { FileName = "a", ContentType = "text/plain", Status = DocumentStatus.Available };
        DocumentService.DownloadUrl(available).ShouldBe($"/api/documents/{available.Id}/content");
    }

    [Fact]
    public async Task Upload_content_stores_bytes_for_pending_owned_document()
    {
        var document = Owned();
        using var body = new MemoryStream("x"u8.ToArray());

        await service.UploadContentAsync(document.Id, body, "application/pdf", Ct);

        await storage.Received(1).PutAsync(Document.Bucket, document.ObjectKey, body, "application/pdf", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Upload_content_rejects_other_type_available_document_and_strangers()
    {
        using var body = new MemoryStream("x"u8.ToArray());

        await Should.ThrowAsync<ValidationException>(() => service.UploadContentAsync(Owned().Id, body, "image/png", Ct));
        await Should.ThrowAsync<ConflictException>(() => service.UploadContentAsync(Owned(status: DocumentStatus.Available).Id, body, "application/pdf", Ct));
        await Should.ThrowAsync<NotFoundException>(() => service.UploadContentAsync(Owned(owner: Guid.CreateVersion7()).Id, body, "application/pdf", Ct));
        await storage.DidNotReceiveWithAnyArgs().PutAsync(default!, default!, default!, default!, CancellationToken.None);
    }

    [Fact]
    public async Task Open_content_only_for_available_owned_documents()
    {
        var available = Owned(status: DocumentStatus.Available);
        var stored = new StoredObject(new MemoryStream(), "application/pdf", 0);
        storage.GetAsync(Document.Bucket, available.ObjectKey, Arg.Any<CancellationToken>()).Returns(stored);

        (await service.OpenContentAsync(available.Id, Ct)).Content.ShouldBe(stored);
        await Should.ThrowAsync<NotFoundException>(() => service.OpenContentAsync(Owned().Id, Ct));
        await Should.ThrowAsync<NotFoundException>(() => service.OpenContentAsync(Owned(owner: Guid.CreateVersion7(), status: DocumentStatus.Available).Id, Ct));
    }

    [Fact]
    public void Query_scopes_to_owner_unless_admin()
    {
        var mine = new Document { OwnerId = me, FileName = "a", ContentType = "text/plain" };
        var theirs = new Document { OwnerId = Guid.CreateVersion7(), FileName = "b", ContentType = "text/plain" };
        repo.Query().Returns(new[] { mine, theirs }.AsQueryable());

        service.Query().ShouldBe([mine]);
        user.Roles.Returns(["platform-admin"]);
        service.Query().Count().ShouldBe(2);
    }
}
