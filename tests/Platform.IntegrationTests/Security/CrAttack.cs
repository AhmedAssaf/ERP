using System.Diagnostics;
using Npgsql;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Vendors.Contracts;
using Platform.Shared.Tenancy;

namespace Platform.IntegrationTests.Security;

/// <summary>
/// Set-up shared by the W-33 attack tests (pentest of the CR ownership check and dispute path): companies, staff,
/// disputes, and a wait for a database session blocked on a lock, so a race can be staged step by step.
/// </summary>
internal static class CrAttack
{
    public const string Statement = "We are the owners named on the CR certificate; someone else registered our company.";

    public const string OfficerNote = "The CR certificate names the registering person as the owner.";

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static CrDisputeRequest Request(string crNumber) =>
        new(crNumber, Statement, VendorPrivacyNotice.CurrentVersion, VendorPrivacyNotice.English);

    /// <summary>A company registered on Acme by a fresh user, with a current clean CR certificate when asked.</summary>
    public static async Task<(Guid CompanyId, string UserId, string CrNumber)> VendorAsync(DatabaseFixture db, string nameEn, bool certificate = true)
    {
        var userId = Guid.NewGuid().ToString();
        var crNumber = VendorRows.NewCrNumber();
        var companyId = await VendorRows.RegisterAsync(db.AppConnectionString, TestTenants.Acme, userId, crNumber, nameEn, Ct);
        if (certificate)
        {
            await VendorDocumentRows.InsertAsync(
                db.OwnerConnectionString, companyId, VendorDocumentTypes.CrCertificate, new DateOnly(2031, 1, 1), "clean", isCurrent: true, Ct);
        }

        return (companyId, userId, crNumber);
    }

    public static async Task<string> StaffAsync(DatabaseFixture db, TenantContext tenant, string role)
    {
        var userId = $"{role}-{Guid.NewGuid():N}";
        await MemberRows.InsertAsync(db.AppConnectionString, tenant.TenantId, userId, $"{userId}@{tenant.Slug}.test", [role], "active", Ct);
        return userId;
    }

    public static async Task<object?> OwnerScalarAsync(DatabaseFixture db, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(db.OwnerConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return await command.ExecuteScalarAsync(Ct);
    }

    public static async Task<string> RelationshipStatusAsync(DatabaseFixture db, Guid tenantId, Guid companyId) =>
        (await VendorRows.RelationshipsAsync(db.OwnerConnectionString, companyId, Ct))[tenantId];

    public static async Task<int> DisputeCountAsync(DatabaseFixture db, Guid companyId) =>
        (int)(await OwnerScalarAsync(db, "select count(*)::int from vendor.cr_disputes where company_id = @company", ("company", companyId)))!;

    public static async Task<int> OpenDisputesOfAsync(DatabaseFixture db, string claimant) =>
        (int)(await OwnerScalarAsync(
            db, "select count(*)::int from vendor.cr_disputes where claimant_user_id = @claimant and status = 'open'", ("claimant", claimant)))!;

    /// <summary>
    /// Waits until another session of the test database waits on a lock while running a statement that names
    /// <paramref name="function"/>, or until <paramref name="task"/> finished (it then was not blocked at all).
    /// </summary>
    public static async Task WaitUntilBlockedOrDoneAsync(DatabaseFixture db, string function, Task task)
    {
        await using var connection = new NpgsqlConnection(db.OwnerConnectionString);
        await connection.OpenAsync(Ct);
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(20))
        {
            if (task.IsCompleted)
            {
                return;
            }

            await using var command = new NpgsqlCommand("""
                select count(*)::int from pg_stat_activity
                where datname = current_database() and pid <> pg_backend_pid()
                  and wait_event_type = 'Lock' and position(@function in query) > 0
                """, connection);
            command.Parameters.AddWithValue("function", function);
            if ((int)(await command.ExecuteScalarAsync(Ct))! > 0)
            {
                return;
            }

            await Task.Delay(25, Ct);
        }

        throw new TimeoutException($"No session waited on a lock in {function}, and the call did not finish.");
    }
}
