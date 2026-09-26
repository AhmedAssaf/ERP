using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Platform.Modules.Operations.Health;

/// <summary>Spec 3.2: the object storage bucket exists, checked path-style (MinIO does not support virtual-host style).</summary>
internal sealed class MinIoHealthCheck(string serviceUrl, string bucketName, string? accessKey, string? secretKey) : IHealthCheck
{
    private const string Component = "MinIO";

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accessKey) || string.IsNullOrWhiteSpace(secretKey))
        {
            return HealthCheckResult.Unhealthy($"{Component} is not configured.");
        }

        try
        {
            var config = new AmazonS3Config { ServiceURL = serviceUrl, ForcePathStyle = true };
            using var client = new AmazonS3Client(accessKey, secretKey, config);
            await client.ListObjectsV2Async(
                new ListObjectsV2Request { BucketName = bucketName, MaxKeys = 1 }, cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            // Never ex.Message (N-10): AmazonS3Exception can echo the request signature or endpoint.
            return HealthCheckResult.Unhealthy(
                HealthCheckMessages.WithExceptionType(ex, HealthCheckMessages.CouldNotReach(Component)));
        }
    }
}
