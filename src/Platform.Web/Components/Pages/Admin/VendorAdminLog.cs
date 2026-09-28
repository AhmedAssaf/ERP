namespace Platform.Web.Components.Pages.Admin;

/// <summary>
/// Log messages of the staff's vendor pages (a source-generated logger cannot live in a .razor file). The exception type
/// only, never the exception or its message (N-10).
/// </summary>
internal static partial class VendorAdminLog
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Approving a vendor from the vendor card failed ({ErrorType}).")]
    public static partial void ApproveFailed(ILogger logger, string errorType);
}
