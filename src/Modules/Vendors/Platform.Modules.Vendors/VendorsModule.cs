using Hangfire;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Npgsql;
using Platform.Modules.Vendors.Access;
using Platform.Modules.Vendors.Consent;
using Platform.Modules.Vendors.Contracts;
using Platform.Modules.Vendors.Documents;
using Platform.Modules.Vendors.Ownership;
using Platform.Modules.Vendors.Persistence;
using Platform.Modules.Vendors.RateLimiting;
using Platform.Modules.Vendors.Registration;
using Platform.Modules.Vendors.Relationships;
using Platform.Shared.Caching;
using Platform.Shared.Data;
using Platform.Shared.Scanning;

namespace Platform.Modules.Vendors;

/// <summary>
/// The Vendors module (vendor slice, ADR-0008): one vendor company across tenants, schema <c>vendor</c>. Platform-level
/// company rows sit under row-level security keyed on the vendor company; tenant relationships under the tenant policy.
/// </summary>
public static class VendorsModule
{
    /// <summary>The recurring job that scans pending vendor documents again (V-10), every five minutes.</summary>
    public const string DocumentRescanJobId = "vendor-document-rescan";

    /// <summary>The recurring job that removes uploads abandoned for more than a day (V-9), hourly.</summary>
    public const string UploadCleanupJobId = "vendor-upload-cleanup";

    /// <summary>The recurring job that tells the platform admins about new CR ownership disputes (W-33), every five minutes.</summary>
    public const string DisputeAlertJobId = "vendor-dispute-alert";

    /// <summary>
    /// The Vendor policy's own requirements (spec section 3), without the same-tenant check: authenticated, a verified
    /// email, the Keycloak realm role <c>vendor</c> in the token, and a <c>vendor.vendor_users</c> row for the token's
    /// <c>sub</c>. The web host combines them with the Identity module's same-tenant policy (membership of the host
    /// tenant's organization) under <see cref="VendorPolicies.Vendor"/>.
    /// </summary>
    public static AuthorizationPolicy VendorRequirements { get; } = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .RequireAssertion(VendorClaims.EmailVerified)
        .RequireClaim(Identity.Contracts.IdentityClaims.Roles, Identity.Contracts.IdentityClaims.VendorRealmRole)
        .AddRequirements(new VendorCompanyRequirement())
        .Build();

    /// <summary>
    /// <see cref="VendorPolicies.VendorApplicant"/>: authenticated with a verified email. Deliberately no same-tenant check:
    /// a new vendor is not a member of any organization until its company is registered.
    /// </summary>
    public static AuthorizationPolicy VendorApplicantPolicy { get; } = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .RequireAssertion(VendorClaims.EmailVerified)
        .Build();

    public static IServiceCollection AddVendorsModule(this IServiceCollection services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        services.AddModuleDbContext<VendorsDbContext>(connectionString);
        return services;
    }

