using System.Globalization;
using System.Net.Mail;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Platform.Modules.Audit.Contracts;
using Platform.Modules.Identity.Contracts;
using Platform.Modules.Identity.Keycloak;
using Platform.Shared.Results;
using Platform.Shared.Tenancy;

namespace Platform.Modules.Identity.Members;

/// <summary>
/// Staff invitations for the current tenant (F-06 as narrowed, spec D-4 and 4.2). Keycloak first (find or create the
/// user, add them to the organization), then the invited member row, then the email, so a failed email never loses the
/// member: the admin resends it. A Keycloak user left in the organization by a failed row insert holds no role.
/// </summary>
internal sealed partial class StaffService(
    IDbContextFactory<MembersDbContext> contexts,
    MemberDirectory directory,
    KeycloakAdminClient keycloak,
    IOptions<KeycloakAdminOptions> options,
    ITenantAccessor tenants,
    IAuditWriter audit,
    TimeProvider clock,
    ILogger<StaffService> logger) : IStaffService
{
    internal const int MaxDisplayName = 200;

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
        if (name.Length == 0 || name.Length > MaxDisplayName)
        {
            return Result.Failure<Invitation>(Error.Validation("identity.invalid_display_name", $"Enter the person's name, up to {MaxDisplayName} characters."));
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
        var userId = existing?.Id ?? await keycloak.CreateUserAsync(NewUser(address, name, tenant), cancellationToken);
        await keycloak.AddToOrganizationAsync(tenant.KeycloakOrgAlias, userId, cancellationToken);

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
            // Another admin invited the same address, or the same account under another address, at the same moment.
            return MemberExists();
        }

        var sent = await SendSetupEmailAsync(userId, tenant, cancellationToken);
        await audit.WriteAsync(
            new AuditEntry(actorId, "identity.member_invited", "member", userId, new Dictionary<string, string?>
            {
                ["email"] = address,
                ["roles"] = string.Join(",", row.Roles),
                ["existing_account"] = existing is null ? "false" : "true",
                ["email_sent"] = sent.ToString().ToLowerInvariant(),
            }),
            cancellationToken);
        return Result.Success(new Invitation(ToMember(row), sent, existing is not null));
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

        var sent = await SendSetupEmailAsync(userId, tenant, cancellationToken);
        if (sent == InvitationEmail.NotNeeded)
        {
            return Result.Failure<Invitation>(Error.Refused(
                "identity.invitation_complete", "This person has already set a password and an authenticator; they can sign in now."));
        }

        await audit.WriteAsync(
            new AuditEntry(actorId, "identity.invitation_resent", "member", userId, new Dictionary<string, string?>
            {
                ["email_sent"] = sent.ToString().ToLowerInvariant(),
            }),
            cancellationToken);
        return Result.Success(new Invitation(ToMember(row), sent, ExistingAccount: true));
    }

    /// <summary>
    /// Emails the setup link for what the account still lacks: a password (UPDATE_PASSWORD) and a TOTP authenticator
    /// (CONFIGURE_TOTP). An account with both needs nothing. A failure to send is logged and reported, not thrown, since
    /// the member is already saved and the admin can resend.
    /// </summary>
    private async Task<InvitationEmail> SendSetupEmailAsync(string userId, TenantContext tenant, CancellationToken cancellationToken)
    {
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

            if (actions.Count == 0)
            {
                return InvitationEmail.NotNeeded;
            }

            await keycloak.SendInvitationEmailAsync(userId, actions, options.Value.TenantHome(tenant.Slug), cancellationToken);
            return InvitationEmail.Sent;
        }
        catch (Exception ex) when (ex is KeycloakAdminException or HttpRequestException
            || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // Never ex.Message (N-10): an HttpRequestException can carry the target URL.
            EmailFailed(logger, tenant.Slug, userId, ex.GetType().Name);
            return InvitationEmail.Failed;
        }
    }

    private TenantContext RequireTenant() =>
        tenants.Current ?? throw new InvalidOperationException("Staff belong to a tenant; this request or circuit has none.");

    private static Result<Invitation> MemberExists() =>
        Result.Failure<Invitation>(Error.Conflict("identity.member_exists", "This person is already a member of this tenant."));

    private static string? NormalizeEmail(string? email)
    {
        var trimmed = email?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > 254
            || !MailAddress.TryCreate(trimmed, out var parsed) || !string.Equals(parsed.Address, trimmed, StringComparison.Ordinal))
        {
            return null;
        }

        return trimmed.ToLowerInvariant();
    }

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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Keycloak did not send the invitation email for tenant {Tenant}, user {UserId} ({ErrorType}).")]
    private static partial void EmailFailed(ILogger logger, string tenant, string userId, string errorType);
}
