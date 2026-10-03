using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Hangfire.Common;
using Hangfire.Storage;
using Newtonsoft.Json;
using Platform.Shared.Tenancy;

namespace Platform.Shared.Jobs;

/// <summary>
/// W-42 (ADR-0012 addendum): a Hangfire job row is written through the application role, so a row the web host wrote and a
/// row SQL injected as that role wrote look the same in the table. Every job the platform creates therefore carries a
/// signature in its <see cref="ParameterName"/> parameter: HMAC-SHA256 under <see cref="JobSigningKeys"/> over what decides
/// what runs and as whom: the job's class, method, parameter types and arguments, its queue, its tenant (<c>TenantId</c> and
/// the <c>Tenant</c> snapshot), its recurring job id, and a random nonce with the time it was signed. The worker runs a job
/// only with a valid signature over exactly the values it uses (<see cref="JobGate"/>), and the nonce runs under one job id
/// only (<see cref="JobReplayLedger"/>), so a copied row is not a second run.
/// </summary>
/// <remarks>
/// Types are named by their full name and assembly name, without versions, so a runtime upgrade does not invalidate the
/// signature of a job queued before it. Arguments are compared as Hangfire serializes them for storage.
/// The token's form is <c>v1.{nonce}.{signed at, Unix milliseconds}.{MAC, base64url}</c>.
/// </remarks>
public sealed class JobAuthenticity(JobSigningKeys keys, TimeProvider time)
{
    /// <summary>The job parameter that carries the signature.</summary>
    public const string ParameterName = "Authenticity";

    /// <summary>A job not yet run this long after it was signed is refused (<see cref="JobReplayLedger"/> keeps nonces this long).</summary>
    public static readonly TimeSpan MaxAgeAtFirstRun = TimeSpan.FromDays(30);

    private const string Version = "v1";
    private const string Domain = "waslabid-job-authenticity-v1";

    /// <summary>A new signature for <paramref name="job"/> bound to <paramref name="binding"/>, with a fresh nonce.</summary>
    public string Sign(Job job, JobBinding binding) => Sign(job, binding, Guid.NewGuid(), time.GetUtcNow());

    /// <summary>Signs with the given nonce and time; tests use it to stand for a holder of the key.</summary>
    public string Sign(Job job, JobBinding binding, Guid nonce, DateTimeOffset signedAt)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(binding);
        var milliseconds = signedAt.ToUnixTimeMilliseconds();
        var mac = Mac(keys.Current, job, binding, nonce, milliseconds);
        return string.Join('.', Version, nonce.ToString("N"), milliseconds.ToString(CultureInfo.InvariantCulture), Base64Url(mac));
    }

    /// <summary>
    /// The token's nonce and signing time when <paramref name="token"/> is a valid signature over <paramref name="job"/> and
    /// <paramref name="binding"/> under the current or the previous key; otherwise null, with the reason (never the token).
    /// </summary>
    public JobToken? Verify(string? token, Job job, JobBinding binding, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(binding);
        if (string.IsNullOrEmpty(token))
        {
            refusal = "the job carries no signature";
            return null;
        }

        var parts = token.Split('.');
        if (parts.Length != 4
            || !string.Equals(parts[0], Version, StringComparison.Ordinal)
            || !Guid.TryParseExact(parts[1], "N", out var nonce)
            || !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var milliseconds)
            || !TryFromBase64Url(parts[3], out var presented))
        {
            refusal = "the job's signature is malformed";
            return null;
        }

        if (Matches(keys.Current, presented, job, binding, nonce, milliseconds)
            || (keys.Previous is { } previous && Matches(previous, presented, job, binding, nonce, milliseconds)))
        {
            refusal = null;
            return new JobToken(nonce, DateTimeOffset.FromUnixTimeMilliseconds(milliseconds));
        }

        refusal = "the job's signature does not match the job, its tenant or its key";
        return null;
    }

    private static bool Matches(byte[] key, byte[] presented, Job job, JobBinding binding, Guid nonce, long milliseconds) =>
        CryptographicOperations.FixedTimeEquals(Mac(key, job, binding, nonce, milliseconds), presented);

    private static byte[] Mac(byte[] key, Job job, JobBinding binding, Guid nonce, long milliseconds)
    {
        var invocation = InvocationData.SerializeJob(job);
        var message = new StringBuilder();
        Append(message, Domain);
        Append(message, TypeName(job.Type, withAssembly: true));
        Append(message, job.Method.Name);
        Append(message, string.Join(';', job.Method.GetParameters().Select(p => TypeName(p.ParameterType, withAssembly: false))));
        Append(message, invocation.Arguments);
        Append(message, job.Queue);
        Append(message, binding.TenantId?.ToString("D"));
        Append(message, binding.Tenant is null ? null : SerializationHelper.Serialize(binding.Tenant, SerializationOption.User));
        Append(message, binding.RecurringJobId);
        Append(message, nonce.ToString("N"));
        Append(message, milliseconds.ToString(CultureInfo.InvariantCulture));
        return HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(message.ToString()));
    }

    /// <summary>Length-prefixed, so no two different field lists produce the same message; null differs from empty.</summary>
    private static void Append(StringBuilder message, string? value) =>
        message.Append(value is null ? "-1" : value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append('\n');

    /// <summary>Full name without assembly versions (<see cref="Type.ToString"/>), plus the assembly's simple name for the job's class.</summary>
    private static string TypeName(Type type, bool withAssembly) =>
        withAssembly ? $"{type}, {type.Assembly.GetName().Name}" : type.ToString();

    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryFromBase64Url(string value, out byte[] bytes)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", _ => string.Empty };
        var buffer = new byte[(padded.Length * 3 / 4) + 3];
        if (Convert.TryFromBase64String(padded, buffer, out var written))
        {
            bytes = buffer[..written];
            return true;
        }

        bytes = [];
        return false;
    }
}

