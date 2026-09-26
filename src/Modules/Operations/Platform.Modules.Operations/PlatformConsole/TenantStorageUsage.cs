using System.Collections.Concurrent;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging;
using Platform.Modules.Operations.Contracts;

namespace Platform.Modules.Operations.PlatformConsole;

/// <summary>
/// D-12: the sum of object sizes under <c>tenants/{tenantId}/</c>, cached per tenant for ten minutes, so opening the
/// tenant list does not walk every tenant's objects each time. A failed listing is logged, not cached, and unknown.
/// </summary>
internal sealed partial class TenantStorageUsage(ObjectStorageSettings settings, TimeProvider clock, ILogger<TenantStorageUsage> logger)
    : ITenantStorageUsage, IDisposable
{
    internal static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<Guid, (long Bytes, DateTimeOffset At)> _cache = new();
    private readonly Lazy<AmazonS3Client> _client = new(() => new AmazonS3Client(
        settings.AccessKey, settings.SecretKey, new AmazonS3Config { ServiceURL = settings.ServiceUrl, ForcePathStyle = true }));

    public static string PrefixFor(Guid tenantId) => $"tenants/{tenantId}/";

    public async Task<long?> UsedBytesAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        if (!settings.IsConfigured)
        {
            return null;
        }

        var now = clock.GetUtcNow();
        if (_cache.TryGetValue(tenantId, out var cached) && now - cached.At < CacheFor)
        {
            return cached.Bytes;
        }

        try
        {
            long total = 0;
            var request = new ListObjectsV2Request { BucketName = settings.BucketName, Prefix = PrefixFor(tenantId) };
            ListObjectsV2Response response;
            do
            {
                response = await _client.Value.ListObjectsV2Async(request, cancellationToken);
                total += response.S3Objects?.Sum(o => o.Size ?? 0) ?? 0;
                request.ContinuationToken = response.NextContinuationToken;
            }
            while (response.IsTruncated == true);

            _cache[tenantId] = (total, now);
            return total;
        }
        catch (Exception ex) when (ex is AmazonServiceException or HttpRequestException or IOException)
        {
            // Never ex.Message (N-10): an S3 error can echo the endpoint or the request signature.
            LogListingFailed(logger, ex.GetType().Name);
            return null;
        }
    }

    public void Dispose()
    {
        if (_client.IsValueCreated)
        {
            _client.Value.Dispose();
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tenant storage usage could not be listed ({ErrorType}); it is shown as unknown.")]
    private static partial void LogListingFailed(ILogger logger, string errorType);
}
