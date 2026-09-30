using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Platform.Modules.Identity.Contracts;

namespace Platform.Web.Vendor;

/// <summary>
/// W-33, ADR-0013 decision 1: every post of <c>/vendor/dispute</c> counts, whatever it asks, per signed-in person (their
/// <c>sub</c>) or, without one, per client address: <see cref="PermitLimit"/> posts in <see cref="Window"/>, then 429.
/// Reading the page is not limited. The database's limit (three pending disputes per person; a company's count never refuses one) and the
/// duplicate-CR limit stay as they are; this only slows down someone who posts in a loop.
/// </summary>
internal static class DisputeRateLimit
{
    public const string Policy = "vendor-dispute";

    public const int PermitLimit = 5;

    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    public static IServiceCollection AddVendorDisputeRateLimit(this IServiceCollection services) =>
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(Policy, context =>
            {
                if (!HttpMethods.IsPost(context.Request.Method))
                {
                    return RateLimitPartition.GetNoLimiter(string.Empty);
                }

                var key = context.User.FindFirst(IdentityClaims.Subject)?.Value is { Length: > 0 } subject
                    ? $"sub:{subject}"
                    : $"ip:{context.Connection.RemoteIpAddress}";
                return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = PermitLimit,
                    Window = Window,
                    QueueLimit = 0,
                });
            });
        });
}
