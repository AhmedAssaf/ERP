using System.Text.RegularExpressions;
using Hangfire;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Platform.Modules.Tenancy.Contracts;
using Platform.Shared.Storage;
using Platform.Shared.Jobs;

namespace Platform.Modules.Tenancy.Branding;

/// <summary>How long an unreferenced logo object is kept before the cleanup may delete it (setting <c>Branding:LogoCleanup:GracePeriod</c>).</summary>
internal sealed class BrandingLogoCleanupOptions
{
    public const string Section = "Branding:LogoCleanup";

    /// <summary>Default one hour: far longer than one save takes, so no save in flight loses its object.</summary>
    public TimeSpan GracePeriod { get; set; } = TimeSpan.FromHours(1);
}

/// <summary>
/// The recurring job "branding-logo-cleanup" (W-38, F-02), hourly in the worker. A logo is written to object storage
/// before the branding row is saved, so a save refused or failed after the upload leaves an object no tenant references.
/// The save never deletes it itself: the object is named by its content, so a second save of the same logo may be writing
/// or have just committed that very key. This job deletes, under each tenant's <c>branding/</c> prefix, the logo objects
/// that no tenant references and that were last written more than the grace period ago.
/// <para>
/// The guarantee: an object is deleted only if, right before its delete, its last write is older than the grace period
/// and no tenant references it (the references are read again for each delete, and the object's metadata is requested
/// again). A save that writes the object puts it first, which refreshes the time, and commits its reference seconds
/// later, so a save can lose its object only if it takes longer than the grace period or its commit lands in the few
/// milliseconds between that last check and the delete. Objects not named like a logo are never touched. Deleting is
/// idempotent; one failure, in a delete or in listing one tenant's prefix, does not stop the run. The log carries
/// counts, tenant ids and error types only (N-10).
/// </para>
/// </summary>
[PlatformJob]
internal sealed partial class BrandingLogoCleanupJob(
    [FromKeyedServices(TenancyModule.DataSourceKey)] NpgsqlDataSource dataSource,
    ITenantSlugs tenants,
    IObjectStorage storage,
    IOptions<BrandingLogoCleanupOptions> options,
    TimeProvider clock,
    ILogger<BrandingLogoCleanupJob> logger)
{
    [DisableConcurrentExecution(timeoutInSeconds: 600)]
    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var cutoff = clock.GetUtcNow() - options.Value.GracePeriod;
        int examined = 0, deleted = 0, failed = 0;

        foreach (var tenantId in (await tenants.ListAsync(cancellationToken)).Keys)
        {
            try
            {
                var objects = await storage.ListAsync($"tenants/{tenantId:D}/branding/", cancellationToken);
                var candidates = objects.Where(o => LogoKey().IsMatch(o.Key) && o.LastModified <= cutoff).ToList();
                examined += objects.Count(o => LogoKey().IsMatch(o.Key));
                if (candidates.Count == 0)
                {
                    continue;
                }

                var referenced = await ReferencedKeysAsync(cancellationToken);
                foreach (var item in candidates.Where(c => !referenced.Contains(c.Key)))
                {
                    if (await DeleteIfStillUnusedAsync(item.Key, cutoff, cancellationToken))
                    {
                        deleted++;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                failed++;
                LogTenantFailed(logger, tenantId, ex.GetType().Name);
            }
        }

        LogRun(logger, examined, deleted, failed);
    }

    /// <summary>Checks the object and the references once more, right before the delete; false when it was skipped.</summary>
    private async Task<bool> DeleteIfStillUnusedAsync(string key, DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        // A re-upload of the same content refreshes the object and commits its reference after the listing.
        if (await storage.GetInfoAsync(key, cancellationToken) is not { } current || current.LastModified > cutoff)
        {
            return false;
        }

        if ((await ReferencedKeysAsync(cancellationToken)).Contains(key))
        {
            return false;
        }

        await storage.DeleteAsync(key, cancellationToken);
        return true;
    }

    private async Task<HashSet<string>> ReferencedKeysAsync(CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("select tenant_id, logo_url from tenancy.referenced_logos()");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken))
        {
            var match = LogoUrl().Match(reader.GetString(1));
            if (match.Success)
            {
                keys.Add(BrandingService.LogoKey(reader.GetGuid(0), match.Groups[1].Value));
            }
        }

        return keys;
    }

    [GeneratedRegex(@"^/branding/logo/([a-f0-9]{64})\.png\z", RegexOptions.CultureInvariant)]
    private static partial Regex LogoUrl();

    [GeneratedRegex(@"^tenants/[0-9a-f-]{36}/branding/logo-[a-f0-9]{64}\.png\z", RegexOptions.CultureInvariant)]
    private static partial Regex LogoKey();

    [LoggerMessage(Level = LogLevel.Information, Message = "Branding logo cleanup: {Examined} logo objects examined, {Deleted} unreferenced ones deleted, {Failed} tenants failed.")]
    private static partial void LogRun(ILogger logger, int examined, int deleted, int failed);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The logo cleanup of tenant {TenantId} failed ({ErrorType}); the run goes on with the next tenant and the next run tries again.")]
    private static partial void LogTenantFailed(ILogger logger, Guid tenantId, string errorType);
}
