using Amazon.S3;
using Amazon.S3.Util;
using DataHub.Domain.Documents;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DataHub.Infrastructure.Storage;

/// <summary>ST-2: creates the buckets on startup; <c>branding</c> is anonymously readable (login background, logo).</summary>
public sealed class BucketInitializer(IAmazonS3 s3, ILogger<BucketInitializer> logger) : IHostedService
{
    public const string BrandingBucket = "branding";

    private const string PublicReadPolicy =
        """{"Version":"2012-10-17","Statement":[{"Effect":"Allow","Principal":{"AWS":["*"]},"Action":["s3:GetObject"],"Resource":["arn:aws:s3:::branding/*"]}]}""";

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

        await s3.PutBucketPolicyAsync(BrandingBucket, PublicReadPolicy, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
