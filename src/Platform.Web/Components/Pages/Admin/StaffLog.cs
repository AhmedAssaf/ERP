namespace Platform.Web.Components.Pages.Admin;

/// <summary>
/// Log messages of the staff page (a source-generated logger cannot live in a .razor file). The exception type only,
/// never the exception or its message (N-10): an HttpRequestException from the Keycloak Admin API can carry its URL.
/// </summary>
internal static partial class StaffLog
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Inviting a staff member from the staff page failed ({ErrorType}).")]
    public static partial void InviteFailed(ILogger logger, string errorType);

    [LoggerMessage(Level = LogLevel.Error, Message = "Changing a staff member's roles from the staff page failed ({ErrorType}).")]
    public static partial void SetRolesFailed(ILogger logger, string errorType);

    [LoggerMessage(Level = LogLevel.Error, Message = "Resending a staff invitation from the staff page failed ({ErrorType}).")]
    public static partial void ResendFailed(ILogger logger, string errorType);
}
