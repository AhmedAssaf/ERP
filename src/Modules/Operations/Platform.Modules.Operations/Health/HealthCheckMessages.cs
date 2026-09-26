namespace Platform.Modules.Operations.Health;

/// <summary>
/// N-10: a failure message is a fixed sentence naming the component, plus the exception's type name when there was
/// one. Never <c>ex.Message</c>: for a connection or authentication failure it can echo a connection string,
/// hostname, or credential.
/// </summary>
internal static class HealthCheckMessages
{
    public static string CouldNotReach(string component) => $"Could not reach {component}.";

    public static string WithExceptionType(Exception exception, string sentence) =>
        $"{exception.GetType().Name}: {sentence}";
}
