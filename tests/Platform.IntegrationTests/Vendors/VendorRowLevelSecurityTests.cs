using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Vendors.Persistence;
using Platform.Shared.Tenancy;

namespace Platform.IntegrationTests.Vendors;

/// <summary>
/// Vendor slice task 1 (spec section 2, ADR-0008): platform-level vendor rows under forced row-level security keyed on
/// the vendor company, a tenant-scoped relationship row, and the security-definer functions that cross them.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed partial class VendorRowLevelSecurityTests(DatabaseFixture db) : IAsyncLifetime
{
    private static readonly string[] VendorTables = ["companies", "vendor_users", "documents", "consent_events"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ModuleHost _host = null!;
    private Guid _recipientId;

    public async ValueTask InitializeAsync()
    {
        _host = new ModuleHost(db.AppConnectionString);
        _recipientId = Guid.NewGuid();
        await using var owner = new NpgsqlConnection(db.OwnerConnectionString);
        await owner.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "insert into vendor.recipients (id, name_ar, name_en) values (@id, 'جهة اختبار', 'Test recipient')", owner);
        command.Parameters.AddWithValue("id", _recipientId);
        await command.ExecuteNonQueryAsync(Ct);
    }

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    [Fact]
    public async Task With_no_vendor_context_the_app_role_sees_no_vendor_rows()
    {
        var companyId = await RegisterAsync(TestTenants.Acme);
        await AddDocumentAsync(companyId);
        await AddConsentGrantAsync(companyId);

        foreach (var tenant in new[] { TestTenants.Acme, null })
        {
            await using var scope = _host.ScopeFor(tenant);
            await using var context = await CreateContextAsync(scope);
            foreach (var table in VendorTables)
            {
                (await CountAsync(context, table)).ShouldBe(0, $"vendor.{table} with tenant {tenant?.Slug ?? "none"}");
            }
        }
    }

    [Fact]
    public async Task A_vendor_context_sees_only_its_own_company()
    {
        var mine = await RegisterAsync(TestTenants.Acme);
        var other = await RegisterAsync(TestTenants.Acme);
        await AddDocumentAsync(mine);
        await AddDocumentAsync(other);
        await AddConsentGrantAsync(mine);
        await AddConsentGrantAsync(other);

        await using var scope = _host.ScopeFor(TestTenants.Acme, mine);
        await using var context = await CreateContextAsync(scope);

        (await CompanyIdsAsync(context, "select id as \"Value\" from vendor.companies")).ShouldBe([mine]);
        (await CompanyIdsAsync(context, "select company_id as \"Value\" from vendor.vendor_users")).ShouldBe([mine]);
        (await CompanyIdsAsync(context, "select company_id as \"Value\" from vendor.documents")).ShouldBe([mine]);
        (await CompanyIdsAsync(context, "select company_id as \"Value\" from vendor.consent_events")).ShouldBe([mine]);

        var rejected = await Should.ThrowAsync<PostgresException>(() => InsertDocumentAsync(context, other));
        rejected.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task Tenant_b_never_reads_tenant_a_relationship_rows()
    {
        var companyId = await RegisterAsync(TestTenants.Acme);

        await using (var acme = _host.ScopeFor(TestTenants.Acme))
        {
            await using var context = await CreateContextAsync(acme);
            (await RelationshipTenantsAsync(context, companyId)).ShouldBe([TestTenants.Acme.TenantId]);
        }

        await using var beta = _host.ScopeFor(TestTenants.Beta);
        await using var betaContext = await CreateContextAsync(beta);
        (await RelationshipTenantsAsync(betaContext, companyId)).ShouldBeEmpty();
        (await betaContext.Relationships.IgnoreQueryFilters().CountAsync(r => r.CompanyId == companyId, Ct)).ShouldBe(0);

        var rejected = await Should.ThrowAsync<PostgresException>(() => betaContext.Database.ExecuteSqlAsync(
            $"insert into vendor.relationships (tenant_id, company_id, status) values ({TestTenants.Acme.TenantId}, {companyId}, 'pending')",
            Ct));
        rejected.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task Related_company_returns_rows_only_while_a_relationship_exists()
    {
        var userId = NewUserId();
        var companyId = await RegisterAsync(TestTenants.Acme, userId: userId);
        await AddDocumentAsync(companyId);

        (await RelatedCompanyCountAsync(TestTenants.Acme, companyId)).ShouldBe(1);
        (await RelatedDocumentCountAsync(TestTenants.Acme, companyId)).ShouldBe(1);
        (await RelatedCompanyCountAsync(TestTenants.Beta, companyId)).ShouldBe(0);
        (await RelatedDocumentCountAsync(TestTenants.Beta, companyId)).ShouldBe(0);
        (await RelatedCompanyCountAsync(null, companyId)).ShouldBe(0);

        // Beta meets the vendor (the join flow, task 5): from then on beta sees the shared facts too.
        await JoinAsync(TestTenants.Beta, companyId, userId);

        (await RelatedCompanyCountAsync(TestTenants.Beta, companyId)).ShouldBe(1);
        (await RelatedDocumentCountAsync(TestTenants.Beta, companyId)).ShouldBe(1);

        // Without the relationship, acme no longer sees the company (the app role cannot delete one; the owner can).
        await using (var owner = new NpgsqlConnection(db.OwnerConnectionString))
        {
            await owner.OpenAsync(Ct);
            await using var delete = new NpgsqlCommand(
                "delete from vendor.relationships where tenant_id = @tenant and company_id = @company", owner);
            delete.Parameters.AddWithValue("tenant", TestTenants.Acme.TenantId);
            delete.Parameters.AddWithValue("company", companyId);
            (await delete.ExecuteNonQueryAsync(Ct)).ShouldBe(1);
        }

        (await RelatedCompanyCountAsync(TestTenants.Acme, companyId)).ShouldBe(0);
        (await RelatedDocumentCountAsync(TestTenants.Acme, companyId)).ShouldBe(0);
    }

    [Fact]
    public async Task A_pooled_connection_reused_without_a_vendor_sees_none_of_the_previous_vendors_rows()
    {
        var companyId = await RegisterAsync(TestTenants.Acme);
        // One connection in the pool forces both scopes onto the same PostgreSQL backend.
        var singleConnection = new NpgsqlConnectionStringBuilder(db.AppConnectionString) { MaxPoolSize = 1 }.ConnectionString;
        await using var host = new ModuleHost(singleConnection);

        int vendorBackend;
        await using (var vendor = host.ScopeFor(TestTenants.Acme, companyId))
        {
            await using var context = await CreateContextAsync(vendor);
            vendorBackend = await context.Database.SqlQueryRaw<int>("select pg_backend_pid() as \"Value\"").SingleAsync(Ct);
            (await CountAsync(context, "companies")).ShouldBe(1);
        }

        await using var staff = host.ScopeFor(TestTenants.Acme);
        await using var staffContext = await CreateContextAsync(staff);
        (await staffContext.Database.SqlQueryRaw<int>("select pg_backend_pid() as \"Value\"").SingleAsync(Ct)).ShouldBe(vendorBackend);
        (await CountAsync(staffContext, "companies")).ShouldBe(0);
    }

    [Theory]
    [InlineData("update vendor.consent_events set actor_id = 'someone-else'")]
    [InlineData("delete from vendor.consent_events")]
    public async Task Consent_events_are_append_only(string sql)
    {
        var companyId = await RegisterAsync(TestTenants.Acme);
        await AddConsentGrantAsync(companyId);

        await using var scope = _host.ScopeFor(TestTenants.Acme, companyId);
        await using var context = await CreateContextAsync(scope);

#pragma warning disable EF1002 // Constant SQL from the test's own inline data.
        var rejected = await Should.ThrowAsync<PostgresException>(() => context.Database.ExecuteSqlRawAsync(sql, Ct));
#pragma warning restore EF1002

        rejected.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task Registering_creates_the_company_its_first_vendor_admin_and_a_pending_relationship_for_the_host_tenant()
    {
        var cr = NewCrNumber();
        var companyId = await RegisterAsync(TestTenants.Acme, cr);

        await using (var vendor = _host.ScopeFor(TestTenants.Acme, companyId))
        {
            await using var context = await CreateContextAsync(vendor);
            var company = await context.Companies.SingleAsync(Ct);
            company.CrNumber.ShouldBe(cr);
            var user = await context.VendorUsers.SingleAsync(Ct);
            user.Role.ShouldBe("vendor-admin");
            user.PrivacyNoticeVersion.ShouldBe("V1");
        }

        await using var acme = _host.ScopeFor(TestTenants.Acme);
        await using var acmeContext = await CreateContextAsync(acme);
        var relationship = await acmeContext.Relationships.SingleAsync(r => r.CompanyId == companyId, Ct);
        relationship.Status.ShouldBe("pending");
        relationship.ApprovedBy.ShouldBeNull();
    }

    [Fact]
    public async Task Cr_exists_answers_without_a_vendor_context_and_a_duplicate_cr_is_refused()
    {
        var cr = NewCrNumber();
        (await CrExistsAsync(cr)).ShouldBeFalse();

        await RegisterAsync(TestTenants.Acme, cr);

        (await CrExistsAsync(cr)).ShouldBeTrue();
        var duplicate = await Should.ThrowAsync<PostgresException>(() => RegisterAsync(TestTenants.Beta, cr));
        duplicate.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
    }

    [Fact]
    public async Task Registering_without_a_tenant_is_refused()
    {
        var refused = await Should.ThrowAsync<PostgresException>(() => RegisterAsync(null));

        refused.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task Company_of_user_finds_the_company_before_any_vendor_context_exists()
    {
        // Task 2: the Vendor policy must find the user's company while RLS still hides vendor.vendor_users.
        var userId = Guid.NewGuid().ToString();
        var companyId = await RegisterAsync(TestTenants.Acme, userId: userId);

        foreach (var tenant in new[] { TestTenants.Acme, TestTenants.Beta, null })
        {
            await using var scope = _host.ScopeFor(tenant);
            await using var context = await CreateContextAsync(scope);
            (await CountAsync(context, "vendor_users")).ShouldBe(0);
            (await CompanyOfUserAsync(context, userId)).ShouldBe(companyId);
            (await CompanyOfUserAsync(context, Guid.NewGuid().ToString())).ShouldBeNull();
        }
    }

    [Fact]
    public async Task Company_of_user_runs_as_its_owner_with_a_pinned_search_path_and_only_the_app_role_may_call_it()
    {
        await using var owner = new NpgsqlConnection(db.OwnerConnectionString);
        await owner.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("""
            select p.prosecdef, p.proconfig::text,
                   has_function_privilege('erp_app', p.oid, 'execute'),
                   has_function_privilege('public', p.oid, 'execute')
            from pg_proc p join pg_namespace n on n.oid = p.pronamespace
            where n.nspname = 'vendor' and p.proname = 'company_of_user'
            """, owner);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        (await reader.ReadAsync(Ct)).ShouldBeTrue("vendor.company_of_user exists");
        reader.GetBoolean(0).ShouldBeTrue("security definer");
        reader.GetString(1).ShouldContain("search_path=vendor, pg_temp");
        reader.GetBoolean(2).ShouldBeTrue("erp_app may execute it");
        reader.GetBoolean(3).ShouldBeFalse("public may not execute it");
    }

    [Fact]
    public async Task A_tenant_connection_cannot_insert_or_update_a_relationship_directly()
    {
        var companyId = await RegisterAsync(TestTenants.Acme);
        var other = await RegisterAsync(TestTenants.Beta);

        await using var scope = _host.ScopeFor(TestTenants.Acme, actingUserId: "officer-direct");
        await using var context = await CreateContextAsync(scope);

        var insert = await Should.ThrowAsync<PostgresException>(() => context.Database.ExecuteSqlAsync(
            $"insert into vendor.relationships (tenant_id, company_id, status) values ({TestTenants.Acme.TenantId}, {other}, 'pending')", Ct));
        insert.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        var update = await Should.ThrowAsync<PostgresException>(() => context.Database.ExecuteSqlAsync(
            $"update vendor.relationships set status = 'approved', approved_by = 'officer-direct' where company_id = {companyId}", Ct));
        update.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);

        (await RelationshipAsync(TestTenants.Acme.TenantId, other)).ShouldBeNull();
        (await RelationshipAsync(TestTenants.Acme.TenantId, companyId)).ShouldBe(("pending", null));
        (await RelatedCompanyCountAsync(TestTenants.Acme, other)).ShouldBe(0);
    }

    [Fact]
    public async Task Joining_requires_a_vendor_context_and_a_tenant()
    {
        var userId = NewUserId();
        var companyId = await RegisterAsync(TestTenants.Acme, userId: userId);

        (await Should.ThrowAsync<PostgresException>(() => JoinAsync(null, companyId, userId)))
            .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        (await Should.ThrowAsync<PostgresException>(() => JoinAsync(TestTenants.Beta, null, userId)))
            .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        // The acting user must be a user of the vendor company the context names.
        (await Should.ThrowAsync<PostgresException>(() => JoinAsync(TestTenants.Beta, companyId, null)))
            .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        (await Should.ThrowAsync<PostgresException>(() => JoinAsync(TestTenants.Beta, companyId, NewUserId())))
            .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        (await RelationshipAsync(TestTenants.Beta.TenantId, companyId)).ShouldBeNull();

        await JoinAsync(TestTenants.Beta, companyId, userId);
        await JoinAsync(TestTenants.Beta, companyId, userId);

        (await RelationshipAsync(TestTenants.Beta.TenantId, companyId)).ShouldBe(("pending", null));
        (await RelationshipAsync(TestTenants.Acme.TenantId, companyId)).ShouldBe(("pending", null));
    }

    [Fact]
    public async Task Joining_again_after_approval_keeps_the_approval()
    {
        var userId = NewUserId();
        var companyId = await RegisterAsync(TestTenants.Acme, userId: userId);
        (await ApproveAsync(TestTenants.Acme, companyId, "acme-officer")).ShouldBeTrue();

        await JoinAsync(TestTenants.Acme, companyId, userId);

        (await RelationshipAsync(TestTenants.Acme.TenantId, companyId)).ShouldBe(("approved", "acme-officer"));
    }

    [Fact]
    public async Task Approving_records_the_acting_user()
    {
        var companyId = await RegisterAsync(TestTenants.Acme);

        (await Should.ThrowAsync<PostgresException>(() => ApproveAsync(TestTenants.Acme, companyId, null)))
            .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        (await Should.ThrowAsync<PostgresException>(() => ApproveAsync(null, companyId, "acme-officer")))
            .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        // Beta has no relationship with the company: nothing to approve, and none is created.
        (await ApproveAsync(TestTenants.Beta, companyId, "beta-officer")).ShouldBeFalse();
        (await RelationshipAsync(TestTenants.Beta.TenantId, companyId)).ShouldBeNull();
        (await RelationshipAsync(TestTenants.Acme.TenantId, companyId)).ShouldBe(("pending", null));

        (await ApproveAsync(TestTenants.Acme, companyId, "acme-officer")).ShouldBeTrue();

        (await RelationshipAsync(TestTenants.Acme.TenantId, companyId)).ShouldBe(("approved", "acme-officer"));
        // Approving again changes nothing: the first approver stays on record.
        (await ApproveAsync(TestTenants.Acme, companyId, "acme-admin")).ShouldBeFalse();
        (await RelationshipAsync(TestTenants.Acme.TenantId, companyId)).ShouldBe(("approved", "acme-officer"));
    }

    [Theory]
    [InlineData("join_tenant", "")]
    [InlineData("approve_relationship", "uuid")]
    [InlineData("register_company", "text, text, text, text, text, text, text, text, text")]
    public async Task Relationship_functions_run_as_their_owner_with_a_pinned_search_path_and_only_the_app_role_may_call_them(
        string name, string arguments)
    {
        await using var owner = new NpgsqlConnection(db.OwnerConnectionString);
        await owner.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("""
            select p.prosecdef, p.proconfig::text, pg_get_function_identity_arguments(p.oid),
                   has_function_privilege('erp_app', p.oid, 'execute'),
                   has_function_privilege('public', p.oid, 'execute')
            from pg_proc p join pg_namespace n on n.oid = p.pronamespace
            where n.nspname = 'vendor' and p.proname = @name
            """, owner);
        command.Parameters.AddWithValue("name", name);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        (await reader.ReadAsync(Ct)).ShouldBeTrue($"vendor.{name} exists");
        reader.GetBoolean(0).ShouldBeTrue("security definer");
        reader.GetString(1).ShouldContain("search_path=vendor, pg_temp");
        ArgumentNames().Replace(reader.GetString(2), string.Empty).ShouldBe(arguments);
        reader.GetBoolean(3).ShouldBeTrue("erp_app may execute it");
        reader.GetBoolean(4).ShouldBeFalse("public may not execute it");
        (await reader.ReadAsync(Ct)).ShouldBeFalse($"vendor.{name} has one signature");
    }

    [Fact]
    public async Task Registering_without_an_acting_user_is_refused()
    {
        var cr = NewCrNumber();

        var refused = await Should.ThrowAsync<PostgresException>(() => RegisterAsync(TestTenants.Acme, cr, null, "V1"));

        refused.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        (await CrExistsAsync(cr)).ShouldBeFalse();
    }

    [Fact]
    public async Task Registering_takes_the_vendor_admin_from_the_session_and_no_user_parameter_exists()
    {
        var sessionUser = NewUserId();
        var companyId = await RegisterAsync(TestTenants.Acme, userId: sessionUser);

        (await VendorAdminOfAsync(companyId)).ShouldBe(sessionUser);

        // The old signature, which let the caller name the user, is gone.
        await using var scope = _host.ScopeFor(TestTenants.Acme, actingUserId: NewUserId());
        await using var context = await CreateContextAsync(scope);
        var cr = NewCrNumber();
        var other = NewUserId();
        var gone = await Should.ThrowAsync<PostgresException>(() => context.Database.SqlQuery<Guid>($"""
            select vendor.register_company({cr}, 'شركة', 'Company', '300000000000003', 'Riyadh',
                                           'Contact Person', '+966500000000', 'contact@example.test', {other}, 'V1') as "Value"
            """).SingleAsync(Ct));
        gone.SqlState.ShouldBe(PostgresErrorCodes.UndefinedFunction);
        (await CrExistsAsync(cr)).ShouldBeFalse();
    }

    [Fact]
    public async Task A_pooled_connection_reused_without_an_acting_user_carries_none()
    {
        var singleConnection = new NpgsqlConnectionStringBuilder(db.AppConnectionString) { MaxPoolSize = 1 }.ConnectionString;
        await using var host = new ModuleHost(singleConnection);

        int backend;
        await using (var signedIn = host.ScopeFor(TestTenants.Acme, actingUserId: "someone"))
        {
            await using var context = await CreateContextAsync(signedIn);
            backend = await context.Database.SqlQueryRaw<int>("select pg_backend_pid() as \"Value\"").SingleAsync(Ct);
            (await CurrentUserAsync(context)).ShouldBe("someone");
        }

        await using var anonymous = host.ScopeFor(TestTenants.Acme);
        await using var anonymousContext = await CreateContextAsync(anonymous);
        (await anonymousContext.Database.SqlQueryRaw<int>("select pg_backend_pid() as \"Value\"").SingleAsync(Ct)).ShouldBe(backend);
        (await CurrentUserAsync(anonymousContext)).ShouldBeNull();
    }

    [Fact]
    public async Task A_revocation_cannot_name_another_revocation()
    {
        var companyId = await RegisterAsync(TestTenants.Acme);
        var grant = await AddConsentGrantAsync(companyId);
        var revocation = await AddConsentRevocationAsync(companyId, grant);

        var refused = await Should.ThrowAsync<PostgresException>(() => AddConsentRevocationAsync(companyId, revocation));

        refused.SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData("V1234567890123456789012345678901234567890")]
    public async Task A_privacy_notice_version_must_be_1_to_40_characters_and_not_blank(string version)
    {
        var refused = await Should.ThrowAsync<PostgresException>(() => RegisterAsync(TestTenants.Acme, null, NewUserId(), version));

        refused.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        refused.ConstraintName.ShouldBe("ck_vendor_users_privacy_notice_version");
    }

    [Fact]
    public async Task A_privacy_notice_version_of_40_characters_is_accepted()
    {
        var companyId = await RegisterAsync(TestTenants.Acme, null, NewUserId(), new string('V', 40));

        (await VendorAdminOfAsync(companyId)).ShouldNotBeNull();
    }

    private static async Task<string?> CurrentUserAsync(VendorsDbContext context) =>
        await context.Database.SqlQueryRaw<string?>("select platform.current_user_id() as \"Value\"").SingleAsync(Ct);

    private static async Task<Guid?> CompanyOfUserAsync(VendorsDbContext context, string userId) =>
        await context.Database.SqlQuery<Guid?>($"select vendor.company_of_user({userId}) as \"Value\"").SingleAsync(Ct);

    private Task<Guid> RegisterAsync(TenantContext? tenant, string? crNumber = null, string? userId = null) =>
        RegisterAsync(tenant, crNumber, userId ?? NewUserId(), "V1");

    private async Task<Guid> RegisterAsync(TenantContext? tenant, string? crNumber, string? actingUserId, string privacyNoticeVersion)
    {
        var cr = crNumber ?? NewCrNumber();
        await using var scope = _host.ScopeFor(tenant, actingUserId: actingUserId);
        await using var context = await CreateContextAsync(scope);
        return await context.Database.SqlQuery<Guid>($"""
            select vendor.register_company({cr}, 'شركة الاختبار', 'Test Company', '300000000000003', 'Riyadh',
                                           'Contact Person', '+966500000000', 'contact@example.test', {privacyNoticeVersion}) as "Value"
            """).SingleAsync(Ct);
    }

    private async Task JoinAsync(TenantContext? tenant, Guid? companyId, string? actingUserId)
    {
        await using var scope = _host.ScopeFor(tenant, companyId, actingUserId);
        await using var context = await CreateContextAsync(scope);
        await context.Database.ExecuteSqlAsync($"select vendor.join_tenant()", Ct);
    }

    private async Task<bool> ApproveAsync(TenantContext? tenant, Guid companyId, string? actingUserId)
    {
        await using var scope = _host.ScopeFor(tenant, actingUserId: actingUserId);
        await using var context = await CreateContextAsync(scope);
        return await context.Database.SqlQuery<bool>($"select vendor.approve_relationship({companyId}) as \"Value\"").SingleAsync(Ct);
    }

    private async Task<(string Status, string? ApprovedBy)?> RelationshipAsync(Guid tenantId, Guid companyId)
    {
        await using var owner = new NpgsqlConnection(db.OwnerConnectionString);
        await owner.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "select status, approved_by from vendor.relationships where tenant_id = @tenant and company_id = @company", owner);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("company", companyId);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        return await reader.ReadAsync(Ct) ? (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)) : null;
    }

    private async Task<string?> VendorAdminOfAsync(Guid companyId)
    {
        await using var owner = new NpgsqlConnection(db.OwnerConnectionString);
        await owner.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("select user_id from vendor.vendor_users where company_id = @company", owner);
        command.Parameters.AddWithValue("company", companyId);
        return (string?)await command.ExecuteScalarAsync(Ct);
    }

    private static string NewUserId() => Guid.NewGuid().ToString();

    private async Task AddDocumentAsync(Guid companyId)
    {
        await using var scope = _host.ScopeFor(null, companyId);
        await using var context = await CreateContextAsync(scope);
        await InsertDocumentAsync(context, companyId);
    }

    private static Task<int> InsertDocumentAsync(VendorsDbContext context, Guid companyId)
    {
        var sha = Convert.ToHexStringLower(SHA256.HashData(Guid.NewGuid().ToByteArray()));
        return context.Database.ExecuteSqlAsync($"""
            insert into vendor.documents (id, company_id, type, expires_on, object_key, sha256, scan_status, is_current)
            values ({Guid.NewGuid()}, {companyId}, 'cr_certificate', date '2030-01-01', {"vendors/" + sha}, {sha}, 'clean', true)
            """, Ct);
    }

    private async Task<Guid> AddConsentGrantAsync(Guid companyId)
    {
        var id = Guid.NewGuid();
        await using var scope = _host.ScopeFor(null, companyId);
        await using var context = await CreateContextAsync(scope);
        await context.Database.ExecuteSqlAsync($"""
            insert into vendor.consent_events (id, company_id, recipient_id, scope, kind, valid_from, valid_to, actor_id)
            values ({id}, {companyId}, {_recipientId}, 'award_records', 'grant', date '2026-01-01', date '2027-01-01', 'vendor-admin-user')
            """, Ct);
        return id;
    }

    private async Task<Guid> AddConsentRevocationAsync(Guid companyId, Guid revokes)
    {
        var id = Guid.NewGuid();
        await using var scope = _host.ScopeFor(null, companyId);
        await using var context = await CreateContextAsync(scope);
        await context.Database.ExecuteSqlAsync($"""
            insert into vendor.consent_events (id, company_id, recipient_id, scope, kind, revokes_grant_id, actor_id)
            values ({id}, {companyId}, {_recipientId}, 'award_records', 'revoke', {revokes}, 'vendor-admin-user')
            """, Ct);
        return id;
    }

    private async Task<bool> CrExistsAsync(string cr)
    {
        await using var scope = _host.ScopeFor(null);
        await using var context = await CreateContextAsync(scope);
        return await context.Database.SqlQuery<bool>($"select vendor.cr_exists({cr}) as \"Value\"").SingleAsync(Ct);
    }

    private async Task<int> RelatedCompanyCountAsync(TenantContext? tenant, Guid companyId)
    {
        await using var scope = _host.ScopeFor(tenant);
        await using var context = await CreateContextAsync(scope);
        return await context.Database.SqlQuery<Guid>($"select id as \"Value\" from vendor.related_company({companyId})").CountAsync(Ct);
    }

    private async Task<int> RelatedDocumentCountAsync(TenantContext? tenant, Guid companyId)
    {
        await using var scope = _host.ScopeFor(tenant);
        await using var context = await CreateContextAsync(scope);
        return await context.Database.SqlQuery<Guid>($"select id as \"Value\" from vendor.related_documents({companyId})").CountAsync(Ct);
    }

    private static Task<List<Guid>> RelationshipTenantsAsync(VendorsDbContext context, Guid companyId) =>
        context.Database
            .SqlQuery<Guid>($"select tenant_id as \"Value\" from vendor.relationships where company_id = {companyId}")
            .ToListAsync(Ct);

#pragma warning disable EF1002 // Table names and SQL come from the test's own constants.
    private static Task<int> CountAsync(VendorsDbContext context, string table) =>
        context.Database.SqlQueryRaw<int>($"select count(*)::int as \"Value\" from vendor.{table}").SingleAsync(Ct);

    private static async Task<List<Guid>> CompanyIdsAsync(VendorsDbContext context, string sql) =>
        await context.Database.SqlQueryRaw<Guid>(sql).Distinct().ToListAsync(Ct);
#pragma warning restore EF1002

    // Ten digits, random enough that tests sharing the database never collide.
    private static string NewCrNumber() =>
        RandomNumberGenerator.GetInt32(1_000_000_000, int.MaxValue).ToString(CultureInfo.InvariantCulture);

    private static Task<VendorsDbContext> CreateContextAsync(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IDbContextFactory<VendorsDbContext>>().CreateDbContextAsync(Ct);

    // The parameter names in pg_get_function_identity_arguments output ("p_company_id uuid" becomes "uuid").
    [GeneratedRegex("p_[a-z_]+ ", RegexOptions.CultureInvariant)]
    private static partial Regex ArgumentNames();
}
