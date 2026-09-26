using System.ComponentModel.DataAnnotations;
using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using DataHub.Application.Abstractions;

namespace DataHub.Infrastructure.Storage;

public sealed class ObjectStorageOptions
{
    /// <summary>S3 endpoint, for example http://localhost:9000 (MinIO) or https://s3.eu-central-1.amazonaws.com.</summary>
    [Required]
    [Url]
    public string Endpoint { get; set; } = string.Empty;

    [Required]
    public string AccessKey { get; set; } = string.Empty;

    [Required]
    public string SecretKey { get; set; } = string.Empty;

    public string Region { get; set; } = "us-east-1";
}

/// <summary>AWSSDK.S3 with path-style addressing, so the same code runs against MinIO, AWS S3 or any S3-compatible store.</summary>
public sealed class S3ObjectStorage(IAmazonS3 s3) : IObjectStorage
{
    public async Task PutAsync(string bucket, string key, Stream content, string contentType, CancellationToken ct = default) =>
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = content, ContentType = contentType, AutoCloseStream = false }, ct);

    public async Task<StoredObject?> GetAsync(string bucket, string key, CancellationToken ct = default)
    {
        try
        {
            var response = await s3.GetObjectAsync(bucket, key, ct);
            return new StoredObject(response.ResponseStream, response.Headers.ContentType, response.ContentLength);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<ObjectInfo?> HeadAsync(string bucket, string key, CancellationToken ct = default)
    {
        try
        {
            var meta = await s3.GetObjectMetadataAsync(bucket, key, ct);
            return new ObjectInfo(key, meta.ContentLength, meta.LastModified);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<ObjectInfo>> ListAsync(string bucket, string? prefix = null, CancellationToken ct = default)
    {
        var result = new List<ObjectInfo>();
        var request = new ListObjectsV2Request { BucketName = bucket, Prefix = prefix };
        ListObjectsV2Response page;
        do
        {
            page = await s3.ListObjectsV2Async(request, ct);
            result.AddRange((page.S3Objects ?? []).Select(o => new ObjectInfo(o.Key, o.Size ?? 0, o.LastModified)));
            request.ContinuationToken = page.NextContinuationToken;
        }
        while (page.IsTruncated == true);

        return result;
    }

    public async Task DeleteAsync(string bucket, string key, CancellationToken ct = default) => await s3.DeleteObjectAsync(bucket, key, ct);
}
