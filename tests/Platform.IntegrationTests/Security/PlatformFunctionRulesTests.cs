using System.Globalization;
using System.Text.Json;
using Npgsql;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Identity.Contracts;

namespace Platform.IntegrationTests.Security;

/// <summary>
/// The second pentest's fixes outside the vendor schema (ADR-0012 point 4): the tenant audit log takes the database's time
/// and a vendor writes only as itself; the platform audit is written only through its function and read only by a session
/// with neither a tenant nor a vendor context; branding needs an active tenant admin of the host tenant and never a vendor;
/// the tenant list answers only a session without a context (the platform console).
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class PlatformFunctionRulesTests(DatabaseFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_staff_insert_into_the_tenant_audit_log_gets_the_database_time()
    {
        var officer = $"officer-{Guid.NewGuid():N}";
        var id = Guid.CreateVersion7();

        await using (var staff = await AppConnectionAsync(TestTenants.Acme.TenantId, null, officer))
        await using (var insert = new NpgsqlCommand("""
            insert into audit.events (id, tenant_id, occurred_at, actor_id, action, subject_type)
            values (@id, @tenant, timestamptz '2020-01-01 00:00:00+00', @actor, 'test.clock', 'x')
            """, staff))
        {
            insert.Parameters.AddWithValue("id", id);
            insert.Parameters.AddWithValue("tenant", TestTenants.Acme.TenantId);
            insert.Parameters.AddWithValue("actor", officer);
            await insert.ExecuteNonQueryAsync(Ct);
        }

        await using var owner = await OwnerAsync();
        await using var read = new NpgsqlCommand("select occurred_at > now() - interval '1 hour' from audit.events where id = @id", owner);
        read.Parameters.AddWithValue("id", id);
        ((bool)(await read.ExecuteScalarAsync(Ct))!).ShouldBeTrue("the database's time replaces the time the writer claimed");
    }

    [Fact]
    public async Task The_platform_audit_is_written_through_its_function_and_read_only_without_a_context()
    {
        var actor = $"platform-admin-{Guid.NewGuid():N}";
        var id = Guid.CreateVersion7();
        var (companyId, vendorUser) = await VendorAsync();

        await using (var console = await AppConnectionAsync(null, null, actor))
        {
            (await StateAsync(console, """
                insert into ops.platform_audit (id, actor_id, action, subject_type) values (gen_random_uuid(), 'x', 'x', 'x')
                """)).ShouldBe("refused", "no direct write");

            await using var write = new NpgsqlCommand("select ops.write_platform_audit(@id, @actor, 'test.written', 'test', 'subject', '{\"a\": \"b\"}'::jsonb)", console);
            write.Parameters.AddWithValue("id", id);
            write.Parameters.AddWithValue("actor", actor);
            await write.ExecuteNonQueryAsync(Ct);

            await using var read = new NpgsqlCommand("select actor_id, action, data::text, occurred_at > now() - interval '1 hour' from ops.platform_audit where id = @id", console);
            read.Parameters.AddWithValue("id", id);
            await using var reader = await read.ExecuteReaderAsync(Ct);
            (await reader.ReadAsync(Ct)).ShouldBeTrue("a session without a context reads the platform audit");
            reader.GetString(0).ShouldBe(actor);
            reader.GetString(1).ShouldBe("test.written");
            JsonDocument.Parse(reader.GetString(2)).RootElement.GetProperty("a").GetString().ShouldBe("b");
            reader.GetBoolean(3).ShouldBeTrue("the function sets the database's time");
        }

        foreach (var (label, tenant, vendor, user) in new (string, Guid?, Guid?, string)[]
        {
            ("tenant staff", TestTenants.Acme.TenantId, null, actor),
            ("vendor", TestTenants.Acme.TenantId, companyId, vendorUser),
            ("vendor without a tenant", null, companyId, vendorUser),
        })
        {
            await using var session = await AppConnectionAsync(tenant, vendor, user);
            await using var count = new NpgsqlCommand("select count(*)::int from ops.platform_audit where id = @id", session);
            count.Parameters.AddWithValue("id", id);
            ((int)(await count.ExecuteScalarAsync(Ct))!).ShouldBe(0, label);
            (await StateAsync(session, "select ops.write_platform_audit(gen_random_uuid(), 'someone-else', 'test.forged', 'test', null, '{}'::jsonb)"))
                .ShouldBe("refused", label);
            (await StateAsync(session, $"select ops.write_platform_audit(gen_random_uuid(), '{user}', 'test.own', 'test', null, '{{}}'::jsonb)"))
                .ShouldNotBe("refused", label);
        }
    }

    [Fact]
    public async Task Branding_changes_only_for_an_active_tenant_admin_of_the_host_and_never_in_a_vendor_session()
    {
        var tenant = await TenantRows.InsertAsync(db.OwnerConnectionString, Ct);
        var admin = $"admin-{Guid.NewGuid():N}";
        var invited = $"invited-{Guid.NewGuid():N}";
        var officer = $"officer-{Guid.NewGuid():N}";
        await MemberRows.InsertAsync(db.AppConnectionString, tenant.TenantId, admin, $"{admin}@t.test", [TenantRoles.TenantAdmin], "active", Ct);
        await MemberRows.InsertAsync(db.AppConnectionString, tenant.TenantId, invited, $"{invited}@t.test", [TenantRoles.TenantAdmin], "invited", Ct);
        await MemberRows.InsertAsync(db.AppConnectionString, tenant.TenantId, officer, $"{officer}@t.test", [TenantRoles.ContractsOfficer], "active", Ct);
        var (companyId, vendorUser) = await VendorAsync();
        const string rebrand = "select * from tenancy.update_branding('Renamed', '#0F766E', null)";

        var states = new Dictionary<string, string>();
        foreach (var (label, user, vendor) in new (string, string?, Guid?)[]
        {
            ("admin", admin, null), ("invited admin", invited, null), ("officer", officer, null), ("nobody", null, null),
            ("admin with a vendor context", admin, companyId), ("vendor", vendorUser, companyId),
        })
        {
            await using var connection = await AppConnectionAsync(tenant.TenantId, vendor, user);
            states[label] = await StateAsync(connection, rebrand);
        }

        states.ShouldBe(new Dictionary<string, string>
        {
            ["admin"] = "rows",
            ["invited admin"] = "refused",
            ["officer"] = "refused",
            ["nobody"] = "refused",
            ["admin with a vendor context"] = "refused",
            ["vendor"] = "refused",
        });
    }

    [Fact]
    public async Task The_tenant_list_answers_only_a_session_without_a_tenant_or_vendor_context()
    {
        var (companyId, vendorUser) = await VendorAsync();

        await using var console = await AppConnectionAsync(null, null, "platform-admin");
        (await StateAsync(console, "select * from tenancy.list_tenants()")).ShouldBe("rows");
        await using var staff = await AppConnectionAsync(TestTenants.Acme.TenantId, null, "acme.admin");
        (await StateAsync(staff, "select * from tenancy.list_tenants()")).ShouldBe("refused");
        await using var vendorOnly = await AppConnectionAsync(null, companyId, vendorUser);
        (await StateAsync(vendorOnly, "select * from tenancy.list_tenants()")).ShouldBe("refused");
    }

    private async Task<(Guid CompanyId, string UserId)> VendorAsync()
    {
        var userId = Guid.NewGuid().ToString();
        var companyId = await VendorRows.RegisterAsync(db.AppConnectionString, TestTenants.Acme, userId, VendorRows.NewCrNumber(), "Function Rules Vendor", Ct);
        return (companyId, userId);
    }

    private async Task<NpgsqlConnection> AppConnectionAsync(Guid? tenantId, Guid? vendorCompanyId, string? userId)
    {
        var connection = new NpgsqlConnection(db.AppConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("""
            select set_config('app.tenant_id', @tenant, false),
                   set_config('app.vendor_company_id', @vendor, false),
                   set_config('app.user_id', @user, false)
            """, connection);
        command.Parameters.AddWithValue("tenant", tenantId?.ToString("D", CultureInfo.InvariantCulture) ?? string.Empty);
        command.Parameters.AddWithValue("vendor", vendorCompanyId?.ToString("D", CultureInfo.InvariantCulture) ?? string.Empty);
        command.Parameters.AddWithValue("user", userId ?? string.Empty);
        await command.ExecuteNonQueryAsync(Ct);
        return connection;
    }

    private async Task<NpgsqlConnection> OwnerAsync()
    {
        var owner = new NpgsqlConnection(db.OwnerConnectionString);
        await owner.OpenAsync(Ct);
        return owner;
    }

    /// <summary>"refused" (42501), "rows" or "no rows", in a rolled-back transaction.</summary>
    private static async Task<string> StateAsync(NpgsqlConnection connection, string sql)
    {
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        try
        {
#pragma warning disable CA2100 // Test SQL built from constants and generated ids only.
            await using var command = new NpgsqlCommand(sql, connection, transaction);
#pragma warning restore CA2100
            await using var reader = await command.ExecuteReaderAsync(Ct);
            return await reader.ReadAsync(Ct) ? "rows" : "no rows";
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InsufficientPrivilege)
        {
            return "refused";
        }
        finally
        {
            await transaction.RollbackAsync(Ct);
        }
    }
}
