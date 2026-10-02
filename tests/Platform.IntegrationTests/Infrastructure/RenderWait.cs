namespace Platform.IntegrationTests.Infrastructure;

/// <summary>
/// How long a bUnit page test waits for a render that follows asynchronous work: a page's load against the test
/// database, or an event handler's reload. A page loads in several awaited steps and any of them may finish
/// synchronously (a reply already in the socket buffer, as on a CI runner with native Docker), so the render after the
/// first step can already show what a readiness check looks for while a later step's rows are still missing. A test
/// therefore waits for the element it is about to use (<c>WaitForElement</c>, <c>WaitForAssertion</c>) instead of
/// finding it right after the render, and gives the wait this timeout rather than bUnit's one-second default, which a
/// slower hosted runner can exceed.
/// </summary>
internal static class RenderWait
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
}
