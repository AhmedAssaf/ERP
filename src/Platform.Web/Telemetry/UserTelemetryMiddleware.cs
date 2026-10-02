using Platform.Shared.Tenancy;

namespace Platform.Web.Telemetry;

/// <summary>
/// O-9 for the signed-in user and the vendor company: one middleware with two entry points, each placed where its value is
/// set (<see cref="UserTelemetryMiddlewareExtensions"/>). After <c>ActingUserMiddleware</c> it tags the server span with
/// <c>user.id</c> (the acting user, which is the authenticated principal's <c>sub</c>; never the email, the user name or the
/// display name) and opens a scope nested in the tenant's; after <c>VendorContextMiddleware</c> it does the same with
/// <c>waslabid.vendor_company.id</c> when the request has a vendor context. Nothing is added when the value is not set.
/// </summary>
internal sealed class UserTelemetryMiddleware(
    RequestDelegate next, ILogger<UserTelemetryMiddleware> logger, UserTelemetryMiddleware.Part part)
{
    /// <summary>Which value an instance of the middleware adds.</summary>
    public enum Part
    {
        User,
        VendorCompany,
    }

    public async Task InvokeAsync(HttpContext context, IActingUserAccessor actingUser, IVendorAccessor vendor)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(actingUser);
        ArgumentNullException.ThrowIfNull(vendor);
        var tags = part switch
        {
            Part.User when !string.IsNullOrEmpty(actingUser.UserId) => TelemetryContext.User(actingUser.UserId),
            Part.VendorCompany when vendor.Current is { } company => TelemetryContext.Vendor(company),
            _ => null,
        };
        if (tags is null)
        {
            await next(context);
            return;
        }

        TelemetryContext.Tag(RequestSpan.Of(context), tags);
        using (logger.BeginScope(tags))
        {
            await next(context);
        }
    }
}

internal static class UserTelemetryMiddlewareExtensions
{
    /// <summary>Directly after <c>ActingUserMiddleware</c>: <c>user.id</c>.</summary>
    public static IApplicationBuilder UseUserTelemetry(this IApplicationBuilder app) =>
        app.UseMiddleware<UserTelemetryMiddleware>(UserTelemetryMiddleware.Part.User);

    /// <summary>Directly after <c>VendorContextMiddleware</c>: <c>waslabid.vendor_company.id</c>.</summary>
    public static IApplicationBuilder UseVendorCompanyTelemetry(this IApplicationBuilder app) =>
        app.UseMiddleware<UserTelemetryMiddleware>(UserTelemetryMiddleware.Part.VendorCompany);
}
