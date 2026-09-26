using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Platform.Modules.Vendors.Access;
using Platform.Modules.Vendors.Contracts;
using Platform.Modules.Vendors.Persistence;
using Platform.Modules.Vendors.Registration;
using Platform.Shared.Data;

namespace Platform.Modules.Vendors;

/// <summary>
/// The Vendors module (vendor slice, ADR-0008): one vendor company across tenants, schema <c>vendor</c>. Platform-level
/// company rows sit under row-level security keyed on the vendor company; tenant relationships under the tenant policy.
/// </summary>
public static class VendorsModule
{
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
    /// Identity module's member directory and vendor accounts and the audit writer), the user-to-company lookup
    /// (<see cref="IVendorUsers"/>), the current company (<see cref="IVendorCompanies"/>) and the Vendor policy's handler.
    /// The web host calls it; the worker does not serve vendors.
    /// </summary>
    public static IServiceCollection AddVendorPortal(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHttpContextAccessor();
        services.AddScoped<VendorUsers>();
        services.AddScoped<IVendorUsers>(sp => sp.GetRequiredService<VendorUsers>());
        services.AddScoped<IVendorCompanies, VendorCompanies>();
        services.AddScoped<IAuthorizationHandler, VendorCompanyHandler>();
        services.AddScoped<IVendorRegistration, VendorRegistrationService>();
        return services;
    }

    public static Task<IReadOnlyList<string>> MigrateAsync(NpgsqlConnection connection, CancellationToken cancellationToken = default) =>
        SqlMigrator.ApplyAsync(connection, "vendors", typeof(VendorsModule).Assembly, cancellationToken);
}
