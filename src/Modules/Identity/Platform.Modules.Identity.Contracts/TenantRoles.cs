namespace Platform.Modules.Identity.Contracts;

/// <summary>
/// The tenant roles of the MVP (F-07 as narrowed by docs/05 row 4; the Auditor role is P1). Held in our own table per
/// tenant (spec D-3), never in Keycloak, and checked through the policies named in <see cref="TenantPolicies"/>.
/// </summary>
public static class TenantRoles
{
    public const string TenantAdmin = "tenant-admin";
    public const string ContractsOfficer = "contracts-officer";
    public const string TechnicalEvaluator = "technical-evaluator";
    public const string FinanceApprover = "finance-approver";

    public static IReadOnlyList<string> All { get; } = [TenantAdmin, ContractsOfficer, TechnicalEvaluator, FinanceApprover];
}

/// <summary>
/// Authorization policies for the tenant roles: each requires the same-tenant check and the role in the host tenant.
/// Use these names (<c>[Authorize(Policy = TenantPolicies.TenantAdmin)]</c>), never <c>Roles =</c>, which would read
/// role claims from any identity on the principal, including ones a token could carry.
/// </summary>
public static class TenantPolicies
{
    public const string TenantAdmin = "TenantAdmin";
    public const string ContractsOfficer = "ContractsOfficer";
    public const string TechnicalEvaluator = "TechnicalEvaluator";
    public const string FinanceApprover = "FinanceApprover";
}
