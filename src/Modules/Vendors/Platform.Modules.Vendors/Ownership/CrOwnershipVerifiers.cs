using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Platform.Modules.Vendors.Contracts;

namespace Platform.Modules.Vendors.Ownership;

/// <summary>
/// The port behind the platform's CR ownership check method (W-33): what an officer is shown about a CR number before
/// confirming that the registering person owns the company. The officer always confirms; a verifier only pre-fills.
/// Never throws for the provider's failures: those are outcomes, and the manual check applies.
/// </summary>
internal interface ICrOwnershipVerifier
{
    CrOwnershipMethod Method { get; }

    Task<CrOwnershipLookup> LookupAsync(string crNumber, CancellationToken cancellationToken);
}

/// <summary>The manual method: nothing is looked up; the officer reads the CR certificate (F-12).</summary>
internal sealed class ManualCrOwnershipVerifier : ICrOwnershipVerifier
{
    public CrOwnershipMethod Method => CrOwnershipMethod.Manual;

    public Task<CrOwnershipLookup> LookupAsync(string crNumber, CancellationToken cancellationToken) =>
        Task.FromResult(CrOwnershipLookup.Without(CrLookupOutcome.Manual));
}

/// <summary>
/// The Wathq method, as far as Wathq's published commercial registration specification confirms it (sandbox Swagger 2.0,
/// "Wathq Commercial Registration API" 6.0.0, https://developer.wathq.sa): <c>GET {base}/owners/{id}</c> ("the owner of
/// establishment and a list of partners with their shares") and <c>GET {base}/managers/{id}</c> ("managers and board of
/// directors"), <c>id</c> the CR number or the unified national number, optional <c>language=ar|en</c>, the key in the
/// <c>apiKey</c> header. Each item has <c>name</c> and <c>typeName</c>, owners a <c>partnership</c> list and managers a
/// <c>positions</c> list, each of <c>{id, name}</c>. Items also carry <c>identity.id</c> (a national ID); it is never read.
/// The specification's owners example is a single object although its schema is an array, so both are accepted.
/// Not configured, a network failure, a timeout, a status other than 200 or 404, or a body that is not the documented
/// shape all end in an outcome that makes the officer check by hand; the key is never logged.
/// </summary>
internal sealed partial class WathqCrOwnershipVerifier(
    IHttpClientFactory clients, IOptions<WathqOptions> options, ILogger<WathqCrOwnershipVerifier> logger) : ICrOwnershipVerifier
{
    public const string HttpClientName = "Wathq";

    public CrOwnershipMethod Method => CrOwnershipMethod.Wathq;

    public async Task<CrOwnershipLookup> LookupAsync(string crNumber, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(crNumber);
        var settings = options.Value;
        if (!settings.IsConfigured)
        {
            NotConfigured(logger);
            return CrOwnershipLookup.Without(CrLookupOutcome.NotConfigured);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.TimeoutSeconds, 1, 60)));
        try
        {
            var owners = await ReadAsync(settings, "owners", "partnership", crNumber, timeout.Token);
            if (owners is null)
            {
                return CrOwnershipLookup.Without(CrLookupOutcome.NotFound);
            }

            // An establishment may have no managers; Wathq's 404 then means no list, not no record.
            var managers = await ReadAsync(settings, "managers", "positions", crNumber, timeout.Token) ?? [];
            return new CrOwnershipLookup(CrLookupOutcome.Found, owners, managers);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or WathqAnswerException
                                   || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            Failed(logger, ex is WathqAnswerException answer ? answer.Message : ex.GetType().Name);
            return CrOwnershipLookup.Without(CrLookupOutcome.Unavailable);
        }
    }

    /// <summary>The parties of one resource; null when Wathq answers 404.</summary>
    private async Task<IReadOnlyList<CrParty>?> ReadAsync(
        WathqOptions settings, string resource, string positionsProperty, string crNumber, CancellationToken cancellationToken)
    {
        var language = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar" ? "ar" : "en";
        var baseUri = settings.BaseUri!.ToString().TrimEnd('/');
        using var request = new HttpRequestMessage(
            HttpMethod.Get, new Uri($"{baseUri}/{resource}/{Uri.EscapeDataString(crNumber)}?language={language}", UriKind.Absolute));
        request.Headers.TryAddWithoutValidation("apiKey", settings.ApiKey);
        request.Headers.Accept.ParseAdd("application/json");

        var http = clients.CreateClient(HttpClientName);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new WathqAnswerException($"{resource} answered {(int)response.StatusCode}");
        }

        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
        var items = document.RootElement.ValueKind switch
        {
            JsonValueKind.Array => document.RootElement.EnumerateArray().ToList(),
            JsonValueKind.Object => [document.RootElement],
            _ => throw new WathqAnswerException($"{resource} answered neither a list nor an item"),
        };

        var parties = new List<CrParty>(items.Count);
        foreach (var item in items)
        {
            if (item.ValueKind != JsonValueKind.Object || Text(item, "name") is not { } name)
            {
                throw new WathqAnswerException($"{resource} answered an item without a name");
            }

            var positions = item.TryGetProperty(positionsProperty, out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Select(p => p.ValueKind == JsonValueKind.Object ? Text(p, "name") : null).OfType<string>().ToList()
                : [];
            parties.Add(new CrParty(name, Text(item, "typeName"), positions));
        }

        return parties;
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;

    [LoggerMessage(Level = LogLevel.Warning, Message = "The CR ownership check method is Wathq, but Wathq:BaseUrl or Wathq:ApiKey is not set; the officer checks by hand.")]
    private static partial void NotConfigured(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Wathq did not answer the CR ownership lookup ({Reason}); the officer checks by hand.")]
    private static partial void Failed(ILogger logger, string reason);

    /// <summary>Wathq answered, but not with what its specification documents. The message names the resource and status only.</summary>
    private sealed class WathqAnswerException(string message) : Exception(message);
}
