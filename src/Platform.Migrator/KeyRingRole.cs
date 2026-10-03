namespace Platform.Migrator;

/// <summary>
/// W-24: platform migration 0007 creates <c>erp_key_ring</c>, the only role that may read or add Data Protection keys,
/// without a login. This gives it one, with the password of <c>ConnectionStrings:KeyRing</c> (the same secret the web
/// host's key-ring pool connects with), through <see cref="RoleLogin"/>. The owner needs CREATEROLE (or superuser) for
/// migration 0007 to create the role and for this to give it a login (docs/07 section 4).
/// </summary>
public static class KeyRingRole
{
    public const string Name = "erp_key_ring";

    public static Task EnableLoginAsync(string ownerConnectionString, string keyRingConnectionString, CancellationToken cancellationToken = default) =>
        RoleLogin.EnableAsync(ownerConnectionString, keyRingConnectionString, Name, "KeyRing", cancellationToken);
}
