namespace Platform.Web.Telemetry;

/// <summary>
/// <c>GET /dev/throw</c>, Development only (W-10, plan task 10's checks): fails with a fixed message, so one error can be
/// followed from the response's correlation id to its log record and span. Not mapped elsewhere, and outside Development the
/// host's <c>/dev</c> rule answers 404 before routing anyway. Anonymous, so the check needs no sign-in.
/// </summary>
internal static class DeliberateFailureEndpoint
{
    public const string Path = "/dev/throw";
    public const string Message = "Deliberate failure for the W-10 checks.";

    public static IEndpointRouteBuilder MapDeliberateFailure(this IEndpointRouteBuilder app, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (environment.IsDevelopment())
        {
            app.MapGet(Path, IResult () => throw new InvalidOperationException(Message)).AllowAnonymous();
        }

        return app;
    }
}
