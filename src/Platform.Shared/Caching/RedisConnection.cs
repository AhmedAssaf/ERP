using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace Platform.Shared.Caching;

/// <summary>
/// W-34: the one Redis connection of a host (docs/02 stack row "Cache and locks"), from <c>ConnectionStrings:Redis</c>.
/// Development uses <c>localhost:6379</c> (the Compose service <c>erp-redis</c>, no password); the pilot's value, with
/// its password, lives in the secret store, never in the repository (N-10). Without the setting nothing is registered,
/// and each user falls back to what it did before (the duplicate-CR throttle counts in process memory; the worker does
/// not check Redis). <c>abortConnect=false</c> is always applied, so the host starts while Redis is down and the
/// multiplexer keeps reconnecting in the background; with the fail-fast backlog policy a call made meanwhile fails at
/// once (the default policy would hold it for the five-second timeout) and its caller falls back. A Redis that accepts
/// connections but does not answer still costs each call the async timeout (five seconds unless the connection string
/// sets <c>asyncTimeout</c>).
/// </summary>
public static class RedisConnection
{
    public const string ConnectionStringName = "Redis";

    /// <summary>True when the host has <c>ConnectionStrings:Redis</c>.</summary>
    public static bool IsConfigured(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return !string.IsNullOrWhiteSpace(configuration.GetConnectionString(ConnectionStringName));
    }

    /// <summary>
    /// The options for <paramref name="connectionString"/> with <see cref="ConfigurationOptions.AbortOnConnectFail"/> off and
    /// <see cref="BacklogPolicy.FailFast"/>.
    /// The connection string is never put into an exception message or a log record here (N-10).
    /// </summary>
    public static ConfigurationOptions Options(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        var options = ConfigurationOptions.Parse(connectionString);
        options.AbortOnConnectFail = false;
        options.BacklogPolicy = BacklogPolicy.FailFast;
        return options;
    }

    /// <summary>
    /// Registers one <see cref="IConnectionMultiplexer"/> for the host when <c>ConnectionStrings:Redis</c> is set; idempotent,
    /// so every module that needs Redis may call it. The multiplexer connects when first resolved.
    /// </summary>
    public static IServiceCollection AddRedis(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        if (!IsConfigured(configuration))
        {
            return services;
        }

        var options = Options(configuration.GetConnectionString(ConnectionStringName)!);
        services.TryAddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(options));
        return services;
    }
}
