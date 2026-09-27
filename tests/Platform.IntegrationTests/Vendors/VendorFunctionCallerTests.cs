using System.Globalization;
using Npgsql;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Identity.Contracts;

namespace Platform.IntegrationTests.Vendors;

/// <summary>
/// ADR-0012 point 4 (pentest P-2, P-3, P-6): security-definer functions state who may call them. The worker's functions
/// (the upload cleanup's list, claim and removal, and the retry scan's queue) answer only a session with neither a tenant
/// nor a vendor context, as the worker's jobs run; approving a relationship is for staff only, so it refuses a vendor
/// context and an acting user who is a vendor user.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class VendorFunctionCallerTests(DatabaseFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Contexts => new("tenant", "vendor", "tenant and vendor");

    [Theory]
    [MemberData(nameof(Contexts))]
    public async Task The_worker_functions_answer_nothing_or_refuse_a_session_with_a_context(string context)
    {
        var (companyId, userId) = await VendorAsync("Worker Function Probe");
        var uploadId = await InsertStaleUploadAsOwnerAsync(companyId);
        var documentId = await InsertPendingDocumentAsOwnerAsync(companyId);
        try
        {
            await using var connection = await AppConnectionAsync(
                context.Contains("tenant", StringComparison.Ordinal) ? TestTenants.Acme.TenantId : null,
                context.Contains("vendor", StringComparison.Ordinal) ? companyId : null,
                userId);

            (await CountAsync(connection, "select count(*)::int from vendor.stale_uploads() where id = @value", uploadId)).ShouldBe(0);
            (await CountAsync(connection, "select count(*)::int from vendor.pending_scan_documents(1000) where id = @value", documentId)).ShouldBe(0);
            (await RefusedAsync(connection, "select count(*)::int from vendor.claim_stale_upload(@value)", uploadId)).ShouldBeTrue();
            (await RefusedAsync(connection, "select vendor.remove_stale_upload(@value)", uploadId)).ShouldBeTrue();
        }
        finally
        {
            await DeleteAsOwnerAsync(uploadId, documentId);
        }
    }

    [Fact]
    public async Task The_worker_functions_still_answer_the_worker_session()
    {
        var (companyId, _) = await VendorAsync("Worker Session Control");
        var uploadId = await InsertStaleUploadAsOwnerAsync(companyId);
        var documentId = await InsertPendingDocumentAsOwnerAsync(companyId);
        try
        {
            await using var connection = await AppConnectionAsync(null, null, null);
            (await CountAsync(connection, "select count(*)::int from vendor.stale_uploads() where id = @value", uploadId)).ShouldBe(1);
            (await CountAsync(connection, "select count(*)::int from vendor.pending_scan_documents(1000) where id = @value", documentId)).ShouldBe(1);
            await using var transaction = await connection.BeginTransactionAsync(Ct);
            (await CountAsync(connection, "select count(*)::int from vendor.claim_stale_upload(@value)", uploadId, transaction)).ShouldBe(1);
            await using var remove = new NpgsqlCommand("select vendor.remove_stale_upload(@value)", connection, transaction);
            remove.Parameters.AddWithValue("value", uploadId);
            ((bool)(await remove.ExecuteScalarAsync(Ct))!).ShouldBeTrue();
            await transaction.RollbackAsync(Ct);
        }
        finally
        {
            await DeleteAsOwnerAsync(uploadId, documentId);
        }
    }

    [Fact]
    public async Task Approving_refuses_an_acting_user_who_is_a_vendor_user_even_without_a_vendor_context()
    {
        var (companyId, vendorUser) = await VendorAsync("Approval By Vendor User");

        await using var connection = await AppConnectionAsync(TestTenants.Acme.TenantId, null, vendorUser);
        (await RefusedAsync(connection, "select vendor.approve_relationship(@value)", companyId)).ShouldBeTrue();

        (await VendorRows.RelationshipsAsync(db.OwnerConnectionString, companyId, Ct))[TestTenants.Acme.TenantId].ShouldBe("pending");
    }

    [Fact]
    public async Task Approving_still_works_for_a_staff_session()
    {
        var (companyId, _) = await VendorAsync("Approval By Staff");
        var officer = $"officer-{Guid.NewGuid():N}";
        await MemberRows.InsertAsync(db.AppConnectionString, TestTenants.Acme.TenantId, officer, $"{officer}@acme.test", [TenantRoles.ContractsOfficer], "active", Ct);

        await using var connection = await AppConnectionAsync(TestTenants.Acme.TenantId, null, officer);
        await using var command = new NpgsqlCommand("select vendor.approve_relationship(@company)", connection);
        command.Parameters.AddWithValue("company", companyId);
        ((bool)(await command.ExecuteScalarAsync(Ct))!).ShouldBeTrue();
    }

    private async Task<(Guid CompanyId, string UserId)> VendorAsync(string nameEn)
    {
        var userId = Guid.NewGuid().ToString();
        var companyId = await VendorRows.RegisterAsync(db.AppConnectionString, TestTenants.Acme, userId, VendorRows.NewCrNumber(), nameEn, Ct);
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

    private static async Task<int> CountAsync(NpgsqlConnection connection, string sql, Guid value, NpgsqlTransaction? transaction = null)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("value", value);
        return (int)(await command.ExecuteScalarAsync(Ct))!;
    }

    /// <summary>True when the statement is refused with 42501, inside a rolled-back transaction.</summary>
    private static async Task<bool> RefusedAsync(NpgsqlConnection connection, string sql, Guid value)
    {
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        try
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("value", value);
            await command.ExecuteScalarAsync(Ct);
            return false;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InsufficientPrivilege)
        {
            return true;
        }
        finally
        {
            await transaction.RollbackAsync(Ct);
        }
    }

    private async Task<Guid> InsertStaleUploadAsOwnerAsync(Guid companyId)
    {
        var id = Guid.NewGuid();
        await using var owner = new NpgsqlConnection(db.OwnerConnectionString);
        await owner.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("""
            insert into vendor.uploads (id, company_id, document_type, file_name, content_type, declared_size, chunk_size,
                                        chunk_count, created_at)
            values (@id, @company, 'cr_certificate', 'old.pdf', 'application/pdf', 1000, 1048576, 1, now() - interval '400 days')
            """, owner);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("company", companyId);
        await command.ExecuteNonQueryAsync(Ct);
        return id;
    }

    private async Task<Guid> InsertPendingDocumentAsOwnerAsync(Guid companyId)
    {
        await VendorDocumentRows.InsertAsync(db.OwnerConnectionString, companyId, "cr_certificate", new DateOnly(2030, 1, 1), "pending_scan", isCurrent: false, Ct);
        var rows = await VendorDocumentRows.ForCompanyAsync(db.OwnerConnectionString, companyId, Ct);
        return rows.Single(r => r.ScanStatus == "pending_scan").Id;
    }

    private async Task DeleteAsOwnerAsync(Guid uploadId, Guid documentId)
    {
        await using var owner = new NpgsqlConnection(db.OwnerConnectionString);
        await owner.OpenAsync(CancellationToken.None);
        await using var command = new NpgsqlCommand(
            "delete from vendor.uploads where id = @upload; delete from vendor.documents where id = @document", owner);
        command.Parameters.AddWithValue("upload", uploadId);
        command.Parameters.AddWithValue("document", documentId);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }
}
