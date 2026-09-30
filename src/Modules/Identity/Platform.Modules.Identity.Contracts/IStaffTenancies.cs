namespace Platform.Modules.Identity.Contracts;

/// <summary>
/// Which tenants a user is staff of, across tenants, for the platform console only (no tenant or vendor context, an acting
/// user; the database refuses any other session, identity migration 0002). A staff member counts from the invitation on
/// (<see cref="MemberStatus.Invited"/> or <see cref="MemberStatus.Active"/>): the invitation already added them to the
/// tenant's Keycloak organization, so that membership is the tenant's decision. W-33 uses it so an uphold's identity
/// provider update never takes a tenant's organization from its own staff.
/// </summary>
public interface IStaffTenancies
{
    /// <summary>The ids of the tenants with a member row (invited or active) for the user; empty when there is none.</summary>
    Task<IReadOnlySet<Guid>> TenantsOfAsync(string userId, CancellationToken cancellationToken = default);
}
