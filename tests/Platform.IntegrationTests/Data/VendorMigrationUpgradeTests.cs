using System.Security.Cryptography;
using Npgsql;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Audit;
using Platform.Modules.Identity;
using Platform.Modules.Operations;
using Platform.Modules.Tenancy;
using Platform.Modules.Vendors;
using Platform.Modules.Workflow;
using Platform.Shared;
using Platform.Shared.Data;

namespace Platform.IntegrationTests.Data;

/// <summary>
/// The vendor upload migrations after 0005 (the object key check and the upload's document reference of 0006, the
/// outcome and cleanup changes of 0007) apply to a database that already holds uploads and documents written by the
/// 0005 code, in its real key formats. A fresh database in the same container, so the shared one is not touched.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class VendorMigrationUpgradeTests(DatabaseFixture db)
{
    private const string LastBefore = "0005_vendors_uploads.sql";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Uploads_and_documents_written_under_0005_survive_the_later_vendor_migrations()
    {
        var database = $"vendors_upgrade_{Guid.NewGuid():N}";
        await ExecuteAsync(db.OwnerConnectionString, $"create database {database}");
        var connectionString = new NpgsqlConnectionStringBuilder(db.OwnerConnectionString) { Database = database }.ConnectionString;
        try
        {
            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync(Ct);
                await ExecuteAsync(connection, "create extension if not exists vector; create extension if not exists pgcrypto; create extension if not exists unaccent;");
                await SharedModule.MigrateAsync(connection, Ct);
                await AuditModule.MigrateAsync(connection, Ct);
                await TenancyModule.MigrateAsync(connection, Ct);
                await IdentityModule.MigrateAsync(connection, Ct);
                await WorkflowModule.MigrateAsync(connection, Ct);
                await OperationsModule.MigrateAsync(connection, Ct);
                var upTo0005 = await VendorScriptsAsync(name => string.CompareOrdinal(name, LastBefore) <= 0);
                upTo0005.Select(s => s.Script).ShouldContain(LastBefore);
                await SqlMigrator.ApplyAsync(connection, "vendors", upTo0005, Ct);

                var companyId = Guid.NewGuid();
                var clean = Guid.NewGuid();
                var pending = Guid.NewGuid();
                await ExecuteAsync(connection, $"""
                    insert into vendor.companies (id, cr_number, name_ar, name_en, vat_number, contact_name, contact_email)
                    values ('{companyId}', '7001234567', 'شركة', 'Company', '300000000000003', 'Contact', 'c@vendor.test');
                    insert into vendor.documents (id, company_id, type, expires_on, object_key, sha256, scan_status, is_current)
                    values ('{clean}', '{companyId}', 'cr_certificate', date '2030-01-01', 'vendors/{companyId:D}/documents/{clean:D}', '{Sha(clean)}', 'clean', true),
                           ('{pending}', '{companyId}', 'vat_certificate', date '2030-01-01', 'vendors/{companyId:D}/quarantine/{pending:D}', '{Sha(pending)}', 'pending_scan', false);
                    insert into vendor.uploads (id, company_id, document_type, file_name, content_type, declared_size, chunk_size, chunk_count,
                                                received_chunks, outcome, document_id, sha256)
                    values ('{clean}', '{companyId}', 'cr_certificate', 'cr.pdf', 'application/pdf', 1000, 1048576, 1, array[0], 'clean', '{clean}', '{Sha(clean)}'),
                           ('{pending}', '{companyId}', 'vat_certificate', 'vat.pdf', 'application/pdf', 1000, 1048576, 1, array[0], 'pending_scan', '{pending}', '{Sha(pending)}'),
                           ('{Guid.NewGuid()}', '{companyId}', 'cr_certificate', 'x.pdf', 'application/pdf', 1000, 1048576, 1, array[0], 'infected', null, null),
                           ('{Guid.NewGuid()}', '{companyId}', 'cr_certificate', 'open.pdf', 'application/pdf', 1000, 1048576, 1, array[]::integer[], null, null, null);
                    """);

                var applied = await VendorsModule.MigrateAsync(connection, Ct);

                applied.ShouldBe(["0006_vendors_uploads_hardening.sql", "0007_vendors_uploads_audit_bounds.sql", "0008_vendors_scan_queue.sql", "0009_vendors_scan_canary.sql", "0010_vendors_directory.sql", "0011_vendors_staff_functions_no_vendor.sql", "0012_vendors_consent_check.sql", "0013_vendors_relationships_vendor_policy.sql"]);
                (await ScalarAsync<long>(connection, $"select count(*) from vendor.uploads where company_id = '{companyId}'")).ShouldBe(4);
                (await ScalarAsync<long>(connection, $"select count(*) from vendor.uploads where company_id = '{companyId}' and last_chunk_at is not null")).ShouldBe(0);
                (await ScalarAsync<long>(connection, $"select count(*) from vendor.documents where company_id = '{companyId}'")).ShouldBe(2);
                (await ScalarAsync<long>(connection, $"select count(*) from vendor.uploads where company_id = '{companyId}' and outcome = 'clean' and document_id = '{clean}'")).ShouldBe(1);
            }

            // The application role calls the recreated cleanup function and gets its new columns.
            var app = new NpgsqlConnectionStringBuilder(db.AppConnectionString) { Database = database }.ConnectionString;
            await using (var connection = new NpgsqlConnection(app))
            {
                await connection.OpenAsync(Ct);
                await using var command = new NpgsqlCommand("select * from vendor.stale_uploads()", connection);
                await using var reader = await command.ExecuteReaderAsync(Ct);
                Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ShouldBe(["id", "chunk_count", "company_id", "outcome"]);
                (await reader.ReadAsync(Ct)).ShouldBeFalse("every upload is younger than the cleanup margin");
            }
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteAsync(db.OwnerConnectionString, $"drop database if exists {database} with (force)");
        }
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql)
    {
#pragma warning disable CA2100 // Test SQL built from generated ids and constants only.
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }

    private static string Sha(Guid id) => Convert.ToHexStringLower(SHA256.HashData(id.ToByteArray()));

    private static async Task<List<(string Script, string Sql)>> VendorScriptsAsync(Func<string, bool> include)
    {
        const string prefix = "Migrations.";
        var assembly = typeof(VendorsModule).Assembly;
        var scripts = new List<(string Script, string Sql)>();
        foreach (var resource in assembly.GetManifestResourceNames()
                     .Where(n => n.StartsWith(prefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
                     .Order(StringComparer.Ordinal))
        {
            var script = resource[prefix.Length..];
            if (!include(script))
            {
                continue;
            }

            await using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            scripts.Add((script, await reader.ReadToEndAsync(Ct)));
        }

        return scripts;
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await ExecuteAsync(connection, sql);
    }

#pragma warning disable CA2100 // Test SQL built from generated ids and constants only.
    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }
#pragma warning restore CA2100
}
