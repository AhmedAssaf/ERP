using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Platform.Modules.Vendors.Registration;

/// <summary>
/// The value the duplicate-CR audit stores instead of the CR number (V-6): HMAC-SHA256 under <c>Vendors:CrAuditKey</c>,
/// in lower-case hex. A plain hash of a ten-digit number is reversed by hashing all ten billion of them; without the key
/// the stored value tells nothing, while the same number still gives the same value, so repeated attempts can be matched.
/// </summary>
internal sealed class CrNumberAudit(IOptions<VendorsOptions> options)
{
    public const string SubjectType = "cr_number_hmac";

    private readonly Lazy<byte[]> _key = new(() =>
        VendorsOptions.DecodeCrAuditKey(options.Value.CrAuditKey) ?? throw new InvalidOperationException(VendorsOptions.CrAuditKeyProblem));

    public string Hmac(string crNumber)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(crNumber);
        return Convert.ToHexStringLower(HMACSHA256.HashData(_key.Value, Encoding.UTF8.GetBytes(crNumber)));
    }
}
