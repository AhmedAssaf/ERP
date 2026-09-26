using System.Net;
using System.Text.RegularExpressions;

namespace Platform.IntegrationTests.Infrastructure;

/// <summary>Where Keycloak sent the browser back to the application: a redirect (GET) or a form post with its fields.</summary>
internal sealed record AppCallback(Uri Url, HttpMethod Method, IReadOnlyDictionary<string, string> Form);

/// <summary>The outcome of one browser step: another Keycloak page, or the hand-back to the application.</summary>
internal sealed record KeycloakStep(string? Page, AppCallback? Callback);

/// <summary>
/// Plays the browser's part in a Keycloak login (plan task 6): follows Keycloak's own redirects, fills the forms it
/// serves, and stops when Keycloak hands the browser back to the application, either by redirect or by the
/// auto-submitting form of <c>response_mode=form_post</c> (the ASP.NET Core handler's default). Keycloak's login theme
/// identifies its forms by id (<c>kc-form-login</c>, <c>kc-otp-login-form</c>). Keycloak marks its session cookies
/// Secure; browsers send those to a loopback address over plain HTTP (a secure context), but
/// <see cref="CookieContainer"/> does not, so the helper keeps its own jar for the one Keycloak it talks to.
/// </summary>
internal sealed partial class KeycloakBrowser : IDisposable
{
    private readonly HttpClient _http;
    private readonly Uri _keycloak;
    private readonly Dictionary<string, string> _cookies = new(StringComparer.Ordinal);

