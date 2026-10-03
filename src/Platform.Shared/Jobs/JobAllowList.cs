using System.Collections.Frozen;
using System.Reflection;
using Hangfire.Common;
using Hangfire.Storage;

namespace Platform.Shared.Jobs;

/// <summary>
/// W-36 fix round 1 (review 2026-10-03): what the worker may load and run from a Hangfire job row. The application role
/// (the web host) can write Hangfire's tables, so a job row is untrusted input: whoever holds that role could name any
/// loadable type and public method (<c>System.Diagnostics.Process.Start</c>) and have the worker run it as erp_worker.
/// <list type="number">
/// <item><see cref="ResolveType"/> is the process-wide Hangfire type resolver of every process that runs a job server
/// (<see cref="JobsModule.AddJobServer"/>): it resolves only types of the platform's own assemblies (<c>Platform.*</c>) and
/// Hangfire's, and a short list of framework value types and collections for job arguments. A row naming anything else (as
/// the job's type or a parameter type) fails to load and is never invoked. Hangfire's internal serializer binds
/// <c>$type</c> through the same resolver; job arguments are deserialized without type names (Hangfire's default for user
/// data, pinned by tests).</item>
/// <item><see cref="Refusal(Job, string)"/>, applied by <see cref="JobAllowListFilter"/> before the job is activated: the
/// type must carry <see cref="PlatformJobAttribute"/> and come from a <c>Platform.*</c> assembly, the method must be public,
/// declared by that type and not a property accessor or operator, and an unscoped job must carry no tenant.</item>
/// </list>
/// </summary>
public static class JobAllowList
{
    private const string PlatformAssemblyPrefix = "Platform.";

    private static readonly FrozenSet<Type> FrameworkTypes = new[]
    {
        typeof(string), typeof(bool), typeof(byte), typeof(short), typeof(int), typeof(long), typeof(float), typeof(double),
        typeof(decimal), typeof(char), typeof(Guid), typeof(DateTime), typeof(DateTimeOffset), typeof(TimeSpan), typeof(DateOnly),
        typeof(CancellationToken), typeof(Nullable<>), typeof(IEnumerable<>), typeof(IReadOnlyCollection<>), typeof(IReadOnlyList<>),
        typeof(List<>), typeof(IReadOnlyDictionary<,>), typeof(Dictionary<,>),
    }.ToFrozenSet();

    /// <summary>Hangfire's default resolution, then refused unless every part of the type is allowed.</summary>
    public static Type ResolveType(string typeName)
    {
        var type = TypeHelper.DefaultTypeResolver(typeName);
        if (!IsAllowedType(type))
        {
            throw new JobRefusedException($"Type {type.FullName} is not on the worker's job allow-list.");
        }

        return type;
    }

    /// <summary>A platform or Hangfire type, or an allowed framework type whose element and type arguments are allowed.</summary>
    public static bool IsAllowedType(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (type.IsArray)
        {
            return type.GetArrayRank() == 1 && IsAllowedType(type.GetElementType()!);
        }

        if (type.IsByRef || type.IsPointer || type.IsGenericParameter)
        {
            return false;
        }

        if (type.IsConstructedGenericType)
        {
            return IsAllowedType(type.GetGenericTypeDefinition()) && type.GenericTypeArguments.All(IsAllowedType);
        }

        var assembly = type.Assembly.GetName().Name ?? string.Empty;
        return assembly.StartsWith(PlatformAssemblyPrefix, StringComparison.Ordinal)
            || assembly.StartsWith("Hangfire.", StringComparison.Ordinal)
            || FrameworkTypes.Contains(type);
    }

    /// <summary>Why the job must not run, or null when it may. <paramref name="tenantParameter"/> is the row's raw <c>TenantId</c> (or <c>Tenant</c>).</summary>
    public static string? Refusal(Job? job, string? tenantParameter)
    {
        if (job is null)
        {
            return "the job could not be loaded";
        }

        var type = job.Type;
        var attribute = type.GetCustomAttribute<PlatformJobAttribute>(inherit: false);
        if (attribute is null || !(type.Assembly.GetName().Name ?? string.Empty).StartsWith(PlatformAssemblyPrefix, StringComparison.Ordinal))
        {
            return $"{type.FullName} is not a platform job";
        }

        var method = job.Method;
        if (method.DeclaringType != type || !method.IsPublic || method.IsSpecialName || method.IsGenericMethodDefinition)
        {
            return $"{type.FullName}.{method.Name} is not a job method of that class";
        }

        if (!attribute.TenantScoped && !string.IsNullOrEmpty(tenantParameter))
        {
            return $"{type.FullName} runs without a tenant, but the job carries one";
        }

        return null;
    }

    /// <summary>The same check from the stored row (job and raw parameter), as the filter reads it.</summary>
    public static string? Refusal(IStorageConnection connection, Hangfire.BackgroundJob backgroundJob)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(backgroundJob);
        var tenant = connection.GetJobParameter(backgroundJob.Id, TenantJobFilter.TenantIdParameter);
        if (string.IsNullOrEmpty(tenant))
        {
            tenant = connection.GetJobParameter(backgroundJob.Id, TenantJobFilter.TenantParameter);
        }

        return Refusal(backgroundJob.Job, tenant);
    }
}

/// <summary>A job row the worker refuses to run (W-36 fix round 1).</summary>
public sealed class JobRefusedException : Exception
{
    public JobRefusedException()
    {
    }

    public JobRefusedException(string message)
        : base(message)
    {
    }

    public JobRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
