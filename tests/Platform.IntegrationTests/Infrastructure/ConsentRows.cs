using Npgsql;

namespace Platform.IntegrationTests.Infrastructure;

/// <summary>A consent ledger row as the owner reads it, bypassing row-level security.</summary>
internal sealed record ConsentEventRow(
    Guid Id, Guid RecipientId, string Scope, string Kind, DateOnly? ValidFrom, DateOnly? ValidTo, Guid? RevokesGrantId, string ActorId);

/// <summary>Consent recipients and ledger rows for tests (vendor plan task 6, F-64).</summary>
internal static class ConsentRows
{
    /// <summary>A platform recipient written as the owner, as a migration or the development seed would (V-13).</summary>
    public static async Task<Guid> AddRecipientAsync(string ownerConnectionString, string nameEn, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(ownerConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "insert into vendor.recipients (id, name_ar, name_en) values (@id, 'جهة مستلمة للاختبار', @name)", connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("name", nameEn);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return id;
    }

    /// <summary>
    /// A grant whose period is given in days from today in Riyadh as the database sees it (so a check, which always asks
    /// about the database's today, can find a grant already running or already over). Written as the owner with the
    /// ledger's user triggers disabled for this transaction only, since a real grant can never start in the past.
    /// </summary>
    public static async Task<Guid> InsertGrantAsOwnerAsync(
        string ownerConnectionString, Guid companyId, Guid recipientId, string scope, int fromDays, int toDays, string actorId,
        CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(ownerConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            alter table vendor.consent_events disable trigger user;
            insert into vendor.consent_events (id, company_id, recipient_id, scope, kind, valid_from, valid_to, actor_id)
            values (@id, @company, @recipient, @scope, 'grant',
                    ((now() + interval '3 hours') at time zone 'UTC')::date + @from,
                    ((now() + interval '3 hours') at time zone 'UTC')::date + @to, @actor);
            alter table vendor.consent_events enable trigger user;
            """, connection, transaction);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("company", companyId);
        command.Parameters.AddWithValue("recipient", recipientId);
        command.Parameters.AddWithValue("scope", scope);
        command.Parameters.AddWithValue("from", fromDays);
        command.Parameters.AddWithValue("to", toDays);
        command.Parameters.AddWithValue("actor", actorId);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return id;
    }

    /// <summary>Every ledger row of the company, oldest first.</summary>
    public static async Task<IReadOnlyList<ConsentEventRow>> ForCompanyAsync(string ownerConnectionString, Guid companyId, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(ownerConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            select id, recipient_id, scope, kind, valid_from, valid_to, revokes_grant_id, actor_id
            from vendor.consent_events where company_id = @company order by occurred_at, kind
            """, connection);
        command.Parameters.AddWithValue("company", companyId);
        var rows = new List<ConsentEventRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new ConsentEventRow(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetFieldValue<DateOnly>(4),
                reader.IsDBNull(5) ? null : reader.GetFieldValue<DateOnly>(5),
                reader.IsDBNull(6) ? null : reader.GetGuid(6),
                reader.GetString(7)));
        }

        return rows;
    }
}
