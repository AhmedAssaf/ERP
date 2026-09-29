using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Modules.Audit.Contracts;
using Platform.Modules.Identity;
using Platform.Shared.Tenancy;

namespace Platform.UnitTests.Identity;

/// <summary>
/// W-21 review: the end of a session is audited even when the request or circuit that found the removal is gone by the
/// time the check completes (its scope disposed), and even when the audit insert fails: the entry is written from a scope
/// of its own for the tenant of the session, or queued and written by the next retry, never dropped.
/// </summary>
public sealed class RevocationAuditLogTests
{
    private static readonly TenantContext Acme = new(
        Guid.Parse("0199a000-0000-7000-8000-00000000ac3e"), "acme", "acme", "ar-SA", new TenantBranding("Acme", "#0F766E", null));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_revocation_is_audited_for_its_tenant_after_the_scope_that_found_it_is_disposed()
    {
        var (provider, recorded) = Services(failFirst: 0);
        var log = new RevocationAuditLog(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<RevocationAuditLog>.Instance);
        RevocationAuditWriter writer;
        await using (var scope = provider.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<TenantAccessor>().Set(Acme);
            writer = new RevocationAuditWriter(scope.ServiceProvider.GetRequiredService<ITenantAccessor>(), log);
        }

        await writer.WriteAsync(Entry(), Ct);

        recorded.ShouldHaveSingleItem().ShouldBe((Acme.TenantId, "identity.session_revoked"));
        log.PendingCount.ShouldBe(0);
    }

    [Fact]
    public async Task A_revocation_audit_that_fails_is_queued_and_written_by_the_next_retry()
    {
        var (provider, recorded) = Services(failFirst: 1);
        var log = new RevocationAuditLog(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<RevocationAuditLog>.Instance);
        var accessor = new TenantAccessor();
        accessor.Set(Acme);

        await new RevocationAuditWriter(accessor, log).WriteAsync(Entry(), Ct);

        recorded.ShouldBeEmpty();
        log.PendingCount.ShouldBe(1);

        await log.RetryPendingAsync(Ct);

        recorded.ShouldHaveSingleItem().ShouldBe((Acme.TenantId, "identity.session_revoked"));
        log.PendingCount.ShouldBe(0);
    }

    private static AuditEntry Entry() =>
        new("u1", "identity.session_revoked", "user", "u1", new Dictionary<string, string?> { ["reason"] = "removed_from_organization" });

    private static (ServiceProvider Provider, List<(Guid Tenant, string Action)> Recorded) Services(int failFirst)
    {
        var recorded = new List<(Guid, string)>();
        var failures = new StrongBox(failFirst);
        var services = new ServiceCollection();
        services.AddScoped<TenantAccessor>();
        services.AddScoped<ITenantAccessor>(sp => sp.GetRequiredService<TenantAccessor>());
        services.AddScoped<IAuditWriter>(sp => new RecordingAudit(sp.GetRequiredService<ITenantAccessor>(), recorded, failures));
        return (services.BuildServiceProvider(validateScopes: true), recorded);
    }

    private sealed class StrongBox(int value)
    {
        public int Value { get; set; } = value;
    }

    private sealed class RecordingAudit(ITenantAccessor tenants, List<(Guid, string)> recorded, StrongBox failures) : IAuditWriter
    {
        public Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default)
        {
            if (failures.Value > 0)
            {
                failures.Value--;
                throw new TimeoutException("The audit log did not answer.");
            }

            lock (recorded)
            {
                recorded.Add((tenants.Current!.TenantId, entry.Action));
            }

            return Task.CompletedTask;
        }
    }
}
