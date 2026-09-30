namespace Platform.Modules.Vendors;

/// <summary>
/// The Wathq commercial registration API (section <c>Wathq</c>, W-33), used only while the platform's ownership check
/// method is Wathq. Optional: without both settings Wathq is "not configured" and the officer checks by hand.
/// <list type="bullet">
/// <item><c>Wathq:BaseUrl</c>: the API's base address up to the resource paths. Wathq's published sandbox specification
/// (Swagger 2.0, "Wathq Commercial Registration API" 6.0.0) names host <c>api.wathq.sa</c> and base path
/// <c>/sandbox/commercial-registration</c>, so the sandbox is <c>https://api.wathq.sa/sandbox/commercial-registration</c>;
/// the production base comes with the subscription and is set here, never assumed.</item>
/// <item><c>Wathq:ApiKey</c>: the subscription key, sent in the <c>apiKey</c> header. A secret: user secrets or
/// <c>infra/compose/.env</c> locally, the cloud KMS in production, never the repository, never logged or shown (N-10).</item>
/// <item><c>Wathq:TimeoutSeconds</c>: how long one call may take, 10 by default.</item>
/// <item>The base address must be https (plain http only on a loopback host, for a test double): the web host refuses to
/// start otherwise. Redirects are never followed, so the key goes to the configured host only.</item>
/// </list>
/// </summary>
internal sealed class WathqOptions
{
    public const string Section = "Wathq";

    public string? BaseUrl { get; set; }

    public string? ApiKey { get; set; }

    public int TimeoutSeconds { get; set; } = 10;

    public const string BaseUrlProblem =
        "Setting 'Wathq:BaseUrl' must be an absolute https address (plain http only for a loopback test double); the API key is sent with every request.";

    /// <summary>A usable base address and a key.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ApiKey) && BaseUri is not null;

    /// <summary>
    /// The base address when it is absolute https, or plain http on a loopback host (a test double on this machine); null
    /// otherwise, so the key never travels unencrypted over a network (N-10).
    /// </summary>
    public Uri? BaseUri =>
        Uri.TryCreate(BaseUrl?.Trim(), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
            ? uri
            : null;

    /// <summary>Checked when the web host starts: no base address at all (Wathq not configured), or a usable one.</summary>
    public bool HasValidBaseUrl => string.IsNullOrWhiteSpace(BaseUrl) || BaseUri is not null;
}
