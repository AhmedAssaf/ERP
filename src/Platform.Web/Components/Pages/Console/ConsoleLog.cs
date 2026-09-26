namespace Platform.Web.Components.Pages.Console;

/// <summary>Log messages of the console pages (a source-generated logger cannot live in a .razor file).</summary>
internal static partial class ConsoleLog
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Re-running job {JobId} from the platform console failed.")]
    public static partial void RerunFailed(ILogger logger, string jobId, Exception exception);
}
