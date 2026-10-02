using System.Text.RegularExpressions;
using Hangfire;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Platform.Modules.Tenancy.Contracts;
using Platform.Shared.Storage;

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
/// The referenced logos are read first, then the objects are listed; an object written after the read is younger than the
/// grace period whatever it is named, and a replacing put refreshes the time, so an in-flight save cannot lose its object.
/// Objects not named like a logo are never touched. Deleting is idempotent and one failure does not stop the run. The log
/// carries counts, tenant ids and error types only (N-10).
/// </para>
/// </summary>
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
        var referenced = await ReferencedKeysAsync(cancellationToken);
        var cutoff = clock.GetUtcNow() - options.Value.GracePeriod;
        int examined = 0, deleted = 0, failed = 0;

        foreach (var tenantId in (await tenants.ListAsync(cancellationToken)).Keys)
        {
            var objects = await storage.ListAsync($"tenants/{tenantId:D}/branding/", cancellationToken);
            foreach (var item in objects.Where(o => LogoKey().IsMatch(o.Key)))
            {
                examined++;
                if (referenced.Contains(item.Key) || item.LastModified > cutoff)
                {
                    continue;
                }

                try
                {
                    await storage.DeleteAsync(item.Key, cancellationToken);
                    deleted++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    failed++;
                    LogDeleteFailed(logger, tenantId, ex.GetType().Name);
                }
            }
        }

        LogRun(logger, examined, deleted, failed);
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Branding logo cleanup: {Examined} logo objects examined, {Deleted} unreferenced ones deleted, {Failed} failed.")]
    private static partial void LogRun(ILogger logger, int examined, int deleted, int failed);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A logo object of tenant {TenantId} could not be deleted ({ErrorType}); the next run tries again.")]
    private static partial void LogDeleteFailed(ILogger logger, Guid tenantId, string errorType);
}
