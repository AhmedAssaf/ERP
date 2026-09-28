using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Platform.Modules.Identity.Contracts;
using Platform.Modules.Vendors.Contracts;
using Platform.Shared.Tenancy;
using Platform.Web.PlatformHost;
using Platform.Web.Tenancy;

namespace Platform.Web.Vendor;

/// <summary>
/// Sets the vendor context of a request or circuit (vendor spec section 2): only on a tenant host, only for a principal
/// that passes the whole Vendor policy there (verified email, realm role, a vendor row, membership of the host tenant's
/// organization), or on the join page the JoiningVendor policy (the same without the membership, which joining adds), and
/// only with the company found for that principal's own <c>sub</c>. Nothing a client sends can name a company. A principal
/// without the vendor role is not evaluated at all, so staff requests cost no lookup.
/// </summary>
internal sealed class VendorContextResolver(
    ITenantAccessor tenants, IAuthorizationService authorization, IVendorUsers users, VendorAccessor accessor)
{
    public Task ResolveAsync(ClaimsPrincipal? user, CancellationToken cancellationToken) =>
        ResolveAsync(user, VendorPolicies.Vendor, cancellationToken);

    /// <summary>Sets the vendor context after <paramref name="policy"/> (Vendor or JoiningVendor) passed for the principal.</summary>
    public async Task ResolveAsync(ClaimsPrincipal? user, string policy, CancellationToken cancellationToken)
    {
        if (policy is not (VendorPolicies.Vendor or VendorPolicies.JoiningVendor))
        {
            throw new ArgumentException($"The vendor context follows the {VendorPolicies.Vendor} or {VendorPolicies.JoiningVendor} policy only.", nameof(policy));
        }

        if (tenants.Current is null || user?.Identity?.IsAuthenticated != true
            || !user.HasClaim(IdentityClaims.Roles, IdentityClaims.VendorRealmRole)
            || user.FindFirst(IdentityClaims.Subject)?.Value is not { Length: > 0 } userId)
        {
            return;
        }

        if (!(await authorization.AuthorizeAsync(user, policy)).Succeeded)
        {
            return;
        }

        if (await users.FindCompanyAsync(userId, cancellationToken) is { } companyId)
        {
            accessor.Set(new VendorContext(companyId));
        }
    }
}

/// <summary>
/// After authorization: an endpoint that requires the Vendor policy, or the JoiningVendor policy of the join page (and does
/// not allow anonymous access), gets the vendor context of its already-authorized user, so its database connections carry
/// <c>app.vendor_company_id</c>. The platform host never has one.
/// </summary>
internal sealed class VendorContextMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, VendorContextResolver resolver)
    {
        if (!PlatformRequest.IsPlatform(context) && VendorPolicyOf(context.GetEndpoint()) is { } policy)
        {
            await resolver.ResolveAsync(context.User, policy, context.RequestAborted);
        }

        await next(context);
    }

    private static string? VendorPolicyOf(Endpoint? endpoint)
    {
        if (endpoint is null || endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
        {
            return null;
        }

        var policies = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy).ToList();
        return policies.Contains(VendorPolicies.Vendor) ? VendorPolicies.Vendor
            : policies.Contains(VendorPolicies.JoiningVendor) ? VendorPolicies.JoiningVendor
            : null;
    }
}

/// <summary>
/// A circuit's acting user and vendor context, from the principal of its connection request (<c>/_blazor</c>, which
/// passed authentication), after <see cref="Tenancy.TenantCircuitHandler"/> set the tenant. The principal is fixed for
/// the circuit's life, so both are set once when the circuit opens. The acting user is set for every authenticated
/// principal on tenant and platform hosts (staff approving a vendor, an applicant registering); the vendor context only
/// on a tenant host and only after the Vendor policy passed.
/// </summary>
internal sealed class VendorCircuitHandler(
    IHttpContextAccessor httpContextAccessor, VendorContextResolver resolver, ActingUserAccessor actingUser) : CircuitHandler
{
    public override int Order => int.MinValue + 1;

    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        var connection = httpContextAccessor.HttpContext;
        if (connection is null)
        {
            return Task.CompletedTask;
        }

        ActingUser.SetFrom(connection.User, actingUser);
        return PlatformRequest.IsPlatform(connection)
            ? Task.CompletedTask
            : resolver.ResolveAsync(connection.User, cancellationToken);
    }
}

/// <summary>
/// <c>GET /vendor/register</c> (spec section 3): sends a visitor to Keycloak's self-registration page of the tenant realm
/// with the tenant host's own callback, and back to <c>/vendor/register/company</c> afterwards. Someone already signed in
/// goes straight to the company form.
/// </summary>
/// <remarks>
/// Keycloak 26.3 reads <c>prompt=create</c> only from the front-channel query of the authorization endpoint, and
/// <c>waslabid-web</c> requires pushed authorization requests, whose front-channel query carries only <c>client_id</c> and
/// <c>request_uri</c>; the prompt is pushed but ignored there. So the challenge is marked, and
/// <see cref="UseRegistrationEndpoint"/> sends the browser to Keycloak's registration endpoint
/// (<c>.../protocol/openid-connect/registrations</c>) instead, which runs the same pushed request into the registration flow.
/// </remarks>
internal static class VendorRegistrationEndpoints
{
    public const string StartPath = "/vendor/register";
    public const string CompanyPath = "/vendor/register/company";
    public const string HomePath = "/vendor";

    private const string RegistrationItem = "waslabid.vendor_registration";

    public static IEndpointRouteBuilder MapVendorRegistrationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(StartPath, (HttpContext context) =>
            {
                if (context.User.Identity?.IsAuthenticated == true)
                {
                    return Results.LocalRedirect(CompanyPath);
                }

                var properties = new OpenIdConnectChallengeProperties { RedirectUri = CompanyPath, Prompt = "create" };
                properties.Items[RegistrationItem] = "true";
                return Results.Challenge(properties, [OpenIdConnectDefaults.AuthenticationScheme]);
            })
            .AllowAnonymous();
        return app;
    }

    /// <summary>
    /// The tenant scheme's redirect event: a challenge from <see cref="StartPath"/> goes to the realm's registration
    /// endpoint; every other challenge is left as it is.
    /// </summary>
    public static Task UseRegistrationEndpoint(RedirectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Properties.Items.ContainsKey(RegistrationItem)
            && context.ProtocolMessage.IssuerAddress is { } authorize
            && authorize.EndsWith("/protocol/openid-connect/auth", StringComparison.Ordinal))
        {
            context.ProtocolMessage.IssuerAddress = authorize[..^"auth".Length] + "registrations";
        }

        return Task.CompletedTask;
    }
}
