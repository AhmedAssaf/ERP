using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Platform.Modules.Identity.Contracts;
using Platform.Modules.Identity.Members;
using Platform.Shared.Tenancy;

namespace Platform.Modules.Identity.Activity;

/// <summary>
/// Writes the scope's hourly activity bucket into <c>identity.user_activity</c> (spec 6.4) through the scope's own
/// connection, so row-level security checks that the row is the acting user's own, for the host tenant, in the kind the
/// vendor context allows; the database sets the hour. At most once an hour (<see cref="ActivityThrottle"/>). A write gets
/// at most <see cref="WriteTimeout"/>. A failure (an error or that timeout) is logged with its type only, once per
/// <see cref="ActivityThrottle.RetryAfter"/>, and swallowed, never failing the request or circuit event; it is not
/// remembered as written, and for <see cref="ActivityThrottle.RetryAfter"/> no request or circuit event of this process
/// touches the database for activity, so an outage or a missing table does not make every request wait and log.
/// </summary>
internal sealed partial class UserActivityRecorder(
    ITenantAccessor tenants,
    IActingUserAccessor actingUser,
    ActivityThrottle throttle,
    IDbContextFactory<MembersDbContext> contexts,
    ILogger<UserActivityRecorder> logger) : IUserActivityRecorder
{
    /// <summary>The longest a caller waits for its activity write (connection included).</summary>
    public static TimeSpan WriteTimeout { get; } = TimeSpan.FromSeconds(2);

    public async Task RecordAsync(ActivityKind kind, CancellationToken cancellationToken = default)
    {
        if (tenants.Current is not { } tenant || actingUser.UserId is not { Length: > 0 } userId
            || throttle.IsPaused
            || !throttle.TryEnter(tenant.TenantId, userId, kind, out var entry))
        {
            return;
        }

        var kindValue = kind == ActivityKind.Vendor ? "vendor" : "staff";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(WriteTimeout);
        try
        {
            await using var db = await contexts.CreateDbContextAsync(timeout.Token);
            await db.Database.ExecuteSqlAsync(
                $"insert into identity.user_activity (tenant_id, user_id, kind) values ({tenant.TenantId}, {userId}, {kindValue}) on conflict do nothing",
                timeout.Token);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // An error, or this write's own timeout: not written, and writes pause for a while.
            throttle.Forget(entry);
            if (throttle.Pause())
            {
                WriteFailed(logger, ex.GetType().Name, (int)ActivityThrottle.RetryAfter.TotalSeconds);
            }
        }
        catch (Exception)
        {
            // Only when the caller's own token was cancelled (the request went away first): rethrown for the caller; its
            // activity is recorded by its next sign of life, and the database is not blamed for it.
            throttle.Forget(entry);
            throw;
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "User activity could not be recorded ({ErrorType}); activity is not written for {PauseSeconds} seconds.")]
    private static partial void WriteFailed(ILogger logger, string errorType, int pauseSeconds);
}
