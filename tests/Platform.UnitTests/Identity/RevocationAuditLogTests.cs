using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

    [Fact]
    public async Task A_failed_entry_is_one_error_then_debug_lines_with_one_warning_per_retry_cycle()
    {
        // Review: an outage must not log an error per queued entry on every 30-second cycle.
        var (provider, recorded) = Services(failFirst: 3);
        var logger = new RecordingLogger<RevocationAuditLog>();
        var log = new RevocationAuditLog(provider.GetRequiredService<IServiceScopeFactory>(), logger);
        var accessor = new TenantAccessor();
        accessor.Set(Acme);

        await new RevocationAuditWriter(accessor, log).WriteAsync(Entry(), Ct);
        await log.RetryPendingAsync(Ct);
        await log.RetryPendingAsync(Ct);
        await log.RetryPendingAsync(Ct);

        recorded.ShouldHaveSingleItem();
        logger.At(LogLevel.Error).ShouldHaveSingleItem().ShouldContain("u1");
        logger.At(LogLevel.Debug).Count.ShouldBe(2);
        logger.At(LogLevel.Warning).ShouldBe(["1 session revocation audits are still queued after a retry.", "1 session revocation audits are still queued after a retry."]);
    }

    [Fact]
    public async Task Stopping_the_host_flushes_the_queue_once_more()
    {
        var (provider, recorded) = Services(failFirst: 1);
        var log = new RevocationAuditLog(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<RevocationAuditLog>.Instance);
        var accessor = new TenantAccessor();
        accessor.Set(Acme);
        await new RevocationAuditWriter(accessor, log).WriteAsync(Entry(), Ct);
        var logger = new RecordingLogger<RevocationAuditRetry>();
        using var retry = new RevocationAuditRetry(log, TimeProvider.System, logger);

        await retry.StopAsync(Ct);

        recorded.ShouldHaveSingleItem();
        log.PendingCount.ShouldBe(0);
        logger.At(LogLevel.Critical).ShouldBeEmpty();
    }

    [Fact]
    public async Task Entries_the_last_flush_cannot_write_are_counted_in_a_critical_line()
    {
        var (provider, recorded) = Services(failFirst: int.MaxValue);
        var log = new RevocationAuditLog(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<RevocationAuditLog>.Instance);
        var accessor = new TenantAccessor();
        accessor.Set(Acme);
        await new RevocationAuditWriter(accessor, log).WriteAsync(Entry(), Ct);
        await new RevocationAuditWriter(accessor, log).WriteAsync(Entry(), Ct);
        var logger = new RecordingLogger<RevocationAuditRetry>();
        using var retry = new RevocationAuditRetry(log, TimeProvider.System, logger);

        await retry.StopAsync(Ct);

        recorded.ShouldBeEmpty();
        logger.At(LogLevel.Critical).ShouldHaveSingleItem().ShouldStartWith("2 session revocation audits were not written before the host stopped");
        RevocationAuditRetry.FlushTimeout.ShouldBeLessThanOrEqualTo(TimeSpan.FromSeconds(5));
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

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public List<string> At(LogLevel level)
        {
            lock (_entries)
            {
                return [.. _entries.Where(e => e.Level == level).Select(e => e.Message)];
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
            {
                _entries.Add((logLevel, formatter(state, exception)));
            }
        }
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
