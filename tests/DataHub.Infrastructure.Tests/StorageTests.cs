using System.Net;
using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using DataHub.Application.Abstractions;
using DataHub.Application.Documents;
using DataHub.Domain.Documents;
using DataHub.Domain.Exceptions;
using DataHub.Infrastructure.Persistence;
using DataHub.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.Minio;

namespace DataHub.Infrastructure.Tests;

/// <summary>ST-5: S3ObjectStorage and all five Document operations against a MinIO container.</summary>
public sealed class StorageTests(PostgresFixture db) : IAsyncLifetime
{
    // minio/minio is no longer published on Docker Hub.
    private readonly MinioContainer minio = new MinioBuilder("cgr.dev/chainguard/minio:latest").Build();
    private readonly HttpClient http = new();
    private AmazonS3Client s3 = null!;
    private S3ObjectStorage storage = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await db.ResetAsync();
        await minio.StartAsync(Ct);
        s3 = new AmazonS3Client(
            new BasicAWSCredentials(minio.GetAccessKey(), minio.GetSecretKey()),
            new AmazonS3Config { ServiceURL = minio.GetConnectionString(), ForcePathStyle = true, AuthenticationRegion = "us-east-1" });
        storage = new S3ObjectStorage(s3);
        await new BucketInitializer(s3, NullLogger<BucketInitializer>.Instance).StartAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        http.Dispose();
        s3.Dispose();
        await minio.DisposeAsync();
    }

    [Fact]
    public async Task Put_head_get_list_delete()
    {
        await storage.PutAsync(Document.Bucket, "owner/one", new MemoryStream("hello"u8.ToArray()), "text/plain", Ct);

        (await storage.HeadAsync(Document.Bucket, "owner/one", Ct))!.SizeBytes.ShouldBe(5);
        await using (var stored = await storage.GetAsync(Document.Bucket, "owner/one", Ct))
        {
            stored!.ContentType.ShouldBe("text/plain");
            (await new StreamReader(stored.Content).ReadToEndAsync(Ct)).ShouldBe("hello");
        }

        (await storage.ListAsync(Document.Bucket, "owner/", Ct)).Select(o => o.Key).ShouldBe(["owner/one"]);
        await storage.DeleteAsync(Document.Bucket, "owner/one", Ct);
        (await storage.GetAsync(Document.Bucket, "owner/one", Ct)).ShouldBeNull();
        (await storage.HeadAsync(Document.Bucket, "owner/one", Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Buckets_are_not_anonymously_readable()
    {
        await storage.PutAsync(BucketInitializer.BrandingBucket, "logo.svg", new MemoryStream("<svg/>"u8.ToArray()), "image/svg+xml", Ct);
        await storage.PutAsync(Document.Bucket, "secret", new MemoryStream("x"u8.ToArray()), "text/plain", Ct);

        (await http.GetAsync($"{minio.GetConnectionString().TrimEnd('/')}/{BucketInitializer.BrandingBucket}/logo.svg", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await http.GetAsync($"{minio.GetConnectionString().TrimEnd('/')}/{Document.Bucket}/secret", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Document_lifecycle_create_confirm_read_update_delete()
    {
        var owner = Substitute.For<ICurrentUser>();
        owner.Id.Returns(Guid.CreateVersion7());
        owner.Email.Returns("owner@local.test");
        owner.Roles.Returns([]);
        await using var ctx = db.Create(owner);
        var service = new DocumentService(new Repository<Document, Guid>(ctx), storage, owner);

        // Create: Pending row + content path
        var upload = await service.RequestUploadAsync("notes.txt", "text/plain", 11, Ct);
        upload.Document.Status.ShouldBe(DocumentStatus.Pending);
        await Should.ThrowAsync<ConflictException>(() => service.ConfirmUploadAsync(upload.Document.Id, Ct)); // nothing uploaded yet
        await Upload(service, upload.Document.Id, "hello world");

        // Confirm: HEAD finds it
        var confirmed = await service.ConfirmUploadAsync(upload.Document.Id, Ct);
        confirmed.Status.ShouldBe(DocumentStatus.Available);

        // Read
        (await Read(service, confirmed.Id)).ShouldBe("hello world");
        (await service.Query().CountAsync(Ct)).ShouldBe(1);

        // Update: rename + replace content on the same key
        (await service.RenameAsync(confirmed.Id, "renamed.txt", Ct)).FileName.ShouldBe("renamed.txt");
        var replace = await service.ReplaceContentAsync(confirmed.Id, "text/plain", 3, Ct);
        replace.Document.Status.ShouldBe(DocumentStatus.Pending);
        await Upload(service, confirmed.Id, "new");
        await service.ConfirmUploadAsync(confirmed.Id, Ct);
        (await Read(service, confirmed.Id)).ShouldBe("new");

        // Delete: soft-deletes the row and removes the object
        await service.DeleteAsync(confirmed.Id, Ct);
        (await service.Query().CountAsync(Ct)).ShouldBe(0);
        (await storage.HeadAsync(Document.Bucket, confirmed.ObjectKey, Ct)).ShouldBeNull();
    }

    private static async Task Upload(DocumentService service, Guid id, string body)
    {
        using var content = new MemoryStream(Encoding.UTF8.GetBytes(body));
        await service.UploadContentAsync(id, content, "text/plain", Ct);
    }

    private static async Task<string> Read(DocumentService service, Guid id)
    {
        var (_, stored) = await service.OpenContentAsync(id, Ct);
        await using (stored)
        {
            return await new StreamReader(stored.Content).ReadToEndAsync(Ct);
        }
    }
}
