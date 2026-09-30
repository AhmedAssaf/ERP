using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Identity.Contracts;
using Platform.Modules.Vendors;
using Platform.Modules.Vendors.Contracts;
using static Platform.IntegrationTests.Security.CrAttack;

namespace Platform.IntegrationTests.Security;

/// <summary>
/// Pentest of W-33, N-10 and personal data: the Wathq API key, the national IDs and names Wathq answers with, the
/// registering person's name and email, and a claimant's email, name and statement never reach a log line (every level,
/// every category, message, structured values, scopes and exceptions), through a found lookup, each Wathq failure, the
/// identity provider failing, and a dispute raised and upheld with Keycloak failing. The key goes only to Wathq.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class CrOwnershipLogTests(DatabaseFixture db)
{
    [Fact]
    public async Task The_wathq_key_and_personal_data_never_reach_a_log_line()
    {
        var marker = Guid.NewGuid().ToString("N")[..10];
        var apiKey = $"wathq-secret-{Guid.NewGuid():N}";
        var nationalIds = new[] { "1101552388", "2202662499" };
        var ownerName = $"Owner Person {marker}";
        var managerName = $"Manager Person {marker}";
        var registrantEmail = $"registrant.{marker}@private.test";
        var claimantEmail = $"claimant.{marker}@private.test";
        var claimantName = $"Claimant Person {marker}";
        var statement = $"Our company, private reason {marker}.";
        var secrets = new[] { apiKey, ownerName, managerName, registrantEmail, $"Registrant{marker}", claimantEmail, claimantName, statement }
            .Concat(nationalIds).ToArray();

        var mode = "found";
        await using var wathq = await FakeHttpServer.StartAsync(async context =>
        {
            var path = context.Request.Path.Value!;
            switch (mode)
            {
                case "echo-401":
                    // An error answer that echoes the key back, as some gateways do.
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await context.Response.WriteAsync($$"""{"message":"invalid apiKey {{context.Request.Headers["apiKey"]}}"}""", context.RequestAborted);
                    return;
                case "bad-shape":
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync($$$"""[{"identity":{"id":"{{{nationalIds[0]}}}"}}]""", context.RequestAborted);
                    return;
            }

            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(path.Contains("/owners/", StringComparison.Ordinal)
                ? $$"""[{"name":"{{ownerName}}","typeName":"Partner","identity":{"id":"{{nationalIds[0]}}"},"partnership":[{"id":8,"name":"Partner"}]}]"""
                : $$"""[{"name":"{{managerName}}","typeName":"Manager","identity":{"id":"{{nationalIds[1]}}"},"positions":[{"id":3,"name":"General manager"}]}]""",
                context.RequestAborted);
        }, Ct);

        var logs = new CapturedLogs();
        var accounts = new FakeVendorAccounts();
        await OwnershipRows.SetMethodAsOwnerAsync(db.OwnerConnectionString, "wathq", Ct);
        try
        {
            await using var host = new ModuleHost(db.AppConnectionString, configure: services =>
            {
                services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
                services.Replace(ServiceDescriptor.Scoped<IVendorAccounts>(_ => accounts));
                services.Configure<WathqOptions>(options =>
                {
                    options.BaseUrl = wathq.BaseAddress;
                    options.ApiKey = apiKey;
                    options.TimeoutSeconds = 5;
                });
            });
            var officer = await StaffAsync(db, TestTenants.Acme, TenantRoles.ContractsOfficer);

            // A found lookup, then approval on it.
            var (found, registrant, _) = await VendorAsync(db, "Log Found Co");
            accounts.Profiles[registrant] = new VendorAccountProfile($"Registrant{marker}", "Person", registrantEmail, EmailVerified: true);
            await using (var scope = host.ScopeFor(TestTenants.Acme, actingUserId: officer))
            {
                var directory = scope.ServiceProvider.GetRequiredService<IVendorDirectory>();
                (await directory.GetOwnershipCheckAsync(found, Ct)).ShouldNotBeNull().Lookup.ShouldNotBeNull().Outcome.ShouldBe(CrLookupOutcome.Found);
                (await directory.ApproveAsync(found, officer, new OwnershipConfirmation(OfficerNote, true, CrLookupOutcome.Found), Ct)).IsSuccess.ShouldBeTrue();
            }

            // Every failure: the answer echoing the key, a body of the wrong shape, and the identity provider failing.
            foreach (var failure in new[] { "echo-401", "bad-shape" })
            {
                mode = failure;
                var (companyId, _, _) = await VendorAsync(db, $"Log {failure} Co");
                await using var scope = host.ScopeFor(TestTenants.Acme, actingUserId: officer);
                (await scope.ServiceProvider.GetRequiredService<IVendorDirectory>().GetOwnershipCheckAsync(companyId, Ct))
                    .ShouldNotBeNull().Lookup.ShouldNotBeNull().Outcome.ShouldBe(CrLookupOutcome.Unavailable);
            }

            accounts.FailProfile = true;
            var (unknown, _, _) = await VendorAsync(db, "Log Profile Co");
            await using (var scope = host.ScopeFor(TestTenants.Acme, actingUserId: officer))
            {
                await scope.ServiceProvider.GetRequiredService<IVendorDirectory>().GetOwnershipCheckAsync(unknown, Ct);
            }

            // A dispute raised and upheld while Keycloak refuses the claimant's role.
            var (disputed, _, crNumber) = await VendorAsync(db, "Log Dispute Co");
            var claimant = Guid.NewGuid().ToString();
            var admin = $"platform-admin-{Guid.NewGuid():N}";
            accounts.OnGrantRole = _ => throw new IdentityProviderException($"Keycloak refused {claimantEmail}.", new HttpRequestException("forced"));
            Guid disputeId;
            await using (var scope = host.ScopeFor(TestTenants.Acme, actingUserId: claimant))
            {
                var raised = await scope.ServiceProvider.GetRequiredService<ICrDisputes>().RaiseAsync(
                    new CrDisputeRequest(crNumber, statement, VendorPrivacyNotice.CurrentVersion, VendorPrivacyNotice.English), claimantEmail, claimantName, Ct);
                raised.IsSuccess.ShouldBeTrue(raised.Error?.Message);
                disputeId = raised.Value;
            }

            await using (var scope = host.PlatformScope(admin))
            {
                var upheld = await scope.ServiceProvider.GetRequiredService<ICrOwnershipAdministration>().UpholdAsync(disputeId, "Checked.", admin, Ct);
                upheld.IsSuccess.ShouldBeTrue(upheld.Error?.Message);
                upheld.Value.IdentityProviderUpdated.ShouldBeFalse();
            }

            (await OwnershipRows.VendorUsersAsync(db.OwnerConnectionString, disputed, Ct)).ShouldBe([(claimant, "vendor-admin")]);
        }
        finally
        {
            await OwnershipRows.SetMethodAsOwnerAsync(db.OwnerConnectionString, "manual", Ct);
        }

        logs.Lines.ShouldNotBeEmpty("the capture saw the flows");
        logs.Lines.ShouldContain(l => l.Contains("WathqCrOwnershipVerifier", StringComparison.Ordinal), "the Wathq failures were logged");
        foreach (var secret in secrets)
        {
            var leaks = logs.Lines.Where(l => l.Contains(secret, StringComparison.Ordinal)).ToList();
            leaks.ShouldBeEmpty($"a log line carries a value it must not ({secret.Length} characters): {string.Join(" || ", leaks.Select(l => l.Replace(secret, "<redacted>", StringComparison.Ordinal)))}");
        }
    }

    [Fact]
    public async Task The_wathq_key_goes_only_to_the_configured_wathq_host_even_on_a_redirect()
    {
        var apiKey = $"wathq-secret-{Guid.NewGuid():N}";
        var elsewhere = new ConcurrentQueue<string>();
        await using var other = await FakeHttpServer.StartAsync(async context =>
        {
            elsewhere.Enqueue(context.Request.Headers["apiKey"].ToString());
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("""[{"name":"Planted Owner","typeName":"Partner"}]""", context.RequestAborted);
        }, Ct);
        await using var wathq = await FakeHttpServer.StartAsync(context =>
        {
            context.Response.StatusCode = StatusCodes.Status302Found;
            context.Response.Headers.Location = other.BaseAddress.Replace("127.0.0.1", "localhost", StringComparison.Ordinal) + context.Request.Path.Value!.TrimStart('/');
            return Task.CompletedTask;
        }, Ct);

        var (companyId, _, _) = await VendorAsync(db, "Redirected Key Co");
        var officer = await StaffAsync(db, TestTenants.Acme, TenantRoles.ContractsOfficer);
        await OwnershipRows.SetMethodAsOwnerAsync(db.OwnerConnectionString, "wathq", Ct);
        try
        {
            await using var host = new ModuleHost(db.AppConnectionString, configure: services =>
            {
                services.Replace(ServiceDescriptor.Scoped<IVendorAccounts>(_ => new FakeVendorAccounts()));
                services.Configure<WathqOptions>(options =>
                {
                    options.BaseUrl = wathq.BaseAddress;
                    options.ApiKey = apiKey;
                    options.TimeoutSeconds = 5;
                });
            });
            await using var scope = host.ScopeFor(TestTenants.Acme, actingUserId: officer);
            await scope.ServiceProvider.GetRequiredService<IVendorDirectory>().GetOwnershipCheckAsync(companyId, Ct);
        }
        finally
        {
            await OwnershipRows.SetMethodAsOwnerAsync(db.OwnerConnectionString, "manual", Ct);
        }

        // The count only: the key itself is never printed, not even a test's (N-10).
        elsewhere.Count(k => string.Equals(k, apiKey, StringComparison.Ordinal)).ShouldBe(0, "the Wathq key followed a redirect to another host");
    }

    /// <summary>Every log entry as one line: category, level, message, structured values, scopes and exception.</summary>
    private sealed class CapturedLogs : ILoggerProvider
    {
        public ConcurrentQueue<string> Lines { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, Lines);

        public void Dispose()
        {
        }

        private sealed class Logger(string category, ConcurrentQueue<string> lines) : ILogger
        {
            private readonly AsyncLocal<List<string>?> _scopes = new();

            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
            {
                var scopes = _scopes.Value ??= [];
                scopes.Add(Describe(state));
                return new Scope(() => scopes.Remove(Describe(state)));
            }

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                ArgumentNullException.ThrowIfNull(formatter);
                lines.Enqueue(string.Join(" | ", category, logLevel, formatter(state, exception), Describe(state),
                    string.Join(";", _scopes.Value ?? []), exception?.ToString() ?? string.Empty));
            }

            private static string Describe(object? state) => state is IEnumerable<KeyValuePair<string, object?>> values
                ? string.Join(";", values.Select(v => $"{v.Key}={v.Value}"))
                : state?.ToString() ?? string.Empty;
        }

        private sealed class Scope(Action dispose) : IDisposable
        {
            public void Dispose() => dispose();
        }
    }
}
