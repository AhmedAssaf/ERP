using Npgsql;
using Platform.IntegrationTests.Infrastructure;

namespace Platform.IntegrationTests.Security;

/// <summary>
/// W-36 pentest (2026-10-03), defence in depth behind the worker role: every <c>SECURITY DEFINER</c> function in the
/// module schemas runs as its owner (the migration owner, a superuser), so an unqualified name it resolves is an escalation
/// path. The application role (<c>erp_app</c>, the web host) owns no object and has no <c>CREATE</c> on any schema, but it
/// does hold the default <c>TEMP</c> right, so it can create objects in <c>pg_temp</c>. The only thing that stops a
/// <c>pg_temp</c> object from shadowing a call inside a definer function is the function's pinned <c>search_path</c>: this
/// test proves every definer function pins one, that <c>pg_temp</c> is never the first entry (so it cannot win a lookup),
/// that <c>public</c> is never on the path, and that no runtime role owns such a function. A regression here would reopen
/// the search-path hijack the worker-only grants are meant to close.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class DefinerSearchPathTests(DatabaseFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] ModuleSchemas = ["identity", "tenancy", "vendor", "ops", "platform"];

    [Fact]
    public async Task Every_security_definer_function_pins_a_safe_search_path_and_is_owned_by_no_runtime_role()
    {
        var runtimeRoles = new[] { "erp_app", "erp_worker", "erp_key_ring" };

        await using var owner = new NpgsqlConnection(db.OwnerConnectionString);
        await owner.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("""
            select n.nspname || '.' || p.proname as fn, p.proconfig, pg_get_userbyid(p.proowner) as owner
            from pg_proc p
            join pg_namespace n on n.oid = p.pronamespace
            where p.prosecdef and n.nspname = any(@schemas)
            order by fn
            """, owner);
        command.Parameters.AddWithValue("schemas", ModuleSchemas);

        var functions = new List<(string Name, string[]? Config, string Owner)>();
        await using (var reader = await command.ExecuteReaderAsync(Ct))
        {
            while (await reader.ReadAsync(Ct))
            {
                functions.Add((
                    reader.GetString(0),
                    await reader.IsDBNullAsync(1, Ct) ? null : reader.GetFieldValue<string[]>(1),
                    reader.GetString(2)));
            }
        }

        functions.ShouldNotBeEmpty("the module schemas have security-definer functions to check");

        var offenders = new List<string>();
        foreach (var (name, config, owner2) in functions)
        {
            if (runtimeRoles.Contains(owner2, StringComparer.Ordinal))
            {
                offenders.Add($"{name}: owned by the runtime role {owner2}, which could ALTER it");
                continue;
            }

            var searchPath = config?.FirstOrDefault(c => c.StartsWith("search_path=", StringComparison.Ordinal));
            if (searchPath is null)
            {
                offenders.Add($"{name}: no search_path pinned, so the caller's search_path (and pg_temp) decides name resolution");
                continue;
            }

            var entries = searchPath["search_path=".Length..]
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (entries.Length == 0)
            {
                offenders.Add($"{name}: empty search_path");
                continue;
            }

            if (string.Equals(entries[0], "pg_temp", StringComparison.Ordinal))
            {
                offenders.Add($"{name}: pg_temp is first on the search_path, so a temp object the app role creates can shadow a call");
            }

            if (entries.Contains("public", StringComparer.Ordinal))
            {
                offenders.Add($"{name}: public is on the search_path");
            }
        }

        offenders.ShouldBeEmpty(string.Join("; ", offenders));
    }
}
