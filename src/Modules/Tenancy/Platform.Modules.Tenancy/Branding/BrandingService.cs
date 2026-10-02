using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Platform.Modules.Audit.Contracts;
using Platform.Modules.Tenancy.Contracts;
using Platform.Shared.Branding;
using Platform.Shared.Results;
using Platform.Shared.Storage;
using Platform.Shared.Tenancy;
using Platform.Shared.Text;

namespace Platform.Modules.Tenancy.Branding;

/// <summary>
/// <see cref="IBrandingService"/> over <c>tenancy.update_branding</c> (spec D-9): the tenant id is set on the connection
/// for the transaction only, and the function updates only that tenant's row. After a save the tenant's hosts are dropped
/// from this process's host cache, so the next request shows the new branding; other web instances follow within the
/// cache's 60 seconds. Logos go to object storage under <c>tenants/{tenantId}/branding/</c>, named by their SHA-256, so
/// the URL changes with the content and can be cached for ever.
/// <para>
/// Who acts is the request or circuit's acting user (<see cref="IActingUserAccessor"/>), which the database reads from
/// the session (vendor spec section 2); the <c>actorId</c> a caller passes must be that same user, and anything else is a
/// defect. The function refuses anyone but an active tenant admin of the tenant, and any vendor user or vendor session
/// (tenancy migrations 0007 and 0008); that refusal is <see cref="BrandingErrors.NotAllowed"/> on both the name and colour
/// save and the logo upload.
/// </para>
/// </summary>
internal sealed partial class BrandingService(
    [FromKeyedServices(TenancyModule.DataSourceKey)] NpgsqlDataSource dataSource,
    ITenantAccessor tenants,
    IVendorAccessor vendors,
    IActingUserAccessor actingUser,
    ITenantDirectory directory,
    IObjectStorage storage,
    IAuditWriter audit,
    ILogger<BrandingService> logger) : IBrandingService
{
    public const int MaxPortalName = 100;
    public const double MinimumContrast = 4.5;
    private const string LogoPathPrefix = "/branding/logo/";

    public Task<TenantBranding> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(RequireTenant().Branding);

    public async Task<Result<BrandingSaved>> SaveAsync(
        string portalName, string primaryColor, string actorId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        var tenant = RequireTenant();
        RequireActingUser(actorId);

        var name = portalName?.Trim() ?? string.Empty;
        if (name.Length == 0 || name.Length > MaxPortalName || name.Any(char.IsControl) || TextSafety.HasInvisibleOrBidiControl(name))
        {
            // The portal name is shown to every vendor on the tenant's pages and goes into invitation emails (F-02,
            // F-06); a bidi-override or zero-width character could make it display as something else, so it is refused
            // for the same reason a staff display name is (Platform.Shared.Text.TextSafety).
            return Result.Failure<BrandingSaved>(Error.Validation(
                BrandingErrors.InvalidPortalName, $"Enter a portal name of 1 to {MaxPortalName} characters."));
        }

        var requested = primaryColor?.Trim() ?? string.Empty;
        if (!HexColor().IsMatch(requested))
        {
            return Result.Failure<BrandingSaved>(Error.Validation(
                BrandingErrors.InvalidColor, "Enter the colour as # and six hexadecimal digits, for example #0F766E."));
        }

        requested = requested.ToUpperInvariant();
        var stored = ColorContrast.EnsureContrast(requested, MinimumContrast);
        if (await UpdateAsync(tenant, name, stored, null, cancellationToken) is not { } saved)
        {
            return Result.Failure<BrandingSaved>(NotAllowed());
        }

        await audit.WriteAsync(
            new AuditEntry(actorId, "tenancy.branding_changed", "tenant", tenant.TenantId.ToString(), new Dictionary<string, string?>
            {
                ["portal_name_before"] = tenant.Branding.PortalName,
                ["portal_name"] = saved.PortalName,
                ["primary_color_before"] = tenant.Branding.PrimaryColor,
                ["primary_color_requested"] = requested,
                ["primary_color"] = saved.PrimaryColor,
            }),
            cancellationToken);
        return Result.Success(new BrandingSaved(saved, requested));
    }

    public async Task<Result<TenantBranding>> SaveLogoAsync(
        Stream content, string? contentType, string actorId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        var tenant = RequireTenant();
        RequireActingUser(actorId);
        if (vendors.Current is not null)
        {
            // The database refuses a vendor session too; answering first keeps the file out of storage.
            return Result.Failure<TenantBranding>(NotAllowed());
        }

        var bytes = await ReadAtMostAsync(content, LogoImage.MaxBytes + 1, cancellationToken);
        var processed = LogoImage.Process(bytes, contentType);
        if (!processed.IsSuccess)
        {
            return Result.Failure<TenantBranding>(processed.Error);
        }

        var logo = processed.Value;
        var hash = Convert.ToHexStringLower(SHA256.HashData(logo.Png));
        var key = LogoKey(tenant.TenantId, hash);
        // The file is named by its content, so the same logo may already be stored, and may be the one in use: only an
        // object this call created is ever taken out again (W-38).
        bool existed;
        await using (var present = await storage.OpenAsync(key, cancellationToken))
        {
            existed = present is not null;
        }

        await storage.PutAsync(key, logo.Png, "image/png", cancellationToken);
        TenantBranding? saved = null;
        try
        {
            saved = await UpdateAsync(tenant, null, null, LogoPathPrefix + hash + ".png", cancellationToken);
        }
        finally
        {
            if (saved is null && !existed)
            {
                // Refused or failed after the upload: leave storage as it was. Best effort; a failure is logged with ids
                // only and never replaces the caller's result or exception (N-10).
                await DeleteUnreferencedLogoAsync(tenant.TenantId, hash);
            }
        }

        if (saved is null)
        {
            return Result.Failure<TenantBranding>(NotAllowed());
        }

        await audit.WriteAsync(
            new AuditEntry(actorId, "tenancy.branding_changed", "tenant", tenant.TenantId.ToString(), new Dictionary<string, string?>
            {
                ["logo_url_before"] = tenant.Branding.LogoUrl,
                ["logo_url"] = saved.LogoUrl,
                ["logo_size"] = string.Create(CultureInfo.InvariantCulture, $"{logo.Width}x{logo.Height}"),
                ["logo_bytes"] = logo.Png.Length.ToString(CultureInfo.InvariantCulture),
            }),
            cancellationToken);
        return Result.Success(saved);
    }

    private async Task DeleteUnreferencedLogoAsync(Guid tenantId, string hash)
    {
        try
        {
            // Not the caller's token: a cancelled request is one reason the save did not happen.
            await storage.DeleteAsync(LogoKey(tenantId, hash), CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Branding logo cleanup failed ({ExceptionType}) for tenant {TenantId}, logo {LogoHash}; the object stays unreferenced.", ex.GetType().Name, tenantId, hash);
        }
    }

    public async Task<Stream?> OpenLogoAsync(string hash, CancellationToken cancellationToken = default)
    {
        var tenant = RequireTenant();
        if (hash is null || !Sha256Hex().IsMatch(hash))
        {
            return null;
        }

        var stored = await storage.OpenAsync(LogoKey(tenant.TenantId, hash), cancellationToken);
        return stored?.Content;
    }

    internal static string LogoKey(Guid tenantId, string hash) => $"tenants/{tenantId:D}/branding/logo-{hash}.png";

    /// <summary>The saved branding, or null when the database refused the acting user (42501).</summary>
    private async Task<TenantBranding?> UpdateAsync(
        TenantContext tenant, string? portalName, string? primaryColor, string? logoUrl, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        // The function checks who acts (tenancy migrations 0007 and 0008): an active tenant admin of this tenant, never a
        // vendor user or session. This connection does not go through the interceptor, so it carries the same settings
        // itself, from the same accessors.
        await using (var scope = new NpgsqlCommand(
            "select set_config('app.tenant_id', $1, true), set_config('app.user_id', $2, true), set_config('app.vendor_company_id', $3, true)",
            connection, transaction))
        {
            scope.Parameters.Add(new NpgsqlParameter { Value = tenant.TenantId.ToString("D") });
            scope.Parameters.Add(new NpgsqlParameter { Value = actingUser.UserId ?? string.Empty });
            scope.Parameters.Add(new NpgsqlParameter { Value = vendors.Current?.CompanyId.ToString("D") ?? string.Empty });
            await scope.ExecuteNonQueryAsync(cancellationToken);
        }

        TenantBranding saved;
        string[] hosts;
        await using (var update = new NpgsqlCommand(
            "select portal_name, primary_color, logo_url, hosts from tenancy.update_branding($1, $2, $3)", connection, transaction))
        {
            update.Parameters.Add(new NpgsqlParameter { Value = (object?)portalName ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
            update.Parameters.Add(new NpgsqlParameter { Value = (object?)primaryColor ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
            update.Parameters.Add(new NpgsqlParameter { Value = (object?)logoUrl ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
            try
            {
                await using var reader = await update.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                {
                    throw new InvalidOperationException($"Tenant '{tenant.Slug}' has no row to brand.");
                }

                saved = new TenantBranding(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2));
                hosts = reader.GetFieldValue<string[]>(3);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InsufficientPrivilege)
            {
                // Not an active tenant admin (any more), or a vendor user: the caller answers NotAllowed. The transaction
                // is rolled back when it is disposed.
                return null;
            }
        }

        await transaction.CommitAsync(cancellationToken);
        foreach (var host in hosts)
        {
            directory.Invalidate(host);
        }

        return saved;
    }

    private static async Task<byte[]> ReadAtMostAsync(Stream content, int limit, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while (buffer.Length < limit && (read = await content.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, limit - buffer.Length)), cancellationToken)) > 0)
        {
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private void RequireActingUser(string actorId)
    {
        if (!string.Equals(actingUser.UserId, actorId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A tenant is branded by the acting user of the request or circuit.");
        }
    }

    private static Error NotAllowed() =>
        Error.Refused(BrandingErrors.NotAllowed, "Only an active tenant admin of this tenant can change its branding.");

    private TenantContext RequireTenant() =>
        tenants.Current ?? throw new InvalidOperationException("Branding belongs to a tenant; this request or circuit has none.");

    [GeneratedRegex(@"^#[0-9A-Fa-f]{6}\z", RegexOptions.CultureInvariant)]
    private static partial Regex HexColor();

    [GeneratedRegex(@"^[a-f0-9]{64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Hex();
}
