using Hangfire;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Platform.IntegrationTests.Infrastructure;
using Platform.Migrator;
using Platform.Shared;
using Platform.Shared.Jobs;

namespace Platform.IntegrationTests.Security;

/// <summary>
/// W-36 (ADR-0012 addendum 2026-10-03, pentest I-2, PT-W10-01): the worker connects as its own role, <c>erp_worker</c>,
/// so the worker-only functions and the worker's ops writes are rights of that role and no longer a context rule that a
/// platform console session (no tenant, no vendor, with or without a user) also met. The application role keeps what the
/// web host and the console need: reading the board, the incidents and the usage counts, and enqueueing jobs. Hangfire's
/// tables belong to the migration owner, not to a runtime role.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class WorkerRoleTests(DatabaseFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Every worker-only function, called with harmless arguments.</summary>
    public static TheoryData<string> WorkerFunctions => new(
        "select * from identity.activity_counts(now())",
        "select identity.prune_activity(now() - interval '40 days')",
        "select * from tenancy.referenced_logos()",
        "select * from vendor.unalerted_cr_disputes(1)",
        "select vendor.mark_cr_disputes_alerted(array[gen_random_uuid()])",
        "select * from vendor.pending_scan_documents(1)",
        "select * from vendor.stale_uploads()",
        "select * from vendor.claim_stale_upload(gen_random_uuid())",
        "select vendor.remove_stale_upload(gen_random_uuid())");

    public static TheoryData<string, string> WorkerFunctionsInConsoleSessions
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var function in WorkerFunctions)
            {
                // The console gap of ADR-0012 point 4: no tenant and no vendor, with an acting user or (an anonymous
                // request on the platform host) without one.
                data.Add(function, "platform console with a user");
                data.Add(function, "no context at all");
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(WorkerFunctionsInConsoleSessions))]
    public async Task The_app_role_cannot_execute_a_worker_function_even_without_any_context(string sql, string session)
    {
        await using var connection = await ActivityRows.AppSessionAsync(
            db.AppConnectionString, null, null, session == "no context at all" ? null : "platform-admin-sub", Ct);

        var refused = await Should.ThrowAsync<PostgresException>(() => ExecuteInRolledBackTransactionAsync(connection, sql));

        refused.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        refused.MessageText.ShouldStartWith("permission denied for function", Case.Sensitive, "refused by the grant, not by the body");
    }

    [Theory]
    [MemberData(nameof(WorkerFunctions))]
    public async Task The_worker_role_executes_every_worker_function(string sql)
    {
        await using var connection = await ActivityRows.AppSessionAsync(db.WorkerConnectionString, null, null, null, Ct);

        await ExecuteInRolledBackTransactionAsync(connection, sql);
    }

    [Theory]
    [MemberData(nameof(WorkerFunctions))]
    public async Task The_worker_role_keeps_the_context_rules_of_the_worker_functions(string sql)
    {
        // Defence in depth: the worker role with a tenant context is refused (or answered with nothing) as before.
        await using var connection = await ActivityRows.AppSessionAsync(db.WorkerConnectionString, TestTenants.Acme.TenantId, null, null, Ct);

        try
        {
            // The set-returning functions answer nothing; the others refuse in their body.
            (await ExecuteInRolledBackTransactionAsync(connection, sql)).ShouldBe(0, sql);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InsufficientPrivilege)
        {
            ex.MessageText.ShouldNotStartWith("permission denied for function", Case.Sensitive, "the function body refused, not the grant");
        }
    }

    public static TheoryData<string> WorkerWrites => new(
        "insert into ops.health_results (id, component, status, latency_ms, checked_at) values (gen_random_uuid(), 'Probe', 'Healthy', 1, now())",
        "insert into ops.incidents (id, component, opened_at) values (gen_random_uuid(), 'Probe-' || gen_random_uuid(), now())",
        "update ops.incidents set last_message = 'forged'",
        "delete from ops.incidents",
        "insert into ops.active_user_counts (tenant_slug, kind, time_window, users, computed_at) values ('probe-' || gen_random_uuid(), 'staff', '1d', 1, now())",
        "delete from ops.active_user_counts",
        "select count(*) from ops.job_failure_streaks",
        "insert into ops.job_failure_streaks (job_key, failures, updated_at) values ('probe:' || gen_random_uuid(), 1, now())",
        "delete from ops.job_failure_streaks");

    [Theory]
    [MemberData(nameof(WorkerWrites))]
    public async Task The_app_role_cannot_write_what_the_worker_writes_in_ops(string sql)
    {
        await using var console = await ActivityRows.AppSessionAsync(db.AppConnectionString, null, null, "platform-admin-sub", Ct);

        (await ActivityRows.TryAsync(console, sql, Ct)).ShouldBe("refused", sql);
    }

    [Fact]
    public async Task The_console_still_reads_the_board_the_incidents_and_the_usage_counts()
    {
        var component = $"Probe-{Guid.NewGuid():N}";
        await ExecuteAsOwnerAsync($"insert into ops.incidents (id, component, opened_at) values (gen_random_uuid(), '{component}', now())");
        try
        {
            await using var console = await ActivityRows.AppSessionAsync(db.AppConnectionString, null, null, "platform-admin-sub", Ct);
            (await CountAsync(console, $"select count(*)::int from ops.incidents where component = '{component}'")).ShouldBe(1);
            (await ActivityRows.TryAsync(console, "select count(*) from ops.health_results", Ct)).ShouldBe("ok");
            (await ActivityRows.TryAsync(console, "select count(*) from ops.active_user_counts", Ct)).ShouldBe("ok");
        }
        finally
        {
            await ExecuteAsOwnerAsync($"delete from ops.incidents where component = '{component}'");
        }
    }

    [Fact]
    public async Task Incidents_are_hidden_from_a_tenant_or_vendor_session()
    {
        var component = $"Probe-{Guid.NewGuid():N}";
        await ExecuteAsOwnerAsync($"insert into ops.incidents (id, component, opened_at) values (gen_random_uuid(), '{component}', now())");
        try
        {
            await using var staff = await ActivityRows.AppSessionAsync(db.AppConnectionString, TestTenants.Acme.TenantId, null, "acme.admin", Ct);
            (await CountAsync(staff, $"select count(*)::int from ops.incidents where component = '{component}'")).ShouldBe(0);
            await using var vendor = await ActivityRows.AppSessionAsync(db.AppConnectionString, null, Guid.NewGuid(), "vendor-user", Ct);
            (await CountAsync(vendor, $"select count(*)::int from ops.incidents where component = '{component}'")).ShouldBe(0);
        }
        finally
        {
            await ExecuteAsOwnerAsync($"delete from ops.incidents where component = '{component}'");
        }
    }

    [Fact]
    public async Task The_worker_records_incidents_and_failure_streaks_only_without_a_context()
    {
        var component = $"Probe-{Guid.NewGuid():N}";
        var insertIncident = $"insert into ops.incidents (id, component, opened_at) values (gen_random_uuid(), '{component}', now())";
        var insertStreak = $"insert into ops.job_failure_streaks (job_key, failures, updated_at) values ('probe:{component}', 1, now())";

        await using (var worker = await ActivityRows.AppSessionAsync(db.WorkerConnectionString, null, null, null, Ct))
        {
            (await ActivityRows.TryAsync(worker, insertIncident, Ct)).ShouldBe("ok");
            (await ActivityRows.TryAsync(worker, insertStreak, Ct)).ShouldBe("ok");
        }

        // Updating a real incident (incidents_worker_update) works for the worker without a context; deleting one never does.
        await ExecuteAsOwnerAsync(insertIncident);
        try
        {
            var update = $"update ops.incidents set last_message = 'w36 probe' where component = '{component}'";
            await using (var worker = await ActivityRows.AppSessionAsync(db.WorkerConnectionString, null, null, null, Ct))
            {
                (await AffectedAsync(worker, update)).ShouldBe(1);
                (await ActivityRows.TryAsync(worker, $"delete from ops.incidents where component = '{component}'", Ct)).ShouldBe("refused");
            }

            await using var workerWithTenant = await ActivityRows.AppSessionAsync(db.WorkerConnectionString, TestTenants.Acme.TenantId, null, null, Ct);
            (await AffectedAsync(workerWithTenant, update)).ShouldBe(0, "the update policy sees no row in a tenant context");
        }
        finally
        {
            await ExecuteAsOwnerAsync($"delete from ops.incidents where component = '{component}'");
        }

        await using var withTenant = await ActivityRows.AppSessionAsync(db.WorkerConnectionString, TestTenants.Acme.TenantId, null, null, Ct);
        (await ActivityRows.TryAsync(withTenant, insertIncident, Ct)).ShouldBe("refused");
        (await ActivityRows.TryAsync(withTenant, insertStreak, Ct)).ShouldBe("refused");
        await using var withUser = await ActivityRows.AppSessionAsync(db.WorkerConnectionString, null, null, "someone", Ct);
        (await ActivityRows.TryAsync(withUser, insertIncident, Ct)).ShouldBe("refused");
        (await ActivityRows.TryAsync(withUser, insertStreak, Ct)).ShouldBe("refused");
    }

    [Theory]
    [InlineData("incidents")]
    [InlineData("job_failure_streaks")]
    public async Task The_ops_tables_the_worker_writes_have_forced_row_level_security(string table)
    {
        await using var owner = new NpgsqlConnection(db.OwnerConnectionString);
        await owner.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "select relrowsecurity and relforcerowsecurity from pg_class where oid = to_regclass(@table)", owner);
        command.Parameters.AddWithValue("table", $"ops.{table}");

        ((bool)(await command.ExecuteScalarAsync(Ct))!).ShouldBeTrue();
    }

    [Fact]
    public async Task The_worker_role_has_no_right_on_the_key_ring_and_the_app_role_cannot_become_the_worker()
    {
        await using var worker = await ActivityRows.AppSessionAsync(db.WorkerConnectionString, null, null, null, Ct);
        (await ActivityRows.TryAsync(worker, "select count(*) from platform.data_protection_keys", Ct)).ShouldBe("refused");

        await using var app = await ActivityRows.AppSessionAsync(db.AppConnectionString, null, null, null, Ct);
        (await ActivityRows.TryAsync(app, "set role erp_worker", Ct)).ShouldBe("refused");
    }

    [Fact]
    public async Task The_hangfire_tables_belong_to_the_migration_owner_and_no_runtime_role_can_change_them()
    {
        await using (var owner = new NpgsqlConnection(db.OwnerConnectionString))
        {
            await owner.OpenAsync(Ct);
            // Every relation (tables, sequences, indexes), function and type in the schema.
            await using var command = new NpgsqlCommand("""
                select distinct pg_get_userbyid(c.relowner) from pg_class c join pg_namespace n on n.oid = c.relnamespace where n.nspname = 'hangfire'
                union
                select distinct pg_get_userbyid(p.proowner) from pg_proc p join pg_namespace n on n.oid = p.pronamespace where n.nspname = 'hangfire'
                union
                select distinct pg_get_userbyid(t.typowner) from pg_type t join pg_namespace n on n.oid = t.typnamespace where n.nspname = 'hangfire'
                """, owner);
            await using var reader = await command.ExecuteReaderAsync(Ct);
            var owners = new List<string>();
            while (await reader.ReadAsync(Ct))
            {
                owners.Add(reader.GetString(0));
            }

            owners.ShouldBe([new NpgsqlConnectionStringBuilder(db.OwnerConnectionString).Username!]);
        }

        foreach (var connectionString in new[] { db.AppConnectionString, db.WorkerConnectionString })
        {
            await using var session = await ActivityRows.AppSessionAsync(connectionString, null, null, null, Ct);
            (await ActivityRows.TryAsync(session, "create table hangfire.probe (id int)", Ct)).ShouldBe("refused");
            (await ActivityRows.TryAsync(session, "alter table hangfire.job add column probe int", Ct)).ShouldBe("refused");
            (await ActivityRows.TryAsync(session, "drop table hangfire.lock", Ct)).ShouldBe("refused");
            (await ActivityRows.TryAsync(session, "truncate hangfire.job", Ct)).ShouldBe("refused");
        }
    }

    [Fact]
    public async Task The_web_host_enqueues_as_the_app_role_and_the_worker_runs_the_job_as_its_own_role()
    {
        // The web host's registration: Hangfire's storage and client only, as erp_app (D-6).
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPlatformShared();
        services.AddJobClient(db.AppConnectionString);
        await using var web = services.BuildServiceProvider();
        await using var worker = await JobServerHost.StartAsync(db.WorkerConnectionString, cancellationToken: Ct);

        string jobId;
        await using (var scope = web.CreateAsyncScope())
        {
            jobId = scope.ServiceProvider.GetRequiredService<IBackgroundJobClient>().Enqueue(() => WorkerRoleProbe.Run());
        }

        await worker.WaitForSuccessAsync(jobId, Ct);
        new NpgsqlConnectionStringBuilder(db.WorkerConnectionString).Username.ShouldBe(WorkerRole.Name);
    }

    private static async Task<int> ExecuteInRolledBackTransactionAsync(NpgsqlConnection connection, string sql)
    {
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        try
        {
#pragma warning disable CA2100 // The statements are the tests' own.
            await using var command = new NpgsqlCommand(sql, connection, transaction);
#pragma warning restore CA2100
            await using var reader = await command.ExecuteReaderAsync(Ct);
            var rows = 0;
            while (await reader.ReadAsync(Ct))
            {
                rows++;
            }

            return rows;
        }
        finally
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task The_worker_role_has_no_dangerous_attribute_and_only_its_membership_of_the_app_role()
    {
        await using var owner = new NpgsqlConnection(db.OwnerConnectionString);
        await owner.OpenAsync(Ct);
        await using (var attributes = new NpgsqlCommand(
            "select rolsuper or rolbypassrls or rolcreaterole or rolcreatedb or rolreplication, rolcanlogin, rolinherit from pg_roles where rolname = 'erp_worker'", owner))
        await using (var reader = await attributes.ExecuteReaderAsync(Ct))
        {
            (await reader.ReadAsync(Ct)).ShouldBeTrue();
            reader.GetBoolean(0).ShouldBeFalse("no superuser, BYPASSRLS, CREATEROLE, CREATEDB or REPLICATION");
            reader.GetBoolean(1).ShouldBeTrue("the migrator gave it its login");
            reader.GetBoolean(2).ShouldBeTrue();
        }

        await using var memberships = new NpgsqlCommand("""
            select r.rolname || ' inherit=' || m.inherit_option || ' set=' || m.set_option
            from pg_auth_members m join pg_roles r on r.oid = m.roleid
            where m.member = (select oid from pg_roles where rolname = 'erp_worker')
            union all
            select 'member ' || pg_get_userbyid(m.member)
            from pg_auth_members m
            where m.roleid = (select oid from pg_roles where rolname = 'erp_worker') and (m.inherit_option or m.set_option)
            """, owner);
        var rows = new List<string>();
        await using (var reader = await memberships.ExecuteReaderAsync(Ct))
        {
            while (await reader.ReadAsync(Ct))
            {
                rows.Add(reader.GetString(0));
            }
        }

        rows.ShouldBe(["erp_app inherit=true set=false"]);
    }

    public static TheoryData<string> UnsafeWorkerRoles => new(
        "alter role erp_worker createdb",
        "alter role erp_worker bypassrls",
        "grant pg_monitor to erp_worker",
        "create role w36_probe_member nologin; grant erp_worker to w36_probe_member",
        "revoke erp_app from erp_worker; grant erp_app to erp_worker with inherit true, set true");

    [Theory]
    [MemberData(nameof(UnsafeWorkerRoles))]
    public async Task Platform_migration_0008_refuses_a_worker_role_it_would_not_have_created(string change)
    {
        await using var owner = new NpgsqlConnection(db.OwnerConnectionString);
        await owner.OpenAsync(Ct);
        // Role DDL is transactional: the change and the migration run in one transaction that is always rolled back.
        await using var transaction = await owner.BeginTransactionAsync(Ct);
        try
        {
#pragma warning disable CA2100 // The statements are the tests' own and the migration's.
            await using (var alter = new NpgsqlCommand(change, owner, transaction))
            {
                await alter.ExecuteNonQueryAsync(Ct);
            }

            await using var migration = new NpgsqlCommand(await PlatformMigrationAsync("0008_platform_worker_role.sql"), owner, transaction);
#pragma warning restore CA2100
            var refused = await Should.ThrowAsync<PostgresException>(() => migration.ExecuteNonQueryAsync(Ct));
            refused.SqlState.ShouldBe("P0001", change);
            refused.MessageText.ShouldContain("erp_worker", Case.Sensitive, change);
        }
        finally
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Platform_migration_0008_accepts_the_role_it_created_again()
    {
        await using var owner = new NpgsqlConnection(db.OwnerConnectionString);
        await owner.OpenAsync(Ct);
        await using var transaction = await owner.BeginTransactionAsync(Ct);
        try
        {
#pragma warning disable CA2100 // The migration's own text.
            await using var migration = new NpgsqlCommand(await PlatformMigrationAsync("0008_platform_worker_role.sql"), owner, transaction);
#pragma warning restore CA2100
            await migration.ExecuteNonQueryAsync(Ct);
        }
        finally
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
    }

    private static async Task<string> PlatformMigrationAsync(string script)
    {
        await using var stream = typeof(SharedModule).Assembly.GetManifestResourceStream($"Migrations.{script}")!;
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(Ct);
    }

    /// <summary>Rows the statement changes, inside a transaction that is rolled back.</summary>
    private static async Task<int> AffectedAsync(NpgsqlConnection connection, string sql)
    {
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        try
        {
#pragma warning disable CA2100 // The statements are the tests' own.
            await using var command = new NpgsqlCommand(sql, connection, transaction);
#pragma warning restore CA2100
            return await command.ExecuteNonQueryAsync(Ct);
        }
        finally
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
    }

    private static async Task<int> CountAsync(NpgsqlConnection connection, string sql)
    {
#pragma warning disable CA2100 // The statements are the tests' own.
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        return (int)(await command.ExecuteScalarAsync(Ct))!;
    }

    private async Task ExecuteAsOwnerAsync(string sql)
    {
        await using var owner = new NpgsqlConnection(db.OwnerConnectionString);
        await owner.OpenAsync(Ct);
#pragma warning disable CA2100 // The statements are the tests' own.
        await using var command = new NpgsqlCommand(sql, owner);
#pragma warning restore CA2100
        await command.ExecuteNonQueryAsync(Ct);
    }
}

/// <summary>A job with no dependencies, enqueued by the web host's client and run by the worker.</summary>
[PlatformJob]
public static class WorkerRoleProbe
{
    public static void Run()
    {
    }
}
