namespace Platform.Shared.Telemetry;

/// <summary>
/// Names of the telemetry the platform emits (W-10, spec <c>docs/superpowers/specs/2026-09-30-observability-design.md</c>):
/// the pipeline's own names (resource, sources and meters, trace and log attributes, technical metrics; spec section 5 and
/// O-9) in <see cref="Services"/>, <see cref="Resource"/>, <see cref="Sources"/>, <see cref="Attributes"/>,
/// <see cref="Metrics"/> and <see cref="MetricTags"/>, and the business metrics of spec 6.1 on meter <see cref="UsageMeter"/>:
/// every instrument, tag and tag value, the tender ones included, so the tender slices publish under the same constants.
/// </summary>
/// <remarks>
/// Label rules (O-19): tags take the tenant slug and the small fixed value sets below only; never a user id, a vendor
/// company id, an email, a tender id or reference, or text a tenant defines. Platform users carry no tenant tag.
/// Prometheus stores the names with dots as underscores (<c>waslabid_users_active</c>).
/// </remarks>
public static class TelemetryNames
{
    /// <summary>The <c>service.name</c> of each host (spec 5.1).</summary>
    public static class Services
    {
        public const string Web = "waslabid-web";
        public const string Worker = "waslabid-worker";
    }

    /// <summary>Resource attributes every record carries (spec 5.1; OpenTelemetry semantic conventions).</summary>
    public static class Resource
    {
        public const string ServiceName = "service.name";

        /// <summary>The host assembly's informational version: the git commit in CI builds.</summary>
        public const string ServiceVersion = "service.version";

        public const string ServiceInstanceId = "service.instance.id";

        /// <summary><c>development</c>, <c>pilot</c>: setting <c>Telemetry:Environment</c>, by default the host environment in lower case.</summary>
        public const string DeploymentEnvironment = "deployment.environment.name";
    }

    /// <summary>
    /// The platform's own activity sources and meters (spec 5.2, 5.4). Both hosts register every name under
    /// <see cref="OwnPrefix"/>, so a new <c>WaslaBid.*</c> source or meter is exported without touching the registration.
    /// </summary>
    public static class Sources
    {
        /// <summary>The wildcard both hosts register for traces and metrics.</summary>
        public const string OwnPrefix = "WaslaBid.*";

        /// <summary>Hangfire job spans and the job metrics (plan task 2).</summary>
        public const string Jobs = "WaslaBid.Jobs";

        /// <summary>The health job's run and check spans and the health metrics (plan task 4).</summary>
        public const string Operations = "WaslaBid.Operations";

        /// <summary>Npgsql's built-in activity source and meter: statement text only, never parameter values.</summary>
        public const string Npgsql = "Npgsql";
    }

    /// <summary>
    /// Context on every span and log record (O-9): pseudonymous ids only, never an email, a name, a CR or national id
    /// number, a file name or offer content.
    /// </summary>
    public static class Attributes
    {
        /// <summary>The host tenant's id (tenant hosts).</summary>
        public const string TenantId = "waslabid.tenant.id";

        /// <summary>The host tenant's slug (tenant hosts); the same key as the business metrics' <see cref="Tags.TenantSlug"/>.</summary>
        public const string TenantSlug = Tags.TenantSlug;

        /// <summary>The Keycloak <c>sub</c> of the signed-in user, after sign-in (Q7: pseudonymous ids kept).</summary>
        public const string UserId = "user.id";

        /// <summary>The vendor company of a vendor request.</summary>
        public const string VendorCompanyId = "waslabid.vendor_company.id";

        /// <summary>The Hangfire job id (worker).</summary>
        public const string JobId = "waslabid.job.id";

        /// <summary>The job's <c>Type.Method</c> (worker).</summary>
        public const string JobType = "waslabid.job.type";

        /// <summary>The component F-53 groups errors by, derived from the logger category (spec 5.3).</summary>
        public const string Component = "waslabid.component";

        /// <summary>The exception's type on a failed span or job (OpenTelemetry semantic conventions).</summary>
        public const string ExceptionType = "exception.type";
    }

    /// <summary>The technical metrics of spec 5.4; they never carry a tenant label.</summary>
    public static class Metrics
    {
        /// <summary>Hangfire job duration, tag <see cref="MetricTags.JobType"/>.</summary>
        public const string JobsDuration = "waslabid.jobs.duration";

        /// <summary>Hangfire job failures, tag <see cref="MetricTags.JobType"/>.</summary>
        public const string JobsFailed = "waslabid.jobs.failed";

        /// <summary>Each health result: 0 Healthy, 1 Degraded, 2 Unhealthy; tag <see cref="MetricTags.Component"/>.</summary>
        public const string HealthStatus = "waslabid.health.status";

        /// <summary>Each health check's duration; tag <see cref="MetricTags.Component"/>.</summary>
        public const string HealthCheckDuration = "waslabid.health.check.duration";
    }

