using System.Security.Cryptography;
using System.Text;
using Platform.Shared.Data;

namespace Platform.UnitTests.Shared;

/// <summary>
/// W-24 (N-10): the key ring role's password reaches PostgreSQL only as a SCRAM-SHA-256 verifier computed here. Checked
/// against the exchange in RFC 7677 section 3 (user "user", password "pencil"): a server holding only our StoredKey and
/// ServerKey must accept the RFC's client proof and answer with the RFC's server signature.
/// </summary>
public class ScramSha256Tests
{
    private const string RfcSalt = "W22ZaJ0SNY7soEsUEjb6gQ==";
    private const string RfcClientProof = "dHzbZapWIk4jUhN+Ute9ytag9zjfMHgsqmmiz7AndVQ=";
    private const string RfcServerSignature = "6rriTRBi23WpRR/wtup+mMhUZUn/dB5nLTJRsjl95G4=";

    private const string RfcAuthMessage =
        "n=user,r=rOprNGfwEbeRWgbNEkqO,"
        + "r=rOprNGfwEbeRWgbNEkqO%hvYDpWUa2RaTCAfuxFIlj)hNlF$k0,s=W22ZaJ0SNY7soEsUEjb6gQ==,i=4096,"
        + "c=biws,r=rOprNGfwEbeRWgbNEkqO%hvYDpWUa2RaTCAfuxFIlj)hNlF$k0";

    [Fact]
    public void The_verifier_of_the_rfc_7677_example_accepts_its_client_proof_and_gives_its_server_signature()
    {
        var verifier = ScramSha256.Verifier("pencil", Convert.FromBase64String(RfcSalt), 4096);

        var (iterations, salt, storedKey, serverKey) = Parse(verifier);
        iterations.ShouldBe(4096);
        salt.ShouldBe(RfcSalt);

        // Server side of SCRAM: ClientKey = ClientProof XOR HMAC(StoredKey, AuthMessage); SHA-256(ClientKey) must be StoredKey.
        var authMessage = Encoding.UTF8.GetBytes(RfcAuthMessage);
        var clientSignature = HMACSHA256.HashData(storedKey, authMessage);
        var clientKey = Convert.FromBase64String(RfcClientProof).Zip(clientSignature, (p, s) => (byte)(p ^ s)).ToArray();
        SHA256.HashData(clientKey).ShouldBe(storedKey);
        Convert.ToBase64String(HMACSHA256.HashData(serverKey, authMessage)).ShouldBe(RfcServerSignature);
    }

    [Fact]
    public void Each_verifier_has_its_own_salt_and_the_postgresql_defaults()
    {
        var first = ScramSha256.Verifier("same-password");
        var second = ScramSha256.Verifier("same-password");

        first.ShouldStartWith("SCRAM-SHA-256$4096:");
        first.ShouldNotBe(second);
        Convert.FromBase64String(Parse(first).Salt).Length.ShouldBe(16);
        first.ShouldNotContain("same-password");
    }

    [Theory]
    [InlineData("")]
    [InlineData("pässword")]
    [InlineData("tab\there")]
    public void A_password_outside_printable_ascii_is_refused_without_echoing_it(string password)
    {
        var refused = Should.Throw<ArgumentException>(() => ScramSha256.Verifier(password));

        if (password.Length > 0)
        {
            refused.Message.ShouldNotContain(password);
        }
    }

    private static (int Iterations, string Salt, byte[] StoredKey, byte[] ServerKey) Parse(string verifier)
    {
        // SCRAM-SHA-256$<iterations>:<salt>$<StoredKey>:<ServerKey>
        var parts = verifier.Split('$');
        parts.Length.ShouldBe(3);
        parts[0].ShouldBe("SCRAM-SHA-256");
        var head = parts[1].Split(':');
        var keys = parts[2].Split(':');
        return (int.Parse(head[0], System.Globalization.CultureInfo.InvariantCulture), head[1], Convert.FromBase64String(keys[0]), Convert.FromBase64String(keys[1]));
    }
}
