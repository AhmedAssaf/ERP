namespace Platform.Web.Components.Pages.Vendor;

/// <summary>
/// Log messages of the vendor pages (a source-generated logger cannot live in a .razor file). The exception type only,
/// never the exception or its message (N-10): an HttpRequestException from the Keycloak Admin API can carry its URL.
/// </summary>
internal static partial class VendorPageLog
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Registering a vendor company from the registration page failed ({ErrorType}).")]
    public static partial void RegisterFailed(ILogger logger, string errorType);

    [LoggerMessage(Level = LogLevel.Error, Message = "Joining a tenant from the join page failed ({ErrorType}).")]
    public static partial void JoinFailed(ILogger logger, string errorType);

    [LoggerMessage(Level = LogLevel.Error, Message = "Raising or listing a CR ownership dispute from the dispute page failed ({ErrorType}).")]
    public static partial void DisputeFailed(ILogger logger, string errorType);

    [LoggerMessage(Level = LogLevel.Error, Message = "A consent {Change} from the consent page failed ({ErrorType}).")]
    public static partial void ConsentChangeFailed(ILogger logger, string change, string errorType);
}
