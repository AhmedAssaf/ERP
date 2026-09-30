using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Platform.Modules.Identity.Contracts;
using Platform.Modules.Identity.Members;
using Platform.Shared.Tenancy;

namespace Platform.Modules.Identity.Activity;

/// <summary>
/// Writes the scope's hourly activity bucket into <c>identity.user_activity</c> (spec 6.4) through the scope's own
/// connection, so row-level security checks that the row is the acting user's own, for the host tenant, in the kind the
/// vendor context allows; the database sets the hour. At most once an hour (<see cref="ActivityThrottle"/>). A failure is
/// logged with its type only and swallowed, never failing the request or circuit event; it is not remembered as written.
/// </summary>
internal sealed partial class UserActivityRecorder(
    ITenantAccessor tenants,
    IActingUserAccessor actingUser,
    ActivityThrottle throttle,
    IDbContextFactory<MembersDbContext> contexts,
    ILogger<UserActivityRecorder> logger) : IUserActivityRecorder
{
    public async Task RecordAsync(ActivityKind kind, CancellationToken cancellationToken = default)
    {
        if (tenants.Current is not { } tenant || actingUser.UserId is not { Length: > 0 } userId
            || !throttle.TryEnter(tenant.TenantId, userId, kind, out var entry))
        {
            return;
        }

        var kindValue = kind == ActivityKind.Vendor ? "vendor" : "staff";
        try
        {
            await using var db = await contexts.CreateDbContextAsync(cancellationToken);
            await db.Database.ExecuteSqlAsync(
                $"insert into identity.user_activity (tenant_id, user_id, kind) values ({tenant.TenantId}, {userId}, {kindValue}) on conflict do nothing",
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            throttle.Forget(entry);
            WriteFailed(logger, ex.GetType().Name);
        }
        catch (OperationCanceledException)
        {
            // The request or circuit went away first; its activity is recorded by its next sign of life.
            throttle.Forget(entry);
            throw;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "User activity could not be recorded ({ErrorType}); the next activity tries again.")]
    private static partial void WriteFailed(ILogger logger, string errorType);
}