    /// <summary>Tag keys of the technical metrics (spec 5.4).</summary>
    public static class MetricTags
    {
        /// <summary>
        /// The job's <c>Type.Method</c>: the same name as the span and log attribute <see cref="Attributes.JobType"/> (O-9;
        /// spec 5.4 aligned 2026-10-01), so a metric, a span and a log line of one job type share a key.
        /// </summary>
        public const string JobType = Attributes.JobType;

        /// <summary>A <c>HealthComponents</c> name.</summary>
        public const string Component = "component";
    }

    /// <summary>The meter of the business metrics (spec 6.1).</summary>
    public const string UsageMeter = "WaslaBid.Usage";

    /// <summary>Blazor circuits whose connection is up now, on this web instance (spec 6.3). Observable gauge.</summary>
    public const string CircuitsConnected = "waslabid.circuits.connected";

    /// <summary>Distinct signed-in users with at least one connected circuit (spec 6.3). Observable gauge.</summary>
    public const string UsersConcurrent = "waslabid.users.concurrent";

    /// <summary>Distinct users with an authenticated request or circuit activity in the window (spec 6.4). Observable gauge.</summary>
    public const string UsersActive = "waslabid.users.active";

    /// <summary><see cref="UsersActive"/> across all tenants, each user once (spec 6.4). Observable gauge.</summary>
    public const string UsersActiveAllTenants = "waslabid.users.active.all_tenants";

    /// <summary>Tenders per F-15 stage now, published by the tender slice (spec 6.5). Observable gauge.</summary>
    public const string Tenders = "waslabid.tenders";

    /// <summary>
    /// Opportunities now, published by the tender slice (spec 6.5, Q9 adopted as recommended on 2026-09-30): a tender a
    /// vendor can bid on now, that is stage <see cref="TenderStages.Published"/> or <see cref="TenderStages.Clarification"/>,
    /// visibility <see cref="TenderVisibilities.Open"/> or <see cref="TenderVisibilities.Invited"/>, and a submission
    /// deadline in the future. A draft, a closed tender or a tender past its deadline is not one. A tender listed in the F-62
    /// directory is counted once, with <see cref="Directories.Listed"/>, never a second time. Observable gauge.
    /// </summary>
    public const string Opportunities = "waslabid.opportunities";

    /// <summary>Units in the UCUM annotation form OpenTelemetry uses.</summary>
    public static class Units
    {
        public const string Circuits = "{circuit}";
        public const string Users = "{user}";
        public const string Tenders = "{tender}";
    }

    /// <summary>The only tag keys the business metrics use.</summary>
    public static class Tags
    {
        public const string TenantSlug = "waslabid.tenant.slug";
        public const string UserKind = "waslabid.user.kind";
        public const string Window = "waslabid.window";
        public const string TenderState = "waslabid.tender.state";
        public const string TenderVisibility = "waslabid.tender.visibility";
        public const string Directory = "waslabid.directory";
    }

    /// <summary>Values of <see cref="Tags.UserKind"/> (O-24, spec 6.2).</summary>
    public static class UserKinds
    {
        public const string Staff = "staff";
        public const string Vendor = "vendor";
        public const string Platform = "platform";
    }

    /// <summary>Values of <see cref="Tags.Window"/>: rolling windows to the hour (spec 6.4).</summary>
    public static class Windows
    {
        public const string OneDay = "1d";
        public const string SevenDays = "7d";
        public const string ThirtyDays = "30d";

        public static IReadOnlyList<string> All { get; } = [OneDay, SevenDays, ThirtyDays];
    }

    /// <summary>Values of <see cref="Tags.TenderState"/>: the fixed F-15 stages in snake case, never a tenant's own workflow step (F-56).</summary>
    public static class TenderStages
    {
        public const string Draft = "draft";
        public const string Published = "published";
        public const string Clarification = "clarification";
        public const string Closed = "closed";
        public const string ComplianceScreening = "compliance_screening";
        public const string TechnicalEvaluation = "technical_evaluation";
        public const string TechnicalLocked = "technical_locked";
        public const string FinancialOpening = "financial_opening";
        public const string FinancialEvaluation = "financial_evaluation";
        public const string FinanceApproval = "finance_approval";
        public const string Awarded = "awarded";
        public const string Cancelled = "cancelled";

        public static IReadOnlyList<string> All { get; } =
        [
            Draft, Published, Clarification, Closed, ComplianceScreening, TechnicalEvaluation, TechnicalLocked,
            FinancialOpening, FinancialEvaluation, FinanceApproval, Awarded, Cancelled,
        ];
    }

    /// <summary>Values of <see cref="Tags.TenderVisibility"/> (ADR-0008: tenders are Invited or Open).</summary>
    public static class TenderVisibilities
    {
        public const string Invited = "invited";
        public const string Open = "open";
    }

    /// <summary>Values of <see cref="Tags.Directory"/>: <see cref="Listed"/> only once F-62 exists.</summary>
    public static class Directories
    {
        public const string NotListed = "not_listed";
        public const string Listed = "listed";
    }
}
