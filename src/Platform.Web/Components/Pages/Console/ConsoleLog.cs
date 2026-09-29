namespace Platform.Web.Components.Pages.Console;

/// <summary>Log messages of the console pages (a source-generated logger cannot live in a .razor file).</summary>
internal static partial class ConsoleLog
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Re-running job {JobId} from the platform console failed.")]
    public static partial void RerunFailed(ILogger logger, string jobId, Exception exception);

    /// <summary>A CR ownership action of the console failed (W-33); the exception type only, never its message (N-10).</summary>
    [LoggerMessage(Level = LogLevel.Error, Message = "The CR ownership action {Action} from the platform console failed ({ErrorType}).")]
    public static partial void OwnershipActionFailed(ILogger logger, string action, string errorType);
}
