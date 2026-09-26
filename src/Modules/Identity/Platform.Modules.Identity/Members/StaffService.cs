using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Platform.Modules.Audit.Contracts;
using Platform.Modules.Identity.Contracts;
using Platform.Modules.Identity.Keycloak;
using Platform.Shared.Email;
using Platform.Shared.Results;
using Platform.Shared.Tenancy;
using Platform.Shared.Text;

namespace Platform.Modules.Identity.Members;

/// <summary>
/// Staff invitations for the current tenant (F-06 as narrowed, spec D-4 and 4.2). Keycloak first (find or create the
/// user, add them to the organization), then the invited member row, then the email, so a failed email never loses the
/// member: the admin resends it. When the row cannot be saved, the organization membership this invitation added is
/// removed again. Every invited person is emailed, whether their account existed or not, and the answer to the admin is
/// the same in both cases, so the page cannot be used to find out who has a WaslaBid account.
/// </summary>
internal sealed partial class StaffService(
    IDbContextFactory<MembersDbContext> contexts,
    MemberDirectory directory,
    KeycloakAdminClient keycloak,
    IOptions<KeycloakAdminOptions> options,
    ITenantAccessor tenants,
    MemberRolesCache cache,
    IAuditWriter audit,
    IEmailSender email,
    InvitationNotice notice,
    TimeProvider clock,
    ILogger<StaffService> logger) : IStaffService
{
    public Task<IReadOnlyList<Member>> ListAsync(CancellationToken cancellationToken = default) => directory.ListAsync(cancellationToken);

    public Task<Result<Member>> SetRolesAsync(
        string userId, IReadOnlyCollection<string> roles, string actorId, CancellationToken cancellationToken = default) =>
        directory.SetRolesAsync(userId, roles, actorId, cancellationToken);

    public async Task<Result<Invitation>> InviteAsync(
        string email, string displayName, IReadOnlyCollection<string> roles, string actorId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        var tenant = RequireTenant();

        var address = NormalizeEmail(email);
        if (address is null)
        {
            return Result.Failure<Invitation>(Error.Validation("identity.invalid_email", "Enter the person's work email address, for example name@company.com."));
        }

        var name = displayName?.Trim() ?? string.Empty;
        if (!DisplayNames.IsValid(name))
        {
            return InvalidName();
        }

        if (roles.Count == 0)
        {
            return Result.Failure<Invitation>(Error.Validation("identity.no_role", "Choose at least one role."));
        }

        var unknown = roles.Where(r => !TenantRoles.All.Contains(r, StringComparer.Ordinal)).ToList();
        if (unknown.Count > 0)
        {
            return Result.Failure<Invitation>(Error.Validation("identity.unknown_role", $"These are not tenant roles: {string.Join(", ", unknown)}."));
        }

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        if (await db.Members.AnyAsync(m => m.Email == address, cancellationToken))
        {
            return MemberExists();
        }

        var existing = await keycloak.FindUserByEmailAsync(address, cancellationToken);
        if (existing is { Enabled: false })
        {
            await audit.WriteAsync(
                new AuditEntry(actorId, "identity.invitation_refused", "member", existing.Id, new Dictionary<string, string?>
                {
                    ["email"] = address,
                    ["reason"] = "account_disabled",
                }),
                cancellationToken);
            return Result.Failure<Invitation>(Error.Refused("identity.account_disabled", "This person cannot be invited. Contact support."));
        }

        string userId;
        if (existing is not null)
        {
            userId = existing.Id;
        }
        else
        {
            try
            {
                userId = await keycloak.CreateUserAsync(NewUser(address, name, tenant), cancellationToken);
            }
            catch (KeycloakAdminException ex) when (ex.Status == HttpStatusCode.BadRequest && ex.DuringUserCreation)
            {
                // Keycloak's user profile refused a value our own check let through; the name is the only free text
                // the create-user request carries, so this is what it refused.
                return InvalidName();
            }
            catch (KeycloakAdminException ex) when (ex.Status == HttpStatusCode.BadRequest)
            {
                // A 400 from an earlier step of the same call (the service account's token request, say) is not the
                // user profile refusing the name; report the generic failure instead of blaming a value that was fine.
                return Result.Failure<Invitation>(Error.Refused("identity.invitation_failed", "The invitation could not be saved. Try again in a moment."));
            }
        }

        var added = await keycloak.AddToOrganizationAsync(tenant.KeycloakOrgAlias, userId, cancellationToken);

        var row = new MemberRecord
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.TenantId,
            UserId = userId,
            Email = address,
            DisplayName = name,
            Roles = [.. TenantRoles.All.Where(r => roles.Contains(r, StringComparer.Ordinal))],
            Status = MemberStatuses.Invited,
            InvitedAt = clock.GetUtcNow(),
        };
        db.Members.Add(row);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Another admin invited the same address, or the same account under another address, at the same moment;
            // their invitation owns the organization membership, so it stays.
            return MemberExists();
        }
        catch (Exception ex) when (ex is DbUpdateException or NpgsqlException or TimeoutException or OperationCanceledException)
        {
            SaveFailed(logger, tenant.Slug, userId, ex.GetType().Name);
            if (added)
            {
                await RemoveMembershipAsync(tenant, userId);
            }

            if (ex is OperationCanceledException)
            {
                throw;
            }

            return Result.Failure<Invitation>(Error.Refused("identity.invitation_failed", "The invitation could not be saved. Try again in a moment."));
        }

        cache.Invalidate(tenant.TenantId, userId);
        var sent = await EmailAsync(row, tenant, actorId, db, cancellationToken);
        await audit.WriteAsync(
            new AuditEntry(actorId, "identity.member_invited", "member", userId, new Dictionary<string, string?>
            {
                ["email"] = address,
                ["roles"] = string.Join(",", row.Roles),
                ["existing_account"] = existing is null ? "false" : "true",
                ["email_kind"] = sent.Kind,
                ["email_sent"] = sent.Email.ToString().ToLowerInvariant(),
            }),
            cancellationToken);
        return Result.Success(new Invitation(ToMember(row), sent.Email));
    }

    public async Task<Result<Invitation>> ResendAsync(string userId, string actorId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        var tenant = RequireTenant();

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var row = await db.Members.AsNoTracking().SingleOrDefaultAsync(m => m.UserId == userId, cancellationToken);
        if (row is null)
        {
            return Result.Failure<Invitation>(Error.NotFound("identity.member_not_found", "The user is not a member of this tenant."));
        }

        if (row.Status != MemberStatuses.Invited)
        {
            return Result.Failure<Invitation>(Error.Refused("identity.member_active", "This member has already signed in, so there is no invitation to resend."));
        }

        var sent = await EmailAsync(row, tenant, actorId, db, cancellationToken);
        await audit.WriteAsync(
            new AuditEntry(actorId, "identity.invitation_resent", "member", userId, new Dictionary<string, string?>
            {
                ["email_kind"] = sent.Kind,
                ["email_sent"] = sent.Email.ToString().ToLowerInvariant(),
            }),
            cancellationToken);
        return Result.Success(new Invitation(ToMember(row), sent.Email));
    }

    /// <summary>
    /// Emails the person. An account that still lacks a password (UPDATE_PASSWORD) or a TOTP authenticator
    /// (CONFIGURE_TOTP) gets Keycloak's setup link for what it lacks ("setup"); an account with both gets our own short
    /// notice that the tenant added them ("notice"). A failure to send is logged and reported, not thrown, since the member
    /// is already saved and the admin can resend.
    /// </summary>
    private async Task<(InvitationEmail Email, string Kind)> EmailAsync(
        MemberRecord row, TenantContext tenant, string actorId, MembersDbContext db, CancellationToken cancellationToken)
    {
        var userId = row.UserId!;
        var kind = "setup";
        try
        {
            var credentials = await keycloak.CredentialTypesAsync(userId, cancellationToken);
            var actions = new List<string>(2);
            if (!credentials.Contains("password"))
            {
                actions.Add("UPDATE_PASSWORD");
            }

            if (!credentials.Contains("otp"))
            {
                actions.Add("CONFIGURE_TOTP");
            }

            if (actions.Count > 0)
            {
                await keycloak.SendInvitationEmailAsync(userId, actions, options.Value.TenantHome(tenant.Slug), cancellationToken);
                return (InvitationEmail.Sent, kind);
            }

            kind = "notice";
            var inviter = await db.Members.AsNoTracking()
                .Where(m => m.UserId == actorId).Select(m => m.DisplayName).SingleOrDefaultAsync(cancellationToken);
            await email.SendAsync(
                notice.Write(row.Email, inviter, tenant.Branding.PortalName, row.Roles, options.Value.TenantHome(tenant.Slug), tenant.DefaultCulture),
                cancellationToken);
            return (InvitationEmail.Sent, kind);
        }
        catch (Exception ex) when (ex is KeycloakAdminException or HttpRequestException or EmailDeliveryException
            || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // Never ex.Message (N-10): an HttpRequestException can carry the target URL.
            EmailFailed(logger, tenant.Slug, userId, kind, ex.GetType().Name);
            return (InvitationEmail.Failed, kind);
        }
    }

    // Best effort: the invitation already failed; a membership left behind holds no role (no member row) and is logged.
    private async Task RemoveMembershipAsync(TenantContext tenant, string userId)
    {
        try
        {
            await keycloak.RemoveFromOrganizationAsync(tenant.KeycloakOrgAlias, userId, CancellationToken.None);
        }
        catch (Exception ex) when (ex is KeycloakAdminException or HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            CompensationFailed(logger, tenant.Slug, userId, ex.GetType().Name);
        }
    }

    private TenantContext RequireTenant() =>
        tenants.Current ?? throw new InvalidOperationException("Staff belong to a tenant; this request or circuit has none.");

    private static Result<Invitation> MemberExists() =>
        Result.Failure<Invitation>(Error.Conflict("identity.member_exists", "This person is already a member of this tenant."));

    private static Result<Invitation> InvalidName() =>
        Result.Failure<Invitation>(Error.Validation(
            "identity.invalid_display_name",
            $"Enter the person's full name, up to {DisplayNames.MaxLength} characters, using letters, spaces, apostrophes, hyphens and periods."));

    /// <summary>
    /// A work email address, stricter than <see cref="System.Net.Mail.MailAddress"/>: ASCII only, an unquoted local
    /// part, a domain of at least two letter/digit/hyphen labels (no label starting or ending with a hyphen, no
    /// trailing dot, no dotted-quad IP literal), and at most 254 characters overall. The address is shown next to the
    /// name on <c>/admin/staff</c> and in emails, so the bidi-override and zero-width characters the name refuses must
    /// not enter here either, though an ASCII-only address can never carry them.
    /// </summary>
    private static string? NormalizeEmail(string? email)
    {
        var trimmed = email?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > 254 || TextSafety.HasInvisibleOrBidiControl(trimmed))
        {
            return null;
        }

        foreach (var c in trimmed)
        {
            if (c > '\u007F')
            {
                return null;
            }
        }

        var at = trimmed.IndexOf('@');
        if (at <= 0 || at != trimmed.LastIndexOf('@') || at == trimmed.Length - 1)
        {
            return null;
        }

        var localPart = trimmed[..at];
        var domain = trimmed[(at + 1)..];
        if (localPart[0] == '"' || !LocalPart().IsMatch(localPart))
        {
            return null;
        }

        var labels = domain.Split('.');
        if (labels.Length < 2 || labels.Any(label => !DomainLabel().IsMatch(label))
            || labels.All(label => label.All(char.IsAsciiDigit)))
        {
            // Fewer than two labels (no dot, "localhost"), an empty label (a trailing or doubled dot), a label
            // starting or ending with a hyphen, or every label numeric (a dotted-quad IP literal) is refused.
            return null;
        }

        return trimmed.ToLowerInvariant();
    }

    [GeneratedRegex(@"^[A-Za-z0-9!#$%&'*+/=?^_`{|}~-]+(\.[A-Za-z0-9!#$%&'*+/=?^_`{|}~-]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex LocalPart();

    [GeneratedRegex(@"^[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex DomainLabel();

    // Keycloak keeps first and last name apart; the last word is the last name ("Sara Al Ahmed" -> "Sara Al", "Ahmed").
    private static NewKeycloakUser NewUser(string email, string name, TenantContext tenant)
    {
        var split = name.LastIndexOf(' ');
        var (first, last) = split > 0 ? (name[..split].Trim(), name[(split + 1)..]) : (name, string.Empty);
        var locale = CultureInfo.GetCultureInfo(tenant.DefaultCulture).TwoLetterISOLanguageName;
        return new NewKeycloakUser(email, first, last, locale);
    }

    private static Member ToMember(MemberRecord row) => new(
        row.UserId, row.Email, row.DisplayName, row.Roles, MemberStatus.Invited, row.InvitedAt, row.ActivatedAt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The invitation email ({Kind}) was not sent for tenant {Tenant}, user {UserId} ({ErrorType}).")]
    private static partial void EmailFailed(ILogger logger, string tenant, string userId, string kind, string errorType);

    [LoggerMessage(Level = LogLevel.Error, Message = "The invited member row was not saved for tenant {Tenant}, user {UserId} ({ErrorType}).")]
    private static partial void SaveFailed(ILogger logger, string tenant, string userId, string errorType);

    [LoggerMessage(Level = LogLevel.Error, Message = "The organization membership added for a failed invitation was not removed for tenant {Tenant}, user {UserId} ({ErrorType}).")]
    private static partial void CompensationFailed(ILogger logger, string tenant, string userId, string errorType);
}