    /// <summary>
    /// The vendor pages' services (vendor plan task 2): registration (<see cref="IVendorRegistration"/>, which needs the
    /// Identity module's member directory and vendor accounts, the audit writer and the Operations module's platform
    /// audit, and keeps its duplicate-CR limits in Redis when <c>ConnectionStrings:Redis</c> is set, W-34), the user-to-company lookup
    /// (<see cref="IVendorUsers"/>), the current company (<see cref="IVendorCompanies"/>), the Vendor policy's handler, the
    /// staff's vendor directory with approval (<see cref="IVendorDirectory"/>), joining another tenant (<see cref="IVendorJoin"/>)
    /// the consent ledger (<see cref="IConsentLedger"/>), and the CR ownership check and disputes (W-33:
    /// <see cref="ICrOwnershipAdministration"/>, <see cref="ICrDisputes"/>, the optional Wathq settings <c>Wathq:*</c>).
    /// The settings (<see cref="VendorsOptions"/>, section <c>Vendors</c>) are validated when the host starts: without a
    /// usable <c>Vendors:CrAuditKey</c> the web host does not start, in Development too. The web host calls it; the
    /// worker does not serve vendors.
    /// </summary>
    public static IServiceCollection AddVendorPortal(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<VendorsOptions>()
            .Bind(configuration.GetSection(VendorsOptions.Section))
            .Validate(o => VendorsOptions.DecodeCrAuditKey(o.CrAuditKey) is not null, VendorsOptions.CrAuditKeyProblem)
            .Validate(o => o.UploadRequestsPerMinute > 0, "Setting 'Vendors:UploadRequestsPerMinute' must be a positive number.")
            .Validate(o => o.MaxUploadsPerDay > 0, "Setting 'Vendors:MaxUploadsPerDay' must be a positive number.")
            .Validate(o => o.DuplicateCrPerAddress > 0, "Setting 'Vendors:DuplicateCrPerAddress' must be a positive number.")
            .Validate(o => o.DuplicateCrWindow >= TimeSpan.FromSeconds(1), "Setting 'Vendors:DuplicateCrWindow' must be at least one second (for example 01:00:00).")
            .Validate(o => o.JoinsPerUserPerMinute > 0, "Setting 'Vendors:JoinsPerUserPerMinute' must be a positive number.")
            .Validate(o => o.JoinsPerTenantPerMinute > 0, "Setting 'Vendors:JoinsPerTenantPerMinute' must be a positive number.")
            .Validate(o => o.MaxConcurrentJoins > 0, "Setting 'Vendors:MaxConcurrentJoins' must be a positive number.")
            .Validate(o => o.ConsentGrantsPerCompanyPerHour > 0, "Setting 'Vendors:ConsentGrantsPerCompanyPerHour' must be a positive number.")
            .ValidateOnStart();
        services.TryAddSingleton<CrNumberAudit>();
        services.AddHttpContextAccessor();
        services.AddScoped<VendorUsers>();
        services.AddScoped<IVendorUsers>(sp => sp.GetRequiredService<VendorUsers>());
        services.AddScoped<IVendorCompanies, VendorCompanies>();
        services.AddScoped<IAuthorizationHandler, VendorCompanyHandler>();
        services.TryAddSingleton(TimeProvider.System);
        // W-34: the duplicate-CR limits per account and per source address, in Redis when ConnectionStrings:Redis is set
        // (shared by every web instance), in process memory otherwise and while Redis does not answer.
        services.AddRedis(configuration);
        services.TryAddSingleton<DuplicateCrThrottle>();
        // W-35, W-37: the join and consent limits and the cap on joins in flight, counted in this process (one web instance
        // in the pilot, W-19).
        services.TryAddSingleton<VendorRateLimits>();
        services.TryAddSingleton<ConcurrentJoinGate>();
        services.AddScoped<IVendorRegistration, VendorRegistrationService>();
        // Staff view and approval, and joining another tenant (vendor plan task 5, V-7, V-11).
        services.AddScoped<IVendorDirectory, VendorDirectory>();
        services.AddScoped<IVendorJoin, VendorJoin>();
        // The consent ledger (vendor plan task 6, F-64, V-12).
        services.AddScoped<IConsentLedger, ConsentLedger>();
        // W-33: the CR ownership check before a company's first approval, its platform setting and the dispute path. Wathq
        // (settings Wathq:*) is optional; without it the officer checks the CR certificate by hand.
        services.AddOptions<WathqOptions>()
            .Bind(configuration.GetSection(WathqOptions.Section))
            .Validate(o => o.HasValidBaseUrl, WathqOptions.BaseUrlProblem)
            .ValidateOnStart();
        // L-6: never follow a redirect (the apiKey header would go to another host); a 3xx is an answer like any other
        // non-200 and the officer checks by hand. The default request log lines name the full URL, which carries the CR
        // number, so they are removed; the verifier logs the path template itself.
        services.AddHttpClient(WathqCrOwnershipVerifier.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false })
            .RemoveAllLoggers();
        services.AddScoped<ICrOwnershipVerifier, ManualCrOwnershipVerifier>();
        services.AddScoped<ICrOwnershipVerifier, WathqCrOwnershipVerifier>();
        services.AddScoped<ICrOwnershipAdministration, CrOwnershipAdministration>();
        services.AddScoped<ICrDisputes, CrDisputes>();
        AddVendorDocuments(services);
        return services;
    }

    /// <summary>
    /// Upload API requests allowed per vendor company per minute (<c>Vendors:UploadRequestsPerMinute</c>, V-9), for the web
    /// host's rate limiter; the options class itself stays internal to the module.
    /// </summary>
    public static int UploadRequestsPerMinute(IServiceProvider services) =>
        services.GetRequiredService<IOptions<VendorsOptions>>().Value.UploadRequestsPerMinute;

    /// <summary>
    /// The worker's vendor jobs: the retry scan of pending documents and the cleanup of abandoned uploads (vendor plan
    /// task 3). They need object storage and the virus scanner configured by the host (<c>AddObjectStorage</c>,
    /// <c>AddVirusScanner</c>) and the Operations module's platform audit. The dispute alert is <see cref="AddVendorDisputeAlerts"/>.
    /// </summary>
    public static IServiceCollection AddVendorJobs(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        AddVendorDocuments(services);
        services.TryAddScoped<VendorDocumentRescanJob>();
        services.TryAddScoped<VendorUploadCleanupJob>();
        return services;
    }

    /// <summary>
    /// The worker's alert on new CR ownership disputes (W-33, ADR-0013 decision 1). It needs the Operations module's
    /// platform alerts (<c>IPlatformAlerts</c>, registered by <c>AddOperationsAlerts</c>), so it is registered on its own,
    /// by a host that has them; <see cref="ScheduleVendorJobs"/> schedules it only when it is registered.
    /// </summary>
    public static IServiceCollection AddVendorDisputeAlerts(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped<CrDisputeAlertJob>();
        return services;
    }

    /// <summary>
    /// Schedules the vendor document jobs (<see cref="AddVendorJobs"/>), and the dispute alert when it is registered
    /// (<see cref="AddVendorDisputeAlerts"/>). Call once after the worker host is built.
    /// </summary>
    public static void ScheduleVendorJobs(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var jobs = new RecurringJobManager(services.GetRequiredService<JobStorage>());
        jobs.AddOrUpdate<VendorDocumentRescanJob>(DocumentRescanJobId, job => job.RunAsync(CancellationToken.None), "*/5 * * * *");
        jobs.AddOrUpdate<VendorUploadCleanupJob>(UploadCleanupJobId, job => job.RunAsync(CancellationToken.None), Cron.Hourly());
        if (services.GetService<IServiceProviderIsService>()?.IsService(typeof(CrDisputeAlertJob)) == true)
        {
            jobs.AddOrUpdate<CrDisputeAlertJob>(DisputeAlertJobId, job => job.RunAsync(CancellationToken.None), "*/5 * * * *");
        }
    }

    /// <summary>
    /// Vendor documents, chunked uploads and document compliance (F-12, V-8 to V-10). Idempotent, since both the web host
    /// (<see cref="AddVendorPortal"/>) and the worker (<see cref="AddVendorJobs"/>) need them.
    /// </summary>
    private static void AddVendorDocuments(IServiceCollection services)
    {
        if (services.Any(d => d.ServiceType == typeof(VendorDocuments)))
        {
            return;
        }

        // V-10: the largest document must fit into one scan, or clamd would cut it off and never give a verdict.
        services.AddOptions<VendorScanLimit>()
            .Configure<ClamAvSettings>((limit, clamAv) => limit.MaxStreamBytes = clamAv.MaxStreamBytes)
            .Validate(
                limit => limit.MaxStreamBytes >= VendorDocumentLimits.MaxBytes,
                $"Setting 'ClamAv:MaxStreamBytes' must be at least {VendorDocumentLimits.MaxBytes} bytes, the largest vendor document (and no more than clamd's StreamMaxLength).")
            .ValidateOnStart();
        // VendorUploads reads VendorsOptions; a host without AddVendorPortal gets its defaults.
        services.AddOptions();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<VendorDocuments>();
        services.TryAddScoped<IVendorDocuments>(sp => sp.GetRequiredService<VendorDocuments>());
        services.TryAddScoped<IVendorUploads, VendorUploads>();
        services.TryAddScoped<IVendorCompliance, VendorCompliance>();
    }

    public static Task<IReadOnlyList<string>> MigrateAsync(NpgsqlConnection connection, CancellationToken cancellationToken = default) =>
        SqlMigrator.ApplyAsync(connection, "vendors", typeof(VendorsModule).Assembly, cancellationToken);

    /// <summary>The scanner limit the start validation of <see cref="AddVendorDocuments"/> checks.</summary>
    private sealed class VendorScanLimit
    {
        public long MaxStreamBytes { get; set; }
    }
}
