using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Platform.Shared.Data;
using Platform.Shared.Email;
using Platform.Shared.Storage;
using Platform.Shared.Tenancy;

namespace Platform.Shared;

public static class SharedModule
{
    public static IServiceCollection AddPlatformShared(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<TenantAccessor>();
        services.AddScoped<ITenantAccessor>(sp => sp.GetRequiredService<TenantAccessor>());
        services.AddScoped<VendorAccessor>();
        services.AddScoped<IVendorAccessor>(sp => sp.GetRequiredService<VendorAccessor>());
        services.AddScoped<PlatformRequestContext>();
        services.AddScoped<IPlatformRequestContext>(sp => sp.GetRequiredService<PlatformRequestContext>());
        // Unconfigured until the host calls AddObjectStorage; using it then throws rather than guessing a bucket.
        services.TryAddSingleton(ObjectStorageSettings.None);
        services.TryAddSingleton<IObjectStorage, S3ObjectStorage>();
        return services;
    }

    /// <summary>
    /// Configures object storage from <c>ObjectStorage:*</c> (<see cref="ObjectStorageSettings"/>) for every module that
    /// uses the bucket. Without the settings, storage stays unconfigured.
    /// </summary>
    public static IServiceCollection AddObjectStorage(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.Replace(ServiceDescriptor.Singleton(ObjectStorageSettings.FromConfiguration(configuration)));
        services.TryAddSingleton<IObjectStorage, S3ObjectStorage>();
        return services;
    }

    /// <summary>
    /// Registers the platform's own email sender (<see cref="IEmailSender"/>, MailKit) with <c>Smtp:*</c>
    /// (<see cref="EmailSettings"/>). Idempotent: the first registration wins, so modules may each call it.
    /// </summary>
    public static IServiceCollection AddEmail(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.TryAddSingleton(_ => EmailSettings.FromConfiguration(configuration));
        services.TryAddSingleton<IEmailSender, MailKitEmailSender>();
        return services;
    }

    public static Task<IReadOnlyList<string>> MigrateAsync(NpgsqlConnection connection, CancellationToken cancellationToken = default) =>
        SqlMigrator.ApplyAsync(connection, "platform", typeof(SharedModule).Assembly, cancellationToken);
}
