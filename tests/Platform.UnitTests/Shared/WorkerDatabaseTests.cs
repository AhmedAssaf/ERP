using Microsoft.Extensions.Configuration;
using Platform.Shared.Data;

namespace Platform.UnitTests.Shared;

/// <summary>
/// W-36 (ADR-0012 addendum 2026-10-03): the worker connects only as its own role, <c>erp_worker</c>, from
/// <c>ConnectionStrings:Worker</c>, and the web host never connects as that role, so the worker-only rights stay out of
/// the request path.
/// </summary>
public sealed class WorkerDatabaseTests
{
    private const string WorkerConnection = "Host=localhost;Database=platform;Username=erp_worker;Password=x";

    [Fact]
    public void The_worker_connection_string_is_required()
    {
        var refused = Should.Throw<InvalidOperationException>(() => WorkerDatabase.ConnectionString(Configuration(null)));

        refused.Message.ShouldContain("ConnectionStrings:Worker");
        refused.Message.ShouldContain("erp_worker");
    }

    [Theory]
    [InlineData("erp_app")]
    [InlineData("erp")]
    [InlineData("erp_key_ring")]
    [InlineData("ERP_WORKER")]
    public void The_worker_refuses_any_role_but_its_own(string role)
    {
        var refused = Should.Throw<InvalidOperationException>(() =>
            WorkerDatabase.ConnectionString(Configuration($"Host=localhost;Database=platform;Username={role};Password=secret-value")));

        refused.Message.ShouldContain(WorkerDatabase.RoleName);
        refused.Message.ShouldNotContain("secret-value");
    }

    [Fact]
    public void The_worker_takes_its_own_role()
    {
        WorkerDatabase.ConnectionString(Configuration(WorkerConnection)).ShouldBe(WorkerConnection);
    }

    [Fact]
    public void The_web_host_refuses_the_worker_role_as_its_application_connection()
    {
        var refused = Should.Throw<InvalidOperationException>(() => WorkerDatabase.RefuseWorkerRole(WorkerConnection, "Platform"));

        refused.Message.ShouldContain("ConnectionStrings:Platform");
        refused.Message.ShouldNotContain("Password");
    }

    [Fact]
    public void The_web_host_accepts_the_application_role()
    {
        Should.NotThrow(() => WorkerDatabase.RefuseWorkerRole("Host=localhost;Username=erp_app;Password=x", "Platform"));
    }

    private static IConfiguration Configuration(string? worker) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(worker is null ? [] : [new KeyValuePair<string, string?>("ConnectionStrings:Worker", worker)])
            .Build();
}
