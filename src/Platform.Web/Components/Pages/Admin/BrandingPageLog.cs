namespace Platform.Web.Components.Pages.Admin;

/// <summary>Log messages of the branding page (a source-generated logger cannot live in a .razor file).</summary>
internal static partial class BrandingPageLog
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Saving the portal name and colour from the branding page failed.")]
    public static partial void SaveFailed(ILogger logger, Exception exception);
}
