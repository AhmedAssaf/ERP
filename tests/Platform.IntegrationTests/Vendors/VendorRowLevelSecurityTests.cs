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
    [InlineData("register_company", "text, text, text, text, text, text, text, text, text, text")]
    [InlineData("stale_uploads", "")]
    [InlineData("remove_stale_upload", "uuid")]
    [InlineData("claim_stale_upload", "uuid")]
    [InlineData("pending_scan_documents", "integer")]
    [InlineData("related_companies", "")]
    [InlineData("related_current_documents", "")]
    [InlineData("related_company", "uuid")]
    [InlineData("related_documents", "uuid")]
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
    public async Task A_document_object_key_is_its_own_document_or_quarantine_key()
    {
        var companyId = await RegisterAsync(TestTenants.Acme);
        var other = await RegisterAsync(TestTenants.Acme);
        var id = Guid.NewGuid();

        foreach (var wrong in new[]
        {
            $"vendors/{other}/documents/{id}",
            $"vendors/{companyId}/documents/{Guid.NewGuid()}",
            $"vendors/{companyId}/elsewhere/{id}",
            $"staging/{id}/0",
            $"vendors/{companyId.ToString().ToUpperInvariant()}/documents/{id}",
        })
        {
            (await Should.ThrowAsync<PostgresException>(() => InsertDocumentAsOwnerAsync(companyId, id, wrong)))
                .SqlState.ShouldBe(PostgresErrorCodes.CheckViolation, wrong);
        }

        await InsertDocumentAsOwnerAsync(companyId, id, $"vendors/{companyId}/quarantine/{id}");
        var clean = Guid.NewGuid();
        await InsertDocumentAsOwnerAsync(companyId, clean, $"vendors/{companyId}/documents/{clean}");
    }

    [Fact]
    public async Task An_upload_names_only_a_document_of_its_own_company()
    {
        var companyId = await RegisterAsync(TestTenants.Acme);
        var other = await RegisterAsync(TestTenants.Acme);
        var othersDocument = Guid.NewGuid();
        await InsertDocumentAsOwnerAsync(other, othersDocument, $"vendors/{other}/quarantine/{othersDocument}");
        var ownDocument = Guid.NewGuid();
        await InsertDocumentAsOwnerAsync(companyId, ownDocument, $"vendors/{companyId}/quarantine/{ownDocument}");

        (await Should.ThrowAsync<PostgresException>(() => InsertCompletedUploadAsOwnerAsync(companyId, othersDocument)))
            .SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);
        (await Should.ThrowAsync<PostgresException>(() => InsertCompletedUploadAsOwnerAsync(companyId, Guid.NewGuid())))
            .SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);
        await InsertCompletedUploadAsOwnerAsync(companyId, ownDocument);
    }

    [Fact]
    public async Task An_uploads_document_reference_is_checked_at_commit()
    {
        await using var owner = new NpgsqlConnection(db.OwnerConnectionString);
        await owner.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("""
            select c.condeferrable, c.condeferred from pg_constraint c
            where c.conrelid = 'vendor.uploads'::regclass and c.conname = 'fk_uploads_document'
            """, owner);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        (await reader.ReadAsync(Ct)).ShouldBeTrue("fk_uploads_document exists");
        reader.GetBoolean(0).ShouldBeTrue("deferrable");
        reader.GetBoolean(1).ShouldBeTrue("initially deferred");
    }

    [Fact]
    public async Task A_refused_upload_names_no_document()
    {
        var companyId = await RegisterAsync(TestTenants.Acme);
        var document = Guid.NewGuid();
        await InsertDocumentAsOwnerAsync(companyId, document, $"vendors/{companyId}/quarantine/{document}");

        await InsertUploadWithOutcomeAsOwnerAsync(companyId, "refused", null);
        (await Should.ThrowAsync<PostgresException>(() => InsertUploadWithOutcomeAsOwnerAsync(companyId, "refused", document)))
            .SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        (await Should.ThrowAsync<PostgresException>(() => InsertUploadWithOutcomeAsOwnerAsync(companyId, "withdrawn", null)))
            .SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
    }

    [Fact]
    public async Task Unparking_a_document_runs_as_its_owner_with_a_pinned_search_path_and_neither_the_app_role_nor_public_may_call_it()
    {
        await using var owner = new NpgsqlConnection(db.OwnerConnectionString);
        await owner.OpenAsync(Ct);
        await using (var command = new NpgsqlCommand("""
            select p.prosecdef, p.proconfig::text, pg_get_function_identity_arguments(p.oid),
                   has_function_privilege('erp_app', p.oid, 'execute'),
                   has_function_privilege('public', p.oid, 'execute')
            from pg_proc p join pg_namespace n on n.oid = p.pronamespace
            where n.nspname = 'vendor' and p.proname = 'unpark_document'
            """, owner))
        await using (var reader = await command.ExecuteReaderAsync(Ct))
        {
            (await reader.ReadAsync(Ct)).ShouldBeTrue("vendor.unpark_document exists");
            reader.GetBoolean(0).ShouldBeTrue("security definer");
            reader.GetString(1).ShouldContain("search_path=vendor, pg_temp");
            ArgumentNames().Replace(reader.GetString(2), string.Empty).ShouldBe("uuid");
            reader.GetBoolean(3).ShouldBeFalse("erp_app may not execute it: platform operators only");
            reader.GetBoolean(4).ShouldBeFalse("public may not execute it");
        }

        await using var app = new NpgsqlConnection(db.AppConnectionString);
        await app.OpenAsync(Ct);
        await using var call = new NpgsqlCommand("select vendor.unpark_document(@id)", app);
        call.Parameters.AddWithValue("id", Guid.NewGuid());
        (await Should.ThrowAsync<PostgresException>(() => call.ExecuteScalarAsync(Ct))).SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task A_personal_login_that_sets_role_erp_unparks_a_document_and_the_audit_names_that_login()
    {
        var companyId = await RegisterAsync(TestTenants.Acme);
        var documentId = Guid.NewGuid();
        await InsertDocumentAsOwnerAsync(companyId, documentId, $"vendors/{companyId}/quarantine/{documentId}");
        var login = $"ops_{Guid.NewGuid():N}"[..20];
        var password = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        await using (var owner = new NpgsqlConnection(db.OwnerConnectionString))
        {
            await owner.OpenAsync(Ct);
            await using var park = new NpgsqlCommand("update vendor.documents set scan_attempts = 12 where id = @id", owner);
            park.Parameters.AddWithValue("id", documentId);
            (await park.ExecuteNonQueryAsync(Ct)).ShouldBe(1);
            // The docs/07 section 4 command: a personal login, a member of erp, that must SET ROLE to use it.
            await using var create = new NpgsqlCommand($"create role {login} login noinherit password '{password}' in role erp", owner);
            await create.ExecuteNonQueryAsync(Ct);
        }

        try
        {
            var personal = new NpgsqlConnectionStringBuilder(db.OwnerConnectionString) { Username = login, Password = password, Pooling = false };
            await using var connection = new NpgsqlConnection(personal.ConnectionString);
            await connection.OpenAsync(Ct);
            await using (var withoutRole = new NpgsqlCommand("select vendor.unpark_document(@id)", connection))
            {
                withoutRole.Parameters.AddWithValue("id", documentId);
                (await Should.ThrowAsync<PostgresException>(() => withoutRole.ExecuteScalarAsync(Ct)))
                    .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege, "the login alone may not unpark");
            }

            await using (var setRole = new NpgsqlCommand("set role erp", connection))
            {
                await setRole.ExecuteNonQueryAsync(Ct);
            }

            await using var unpark = new NpgsqlCommand("select vendor.unpark_document(@id)", connection);
            unpark.Parameters.AddWithValue("id", documentId);
            ((bool)(await unpark.ExecuteScalarAsync(Ct))!).ShouldBeTrue();

            await using var owner = new NpgsqlConnection(db.OwnerConnectionString);
            await owner.OpenAsync(Ct);
            await using var audit = new NpgsqlCommand(
                "select actor_id from ops.platform_audit where action = 'vendor.document_unparked' and data ->> 'document_id' = @document", owner);
            audit.Parameters.AddWithValue("document", documentId.ToString("D"));
            (await audit.ExecuteScalarAsync(Ct)).ShouldBe(login, "the audit names the person, never the shared erp role");
        }
        finally
        {
            await using var owner = new NpgsqlConnection(db.OwnerConnectionString);
            await owner.OpenAsync(Ct);
            await using var drop = new NpgsqlCommand($"drop role {login}", owner);
            await drop.ExecuteNonQueryAsync(Ct);
        }
    }

    [Fact]
    public async Task The_platform_audit_table_records_that_unparking_a_vendor_document_writes_to_it_directly()
    {
        await using var owner = new NpgsqlConnection(db.OwnerConnectionString);
        await owner.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("select obj_description('ops.platform_audit'::regclass, 'pg_class')", owner);

        var comment = await command.ExecuteScalarAsync(Ct) as string;

        comment.ShouldNotBeNull();
        comment.ShouldContain("vendor.unpark_document");
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

        // No signature lets the caller name the user: the one register_company has no user parameter.
        await using var owner = new NpgsqlConnection(db.OwnerConnectionString);
        await owner.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("""
            select string_agg(pg_get_function_arguments(p.oid), ' | ')
            from pg_proc p join pg_namespace n on n.oid = p.pronamespace
            where n.nspname = 'vendor' and p.proname = 'register_company'
            """, owner);
        var arguments = (string)(await command.ExecuteScalarAsync(Ct))!;
        arguments.ShouldContain("p_privacy_notice_culture");
        arguments.ShouldNotContain("user");
    }

    [Theory]
    [InlineData("fr-FR")]
    [InlineData("ar")]
    [InlineData("")]
    public async Task A_privacy_notice_culture_must_be_arabic_or_english(string culture)
    {
        var refused = await Should.ThrowAsync<PostgresException>(() => RegisterAsync(TestTenants.Acme, null, NewUserId(), "V1", culture));

        refused.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        refused.ConstraintName.ShouldBe("ck_vendor_users_privacy_notice_culture");
    }

    [Fact]
    public async Task A_privacy_notice_culture_is_required()
    {
        var cr = NewCrNumber();

        var refused = await Should.ThrowAsync<PostgresException>(() => RegisterAsync(TestTenants.Acme, cr, NewUserId(), "V1", null));

        refused.SqlState.ShouldBe(PostgresErrorCodes.NotNullViolation);
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

    private async Task<Guid> RegisterAsync(
        TenantContext? tenant, string? crNumber, string? actingUserId, string privacyNoticeVersion, string? privacyNoticeCulture = "en-US")
    {
        var cr = crNumber ?? NewCrNumber();
        await using var scope = _host.ScopeFor(tenant, actingUserId: actingUserId);
        await using var context = await CreateContextAsync(scope);
        return await context.Database.SqlQuery<Guid>($"""
            select vendor.register_company({cr}, 'شركة الاختبار', 'Test Company', '300000000000003', 'Riyadh',
                                           'Contact Person', '+966500000000', 'contact@example.test', {privacyNoticeVersion},
                                           {privacyNoticeCulture}) as "Value"
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
        var id = Guid.NewGuid();
        var sha = Convert.ToHexStringLower(SHA256.HashData(id.ToByteArray()));
        return context.Database.ExecuteSqlAsync($"""
            insert into vendor.documents (id, company_id, type, expires_on, object_key, sha256, scan_status, is_current)
            values ({id}, {companyId}, 'cr_certificate', date '2030-01-01', {$"vendors/{companyId}/documents/{id}"}, {sha}, 'clean', true)
            """, Ct);
    }

    /// <summary>A document row written as the owner (no row-level security), with the given object key.</summary>
    private async Task InsertDocumentAsOwnerAsync(Guid companyId, Guid id, string objectKey)
    {
        await using var owner = new NpgsqlConnection(db.OwnerConnectionString);
        await owner.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("""
            insert into vendor.documents (id, company_id, type, expires_on, object_key, sha256, scan_status, is_current)
            values (@id, @company, 'cr_certificate', date '2030-01-01', @key, @sha, 'pending_scan', false)
            """, owner);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("company", companyId);
        command.Parameters.AddWithValue("key", objectKey);
        command.Parameters.AddWithValue("sha", Convert.ToHexStringLower(SHA256.HashData(id.ToByteArray())));
        await command.ExecuteNonQueryAsync(Ct);
    }

    /// <summary>A completed upload row written as the owner, naming <paramref name="documentId"/> as its document.</summary>
    private async Task InsertCompletedUploadAsOwnerAsync(Guid companyId, Guid documentId)
    {
        await using var owner = new NpgsqlConnection(db.OwnerConnectionString);
        await owner.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("""
            insert into vendor.uploads (id, company_id, document_type, file_name, content_type, declared_size, chunk_size,
                                        chunk_count, outcome, document_id, sha256)
            values (@id, @company, 'cr_certificate', 'cr.pdf', 'application/pdf', 1000, 1048576, 1, 'pending_scan', @document, @sha)
            """, owner);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("company", companyId);
        command.Parameters.AddWithValue("document", documentId);
        command.Parameters.AddWithValue("sha", Convert.ToHexStringLower(SHA256.HashData(documentId.ToByteArray())));
        await command.ExecuteNonQueryAsync(Ct);
    }

    /// <summary>An upload row written as the owner with the given outcome and document.</summary>
    private async Task InsertUploadWithOutcomeAsOwnerAsync(Guid companyId, string outcome, Guid? documentId)
    {
        await using var owner = new NpgsqlConnection(db.OwnerConnectionString);
        await owner.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("""
            insert into vendor.uploads (id, company_id, document_type, file_name, content_type, declared_size, chunk_size,
                                        chunk_count, outcome, document_id)
            values (@id, @company, 'cr_certificate', 'cr.pdf', 'application/pdf', 1000, 1048576, 1, @outcome, @document)
            """, owner);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("company", companyId);
        command.Parameters.AddWithValue("outcome", outcome);
        command.Parameters.AddWithValue("document", documentId is { } id ? id : DBNull.Value);
        await command.ExecuteNonQueryAsync(Ct);
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