    public KeycloakBrowser(string keycloakBaseAddress)
    {
        _keycloak = new Uri(keycloakBaseAddress);
        _http = new HttpClient(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false });
    }

    /// <summary>Opens the authorization URL and returns the login page.</summary>
    public async Task<string> OpenAsync(Uri authorizationUrl, CancellationToken cancellationToken)
    {
        var step = await FollowAsync(await SendAsync(HttpMethod.Get, authorizationUrl, null, cancellationToken), cancellationToken);
        return step.Page ?? throw new InvalidOperationException($"Keycloak sent the browser to {step.Callback?.Url} instead of the login page.");
    }

    /// <summary>Posts the named form on <paramref name="page"/> and follows Keycloak until the next page or the hand-back.</summary>
    public async Task<KeycloakStep> SubmitAsync(
        string page, string formId, IReadOnlyDictionary<string, string> fields, CancellationToken cancellationToken)
    {
        var tag = FormTag(page, formId) ?? throw new InvalidOperationException($"The page has no form '{formId}'.");
        using var content = new FormUrlEncodedContent(fields);
        return await FollowAsync(await SendAsync(HttpMethod.Post, FormAction(tag), content, cancellationToken), cancellationToken);
    }

    /// <summary>Opens any Keycloak URL (a logout, say) and follows it to the next page or the hand-back.</summary>
    public async Task<KeycloakStep> NavigateAsync(Uri url, CancellationToken cancellationToken) =>
        await FollowAsync(await SendAsync(HttpMethod.Get, url, null, cancellationToken), cancellationToken);

    /// <summary>
    /// Posts the page's first form with its hidden fields and its first named submit button, as a click on that button
    /// would (Keycloak's logout confirmation has no form id).
    /// </summary>
    public async Task<KeycloakStep> SubmitFirstFormAsync(string page, CancellationToken cancellationToken)
    {
        var tag = FormTags().Match(page) is { Success: true } match ? match.Value : throw new InvalidOperationException("The page has no form.");
        var fields = HiddenInputs().Matches(page)
            .Select(m => (Name: Attribute(m.Value, "name"), Value: Attribute(m.Value, "value")))
            .Where(f => f.Name is not null)
            .ToDictionary(f => f.Name!, f => f.Value ?? string.Empty, StringComparer.Ordinal);
        if (SubmitInputs().Match(page) is { Success: true } submit && Attribute(submit.Value, "name") is { } name)
        {
            fields[name] = Attribute(submit.Value, "value") ?? string.Empty;
        }

        using var content = new FormUrlEncodedContent(fields);
        return await FollowAsync(await SendAsync(HttpMethod.Post, FormAction(tag), content, cancellationToken), cancellationToken);
    }

    public static bool HasForm(string page, string formId) => FormTag(page, formId) is not null;

    /// <summary>The page's template and message, for an assertion that failed on an unexpected Keycloak page.</summary>
    public static string Feedback(string page)
    {
        var template = Template().Match(page);
        var message = FeedbackText().Match(page);
        return $"Keycloak showed {(template.Success ? template.Groups[1].Value : "an unknown page")}: "
            + (message.Success ? WebUtility.HtmlDecode(message.Groups[1].Value.Trim()) : "no message");
    }

    public void Dispose() => _http.Dispose();

    private async Task<KeycloakStep> FollowAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var current = response;
        try
        {
            while (current.StatusCode is HttpStatusCode.Found or HttpStatusCode.SeeOther)
            {
                var location = current.Headers.Location ?? throw new InvalidOperationException("A redirect without a location.");
                location = location.IsAbsoluteUri ? location : new Uri(current.RequestMessage!.RequestUri!, location);
                if (location.Authority != _keycloak.Authority)
                {
                    return new KeycloakStep(null, new AppCallback(location, HttpMethod.Get, new Dictionary<string, string>()));
                }

                current.Dispose();
                current = await SendAsync(HttpMethod.Get, location, null, cancellationToken);
            }

            current.EnsureSuccessStatusCode();
            var page = await current.Content.ReadAsStringAsync(cancellationToken);
            return FormPostCallback(page) is { } callback ? new KeycloakStep(null, callback) : new KeycloakStep(page, null);
        }
        finally
        {
            current.Dispose();
        }
    }

    // response_mode=form_post: a page whose only purpose is a form posting to the application, not to Keycloak.
    private AppCallback? FormPostCallback(string page)
    {
        foreach (var tag in FormTags().Matches(page).Select(m => m.Value))
        {
            if (!ActionAttribute().IsMatch(tag))
            {
                continue;
            }

            var action = FormAction(tag);
            if (action.Authority == _keycloak.Authority)
            {
                continue;
            }

            var fields = HiddenInputs().Matches(page)
                .Select(m => (Name: Attribute(m.Value, "name"), Value: Attribute(m.Value, "value")))
                .Where(f => f.Name is not null)
                .ToDictionary(f => f.Name!, f => f.Value ?? string.Empty, StringComparer.Ordinal);
            return new AppCallback(action, HttpMethod.Post, fields);
        }

        return null;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, Uri url, HttpContent? content, CancellationToken cancellationToken)
    {
        if (url.Authority != _keycloak.Authority)
        {
            throw new InvalidOperationException($"The browser only talks to Keycloak, not to {url.Authority}.");
        }

        using var request = new HttpRequestMessage(method, url) { Content = content };
        if (_cookies.Count > 0)
        {
            request.Headers.Add("Cookie", string.Join("; ", _cookies.Select(c => $"{c.Key}={c.Value}")));
        }

        var response = await _http.SendAsync(request, cancellationToken);
        if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
        {
            foreach (var setCookie in setCookies)
            {
                var pair = setCookie.Split(';', 2)[0];
                var separator = pair.IndexOf('=', StringComparison.Ordinal);
                var name = pair[..separator].Trim();
                var value = pair[(separator + 1)..].Trim();
                if (value.Length == 0 || setCookie.Contains("Max-Age=0", StringComparison.OrdinalIgnoreCase))
                {
                    _cookies.Remove(name);
                }
                else
                {
                    _cookies[name] = value;
                }
            }
        }

        return response;
    }

    // Login forms carry an absolute action; the logout confirmation's is relative to Keycloak's root.
    private Uri FormAction(string formTag)
    {
        var action = ActionAttribute().Match(formTag);
        return action.Success
            ? new Uri(_keycloak, WebUtility.HtmlDecode(action.Groups[1].Value))
            : throw new InvalidOperationException("The form has no action.");
    }

    private static string? FormTag(string page, string formId) =>
        FormTags().Matches(page).Select(m => m.Value).FirstOrDefault(tag => tag.Contains($"id=\"{formId}\"", StringComparison.Ordinal));

    private static string? Attribute(string tag, string name)
    {
        var match = Regex.Match(tag, $"\\b{name}=\"([^\"]*)\"", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        return match.Success ? WebUtility.HtmlDecode(match.Groups[1].Value) : null;
    }

    [GeneratedRegex("<!-- template: ([^ ]+) -->")]
    private static partial Regex Template();

    [GeneratedRegex("kc-feedback-text\">([^<]*)<")]
    private static partial Regex FeedbackText();

    [GeneratedRegex("<form\\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex FormTags();

    [GeneratedRegex("<input\\b[^>]*type=\"hidden\"[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex HiddenInputs();

    [GeneratedRegex("<(?:input|button)\\b[^>]*type=\"submit\"[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex SubmitInputs();

    [GeneratedRegex("\\baction=\"([^\"]*)\"", RegexOptions.IgnoreCase)]
    private static partial Regex ActionAttribute();
}
