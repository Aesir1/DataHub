using System.Globalization;
using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Util;
using Microsoft.Extensions.Options;

namespace DataHub.Webhook;

public interface IPayloadStore
{
    /// <summary>Stores the raw bytes as <c>{sender}/{timestamp}.json</c> and returns the object key.</summary>
    Task<string> PutAsync(string sender, byte[] body, CancellationToken ct);
}

public sealed class S3PayloadStore(IAmazonS3 s3, IOptions<ObjectStorageOptions> options, TimeProvider time) : IPayloadStore, IDisposable
{
    private readonly SemaphoreSlim bucketLock = new(1, 1);
    private bool bucketReady;

    public async Task<string> PutAsync(string sender, byte[] body, CancellationToken ct)
    {
        await EnsureBucketAsync(ct);
        const int maxAttempts = 3;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var key = $"{sender}/{time.GetUtcNow().UtcDateTime.ToString("yyyyMMdd'T'HHmmssfffffff'Z'", CultureInfo.InvariantCulture)}.json";
            try
            {
                using var content = new MemoryStream(body, writable: false);
                await s3.PutObjectAsync(
                    new PutObjectRequest
                    {
                        BucketName = options.Value.Bucket,
                        Key = key,
                        InputStream = content,
                        ContentType = "application/json",
                        IfNoneMatch = "*", // never overwrite a payload that landed on the same tick
                    },
                    ct);
                return key;
            }
            catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed && attempt < maxAttempts)
            {
                await Task.Delay(1, ct);
            }
        }

        throw new InvalidOperationException("Unreachable: the last attempt rethrows.");
    }

    public void Dispose() => bucketLock.Dispose();

    private async Task EnsureBucketAsync(CancellationToken ct)
    {
        if (bucketReady)
        {
            return;
        }

        await bucketLock.WaitAsync(ct);
        try
        {
            if (!await AmazonS3Util.DoesS3BucketExistV2Async(s3, options.Value.Bucket))
            {
                await s3.PutBucketAsync(options.Value.Bucket, ct);
            }

            bucketReady = true;
        }
        finally
        {
            bucketLock.Release();
        }
    }
}
