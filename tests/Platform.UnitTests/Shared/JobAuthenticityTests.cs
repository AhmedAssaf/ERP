using System.Security.Cryptography;
using Hangfire.Common;
using Hangfire.Storage;
using Microsoft.Extensions.Configuration;
using Platform.Shared.Jobs;
using Platform.Shared.Tenancy;

namespace Platform.UnitTests.Shared;

/// <summary>
/// W-42: the signature a job row carries. It verifies only for the same job (class, method, arguments, queue), the same
/// tenant pair and recurring job id, the same nonce and time, under the current or previous key; any change, another key,
/// or a malformed token is a refusal that never echoes the token. The keys come from configuration and are refused when
/// missing or short.
/// </summary>
public sealed class JobAuthenticityTests
{
    private static readonly TenantContext Acme = new(
        Guid.Parse("6a1f0d0e-5d43-4d0b-9f43-1f6c1d2b7a01"), "acme", "acme", "ar-SA", new TenantBranding("Acme", "#123456", null));

    private static readonly TenantContext Beta = Acme with { TenantId = Guid.Parse("0b7e3f52-2f3c-45a5-8f7e-6b9b1c1e2d02"), Slug = "beta" };

    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);

    [Fact]
    public void A_signed_job_verifies_with_its_nonce_and_time()
    {
        var authenticity = Authenticity(_key);
        var job = Job.FromExpression(() => SignedProbe.Run("a", 1));
        var binding = new JobBinding(Acme.TenantId, Acme, "recurring-a");
        var signedAt = DateTimeOffset.FromUnixTimeMilliseconds(1_790_000_000_000);
        var nonce = Guid.NewGuid();

        var token = authenticity.Sign(job, binding, nonce, signedAt);

        authenticity.Verify(token, job, binding, out var refusal).ShouldBe(new JobToken(nonce, signedAt));
        refusal.ShouldBeNull();
    }

    [Fact]
    public void A_change_to_the_job_or_what_it_is_bound_to_is_refused()
    {
        var authenticity = Authenticity(_key);
        var job = Job.FromExpression(() => SignedProbe.Run("a", 1));
        var binding = new JobBinding(Acme.TenantId, Acme, null);
        var token = authenticity.Sign(job, binding);

        Job[] otherJobs =
        [
            Job.FromExpression(() => SignedProbe.Run("b", 1)),
            Job.FromExpression(() => SignedProbe.Run("a", 2)),
            Job.FromExpression(() => SignedProbe.Other("a", 1)),
            OnQueue(Job.FromExpression(() => SignedProbe.Run("a", 1)), "other-queue"),
            Job.FromExpression(() => OtherSignedProbe.Run("a", 1)),
        ];
        foreach (var other in otherJobs)
        {
            authenticity.Verify(token, other, binding, out var refusal).ShouldBeNull($"{other.Method.Name} {other.Queue}");
            refusal.ShouldNotBeNull().ShouldContain("does not match");
        }

        JobBinding[] otherBindings =
        [
            new(Beta.TenantId, Beta, null),
            new(Beta.TenantId, Acme, null),
            new(Acme.TenantId, Beta, null),
            new(null, null, null),
            new(Acme.TenantId, Acme, "recurring-a"),
            new(Acme.TenantId, Acme with { DefaultCulture = "en-US" }, null),
        ];
        foreach (var other in otherBindings)
        {
            authenticity.Verify(token, job, other, out _).ShouldBeNull(other.ToString());
        }
    }

    [Fact]
    public void A_token_with_another_nonce_or_time_does_not_verify()
    {
        var authenticity = Authenticity(_key);
        var job = Job.FromExpression(() => SignedProbe.Run("a", 1));
        var parts = authenticity.Sign(job, JobBinding.None).Split('.');

        var otherNonce = string.Join('.', parts[0], Guid.NewGuid().ToString("N"), parts[2], parts[3]);
        var otherTime = string.Join('.', parts[0], parts[1], (long.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture) + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), parts[3]);

        authenticity.Verify(otherNonce, job, JobBinding.None, out _).ShouldBeNull();
        authenticity.Verify(otherTime, job, JobBinding.None, out _).ShouldBeNull();
    }

    [Fact]
    public void Another_key_does_not_verify_and_the_previous_key_still_does()
    {
        var job = Job.FromExpression(() => SignedProbe.Run("a", 1));
        var previous = RandomNumberGenerator.GetBytes(32);
        var signedWithPrevious = Authenticity(previous).Sign(job, JobBinding.None);
        var signedWithOther = Authenticity(RandomNumberGenerator.GetBytes(32)).Sign(job, JobBinding.None);
        var rotated = new JobAuthenticity(JobSigningKeys.FromBytes(_key, previous), TimeProvider.System);

        rotated.Verify(signedWithPrevious, job, JobBinding.None, out _).ShouldNotBeNull();
        rotated.Verify(signedWithOther, job, JobBinding.None, out _).ShouldBeNull();
        Authenticity(_key).Verify(signedWithPrevious, job, JobBinding.None, out _).ShouldBeNull("the previous key verifies only where it is configured");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("v1")]
    [InlineData("v2.00000000000000000000000000000000.1.AAAA")]
    [InlineData("v1.not-a-guid.1.AAAA")]
    [InlineData("v1.00000000000000000000000000000000.-1.AAAA")]
    [InlineData("v1.00000000000000000000000000000000.1.***")]
    [InlineData("v1.00000000000000000000000000000000.1.AAAA.extra")]
    public void A_missing_or_malformed_token_is_refused_without_echoing_it(string? token)
    {
        var job = Job.FromExpression(() => SignedProbe.Run("a", 1));

        Authenticity(_key).Verify(token, job, JobBinding.None, out var refusal).ShouldBeNull();

        refusal.ShouldNotBeNullOrEmpty();
        if (!string.IsNullOrEmpty(token))
        {
            refusal.ShouldNotContain(token);
        }
    }

    [Fact]
    public void The_signature_names_types_without_versions()
    {
        // Two jobs whose parameter type strings would differ only in a runtime version still sign the same.
        var authenticity = Authenticity(_key);
        var job = Job.FromExpression(() => SignedProbe.Cancellable(CancellationToken.None));
        var reloaded = InvocationData.SerializeJob(job).DeserializeJob();
        var token = authenticity.Sign(job, JobBinding.None);

        authenticity.Verify(token, reloaded, JobBinding.None, out _).ShouldNotBeNull();
    }

    [Fact]
    public void The_keys_come_from_configuration_and_a_missing_or_short_key_is_refused()
    {
        var key = Convert.ToBase64String(_key);
        JobSigningKeys.FromConfiguration(Configuration((JobSigningKeys.SigningKeySetting, key))).ShouldNotBeNull();

        foreach (var value in new[] { null, string.Empty, "not base64!", Convert.ToBase64String(new byte[31]) })
        {
            var refused = Should.Throw<InvalidOperationException>(() => JobSigningKeys.FromConfiguration(Configuration((JobSigningKeys.SigningKeySetting, value))));
            refused.Message.ShouldContain(JobSigningKeys.SigningKeySetting);
            if (!string.IsNullOrEmpty(value))
            {
                refused.Message.ShouldNotContain(value);
            }
        }

        Should.Throw<InvalidOperationException>(() => JobSigningKeys.FromConfiguration(Configuration(
            (JobSigningKeys.SigningKeySetting, key), (JobSigningKeys.PreviousSigningKeySetting, "short")))).Message.ShouldContain(JobSigningKeys.PreviousSigningKeySetting);
        JobSigningKeys.FromConfiguration(Configuration((JobSigningKeys.SigningKeySetting, key))).ToString().ShouldNotContain(key);
    }

    private static Job OnQueue(Job job, string queue) => new(job.Type, job.Method, job.Args, queue);

    private static JobAuthenticity Authenticity(byte[] key) => new(JobSigningKeys.FromBytes(key), TimeProvider.System);

    private static IConfiguration Configuration(params (string Key, string? Value)[] settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value))).Build();
}

[PlatformJob]
public static class SignedProbe
{
    public static void Run(string text, int number) => _ = (text, number);

    public static void Other(string text, int number) => _ = (text, number);

    public static Task Cancellable(CancellationToken cancellationToken) => Task.CompletedTask;
}

[PlatformJob]
public static class OtherSignedProbe
{
    public static void Run(string text, int number) => _ = (text, number);
}
