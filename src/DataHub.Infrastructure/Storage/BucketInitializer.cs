using Amazon.S3;
using Amazon.S3.Util;
using DataHub.Domain.Documents;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DataHub.Infrastructure.Storage;

/// <summary>ST-2: creates the buckets on startup; both stay private; the Api serves their objects.</summary>
public sealed class BucketInitializer(IAmazonS3 s3, ILogger<BucketInitializer> logger) : IHostedService
{
    public const string BrandingBucket = "branding";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var bucket in new[] { Document.Bucket, BrandingBucket })
        {
            if (!await AmazonS3Util.DoesS3BucketExistV2Async(s3, bucket))
            {
                await s3.PutBucketAsync(bucket, cancellationToken);
                logger.LogInformation("Created bucket {Bucket}", bucket);
            }
        }

        // Older versions made branding anonymously readable; drop that policy.
        await s3.DeleteBucketPolicyAsync(BrandingBucket, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
