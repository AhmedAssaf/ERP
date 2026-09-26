namespace Platform.Web.Components.Pages.Admin;

/// <summary>Log messages of the staff page (a source-generated logger cannot live in a .razor file).</summary>
internal static partial class StaffLog
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Inviting a staff member from the staff page failed.")]
    public static partial void InviteFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Changing a staff member's roles from the staff page failed.")]
    public static partial void SetRolesFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Resending a staff invitation from the staff page failed.")]
    public static partial void ResendFailed(ILogger logger, Exception exception);
}
