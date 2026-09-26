using DataHub.Application.Abstractions;
using DataHub.Application.Documents;
using DataHub.Domain.Documents;
using DataHub.Domain.Exceptions;
using Microsoft.Extensions.Time.Testing;

namespace DataHub.Application.Tests;

/// <summary>ST-5 unit level: DocumentService with a faked IObjectStorage.</summary>
public class DocumentServiceTests
{
    private static readonly Uri Presigned = new("http://minio.test/documents/signed");

    private readonly IRepository<Document, Guid> repo = Substitute.For<IRepository<Document, Guid>>();
    private readonly IObjectStorage storage = Substitute.For<IObjectStorage>();
    private readonly ICurrentUser user = Substitute.For<ICurrentUser>();
    private readonly FakeTimeProvider time = new(new DateTimeOffset(2026, 9, 25, 10, 0, 0, TimeSpan.Zero));
    private readonly Guid me = Guid.CreateVersion7();
    private readonly DocumentService service;

    public DocumentServiceTests()
    {
        user.Id.Returns(me);
        user.Roles.Returns([]);
        repo.AddAsync(Arg.Any<Document>(), Arg.Any<CancellationToken>()).Returns(c => c.Arg<Document>());
        repo.UpdateAsync(Arg.Any<Document>(), Arg.Any<CancellationToken>()).Returns(c => c.Arg<Document>());
        storage.GetPresignedUrl(default!, default!, default, default, default).ReturnsForAnyArgs(Presigned);
        service = new DocumentService(repo, storage, user, time);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Document Owned(Guid? owner = null, DocumentStatus status = DocumentStatus.Pending)
    {
        var document = new Document { OwnerId = owner ?? me, FileName = "a.pdf", ContentType = "application/pdf", SizeBytes = 10, Status = status };
        repo.GetByIdAsync(document.Id, Arg.Any<CancellationToken>()).Returns(document);
        return document;
    }

    [Fact]
    public async Task Request_upload_creates_pending_row_and_10_minute_put_url()
    {
        var upload = await service.RequestUploadAsync(" report.pdf ", "application/pdf", 1024, Ct);

        upload.Document.OwnerId.ShouldBe(me);
        upload.Document.FileName.ShouldBe("report.pdf");
        upload.Document.Status.ShouldBe(DocumentStatus.Pending);
        upload.UploadUrl.ShouldBe(Presigned);
        upload.ExpiresAtUtc.ShouldBe(time.GetUtcNow().AddMinutes(10));
        storage.Received(1).GetPresignedUrl(Document.Bucket, $"{me}/{upload.Document.Id}", HttpVerb.Put, TimeSpan.FromMinutes(10), "application/pdf");
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
        upload.UploadUrl.ShouldBe(Presigned);
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
        service.DownloadUrl(new Document { FileName = "a", ContentType = "text/plain", Status = DocumentStatus.Pending }).ShouldBeNull();
        service.DownloadUrl(new Document { FileName = "a", ContentType = "text/plain", Status = DocumentStatus.Available }).ShouldBe(Presigned);
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
