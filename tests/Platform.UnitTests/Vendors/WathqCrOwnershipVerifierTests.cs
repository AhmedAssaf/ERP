using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Platform.Modules.Vendors;
using Platform.Modules.Vendors.Contracts;
using Platform.Modules.Vendors.Ownership;

namespace Platform.UnitTests.Vendors;

/// <summary>
/// W-33: the Wathq adapter reads only what Wathq's published commercial registration specification documents (owners and
/// managers by name, type and position), accepts the owners answer as a list or, as the specification's example shows it,
/// a single item, never returns identity numbers, never calls Wathq when it is not configured, and never logs the key.
/// </summary>
public sealed class WathqCrOwnershipVerifierTests
{
    private const string Key = "unit-test-wathq-key";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_single_owner_item_as_in_the_specifications_example_is_read_and_identity_numbers_are_dropped()
    {
        var handler = new Handler(request => request.RequestUri!.AbsolutePath.Contains("/owners/", StringComparison.Ordinal)
            ? Json("""{"name":"Sole Owner","typeId":11,"typeName":"Owner","identity":{"id":"1101552388","typeId":1,"typeName":"National ID"},"partnership":[{"id":8,"name":"Owner"}],"nationality":null}""")
            : Json("""[]"""));
        var logs = new Logs();
        var verifier = Verifier(handler, logs, "https://wathq.example/sandbox/commercial-registration/");

        var lookup = await verifier.LookupAsync("1010000001", Ct);

        lookup.Outcome.ShouldBe(CrLookupOutcome.Found);
        var owner = lookup.Owners.ShouldHaveSingleItem();
        (owner.Name, owner.Type, string.Join('|', owner.Positions)).ShouldBe(("Sole Owner", "Owner", "Owner"));
        lookup.Managers.ShouldBeEmpty();
        handler.Requests.Select(r => r.Path).Order(StringComparer.Ordinal).ShouldBe(
            ["/sandbox/commercial-registration/managers/1010000001", "/sandbox/commercial-registration/owners/1010000001"]);
        handler.Requests.ShouldAllBe(r => r.Key == Key);
        string.Join(' ', lookup.Owners.Select(o => o.ToString())).ShouldNotContain("1101552388");
    }

    [Fact]
    public async Task Without_an_address_or_a_key_nothing_is_called_and_the_outcome_is_not_configured()
    {
        foreach (var (baseUrl, key) in new[] { ((string?)null, (string?)Key), ("https://wathq.example/", null), ("not a url", Key), ("ftp://wathq.example/", Key) })
        {
            var handler = new Handler(_ => Json("[]"));
            var verifier = Verifier(handler, new Logs(), baseUrl, key);

            (await verifier.LookupAsync("1010000001", Ct)).Outcome.ShouldBe(CrLookupOutcome.NotConfigured);
            handler.Requests.ShouldBeEmpty();
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, CrLookupOutcome.Unavailable)]
    [InlineData(HttpStatusCode.TooManyRequests, CrLookupOutcome.Unavailable)]
    [InlineData(HttpStatusCode.NotFound, CrLookupOutcome.NotFound)]
    public async Task An_error_answer_is_an_outcome_and_the_key_never_reaches_the_log(HttpStatusCode status, CrLookupOutcome expected)
    {
        var logs = new Logs();
        var verifier = Verifier(new Handler(_ => new HttpResponseMessage(status) { Content = new StringContent($"{{\"message\":\"bad key {Key}\"}}") }), logs);

        (await verifier.LookupAsync("1010000001", Ct)).Outcome.ShouldBe(expected);

        logs.Messages.ShouldAllBe(m => !m.Contains(Key, StringComparison.Ordinal));
        if (expected == CrLookupOutcome.Unavailable)
        {
            logs.Messages.ShouldContain(m => m.Contains(((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task An_item_without_a_name_is_not_the_documented_shape_and_the_manual_check_applies()
    {
        var verifier = Verifier(new Handler(_ => Json("""[{"typeName":"Owner"}]""")), new Logs());

        (await verifier.LookupAsync("1010000001", Ct)).Outcome.ShouldBe(CrLookupOutcome.Unavailable);
    }

    private static WathqCrOwnershipVerifier Verifier(Handler handler, Logs logs, string? baseUrl = "https://wathq.example/api", string? key = Key) =>
        new(new Clients(handler), Options.Create(new WathqOptions { BaseUrl = baseUrl, ApiKey = key, TimeoutSeconds = 5 }), logs);

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public ConcurrentQueue<(string Path, string? Key)> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue((request.RequestUri!.AbsolutePath, request.Headers.TryGetValues("apiKey", out var values) ? values.Single() : null));
            return Task.FromResult(answer(request));
        }
    }

    private sealed class Clients(Handler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Logs : ILogger<WathqCrOwnershipVerifier>
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Enqueue(formatter(state, exception) + (exception is null ? string.Empty : " " + exception));
    }
}