/// <summary>A verified signature's nonce and signing time.</summary>
public readonly record struct JobToken(Guid Nonce, DateTimeOffset SignedAt);

/// <summary>
/// What a job's signature binds besides the job itself: the tenant it runs as (<see cref="TenantJobFilter"/>'s two
/// parameters) and its recurring job id (set by Hangfire's recurring scheduler, used by the F-60 job failure streaks).
/// </summary>
public sealed record JobBinding(Guid? TenantId, TenantContext? Tenant, string? RecurringJobId)
{
    /// <summary>Hangfire's recurring scheduler's parameter.</summary>
    public const string RecurringJobIdParameter = "RecurringJobId";

    public static JobBinding None { get; } = new(null, null, null);

    /// <summary>The values a job being created carries (its parameters may hold the objects or their serialized form).</summary>
    public static JobBinding FromParameters(IDictionary<string, object?> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return new JobBinding(
            Value<Guid?>(parameters, TenantJobFilter.TenantIdParameter),
            Value<TenantContext>(parameters, TenantJobFilter.TenantParameter),
            Value<string>(parameters, RecurringJobIdParameter));
    }

    /// <summary>
    /// The stored values of job <paramref name="jobId"/>, each read once, with its signature parameter. A value that does not
    /// deserialize is a refusal (the row was not written by the platform's client), never a guess.
    /// </summary>
    public static StoredBinding Read(IStorageConnection connection, string jobId)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        var tenantId = connection.GetJobParameter(jobId, TenantJobFilter.TenantIdParameter);
        var tenant = connection.GetJobParameter(jobId, TenantJobFilter.TenantParameter);
        var recurring = connection.GetJobParameter(jobId, RecurringJobIdParameter);
        var token = connection.GetJobParameter(jobId, JobAuthenticity.ParameterName);
        try
        {
            return new StoredBinding(
                new JobBinding(Deserialize<Guid?>(tenantId), Deserialize<TenantContext>(tenant), Deserialize<string>(recurring)),
                Deserialize<string>(token),
                Refusal: null,
                RawTenantId: tenantId);
        }
        catch (JsonException)
        {
            // Not swallowed: the row is refused for it, and the refusal is logged by the caller with the job id.
            return new StoredBinding(None, null, "a tenant, recurring job or signature parameter is malformed", tenantId);
        }
    }

    private static T? Deserialize<T>(string? raw) => string.IsNullOrEmpty(raw) ? default : SerializationHelper.Deserialize<T>(raw);

    private static T? Value<T>(IDictionary<string, object?> parameters, string name) =>
        parameters.TryGetValue(name, out var value) switch
        {
            false => default,
            true when value is null => default,
            true when value is T typed => typed,
            true when value is string raw && typeof(T) != typeof(string) => SerializationHelper.Deserialize<T>(raw),
            _ => SerializationHelper.Deserialize<T>(SerializationHelper.Serialize(value, SerializationOption.User)),
        };
}

/// <summary>A job's stored binding and signature, or why they could not be read.</summary>
public sealed record StoredBinding(JobBinding Binding, string? Token, string? Refusal, string? RawTenantId);
