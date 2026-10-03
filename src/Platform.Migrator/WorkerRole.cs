using Platform.Shared.Data;

namespace Platform.Migrator;

/// <summary>
/// W-36: platform migration 0008 creates <c>erp_worker</c>, the worker's own role, without a login. This gives it one, with
/// the password of <c>ConnectionStrings:Worker</c> (the same secret the worker connects with), through
/// <see cref="RoleLogin"/>, exactly as <see cref="KeyRingRole"/> does for the key ring.
/// </summary>
public static class WorkerRole
{
    public const string Name = WorkerDatabase.RoleName;

    public const string ConnectionStringName = WorkerDatabase.ConnectionStringName;

    public static Task EnableLoginAsync(string ownerConnectionString, string workerConnectionString, CancellationToken cancellationToken = default) =>
        RoleLogin.EnableAsync(ownerConnectionString, workerConnectionString, Name, ConnectionStringName, cancellationToken);
}
