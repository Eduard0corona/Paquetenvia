using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Paqueteria.Infrastructure.Database.Pooling;

/// <summary>
/// SCL-007: the only registration of runtime connection pools. Every module asks for the role it runs as; the first
/// call registers the role's <see cref="RuntimeDataSource"/> under that key and every later call reuses it, so a process
/// holds exactly one pool per role whatever the order in which its modules are composed.
/// </summary>
public static class RuntimeDataSourceServiceCollectionExtensions
{
    /// <summary>
    /// Registers, once per process, the <see cref="RuntimeDataSource"/> of <paramref name="role"/> (keyed by the role)
    /// and its <see cref="NpgsqlDataSource"/> under the same key, sized by the validated
    /// <see cref="DatabasePoolOptions"/>.
    /// </summary>
    public static IServiceCollection AddRuntimeDataSource(
        this IServiceCollection services,
        IConfiguration configuration,
        RuntimeDatabaseRole role)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var connectionStringName = RuntimeDatabaseRoles.ConnectionStringName(role);

        if (!services.Any(static descriptor => descriptor.ServiceType == typeof(DatabasePoolOptionsMarker)))
        {
            services.AddSingleton<DatabasePoolOptionsMarker>();
            services.AddOptions<DatabasePoolOptions>()
                .Bind(configuration.GetSection(DatabasePoolOptions.SectionName))
                .ValidateOnStart();
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IValidateOptions<DatabasePoolOptions>, DatabasePoolOptionsValidator>());
        }

        services.TryAddKeyedSingleton(role, (serviceProvider, _) => RuntimeDataSource.Create(
            role,
            configuration.GetConnectionString(connectionStringName),
            serviceProvider.GetRequiredService<IOptions<DatabasePoolOptions>>().Value.MaximumPoolSizeFor(role),
            serviceProvider.GetServices<RuntimeDataSourcePlugin>()
                .Where(plugin => plugin.Role == role)
                .OrderBy(static plugin => plugin.Name, StringComparer.Ordinal)
                .Select(static plugin => plugin.Configure)
                .ToArray()));
        services.TryAddKeyedSingleton<NpgsqlDataSource>(role, static (serviceProvider, key) =>
            serviceProvider.GetRequiredKeyedService<RuntimeDataSource>(key).DataSource);
        return services;
    }

    /// <summary>
    /// Adds a type plugin to the shared pool of <paramref name="role"/>, once per <paramref name="name"/>. It is applied
    /// when the pool is built, so it reaches the pool whichever module registered it first.
    /// </summary>
    public static IServiceCollection AddRuntimeDataSourcePlugin(
        this IServiceCollection services,
        RuntimeDatabaseRole role,
        string name,
        Action<NpgsqlDataSourceBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        var plugin = new RuntimeDataSourcePlugin(role, name, configure);
        if (!services.Any(descriptor =>
                descriptor.ServiceType == typeof(RuntimeDataSourcePlugin) &&
                descriptor.ImplementationInstance is RuntimeDataSourcePlugin existing &&
                existing.Role == role &&
                string.Equals(existing.Name, name, StringComparison.Ordinal)))
        {
            services.AddSingleton(plugin);
        }

        return services;
    }

    /// <summary>The shared pool of <paramref name="role"/>, for EF Core registrations.</summary>
    public static NpgsqlDataSource GetRuntimeDataSource(
        this IServiceProvider serviceProvider,
        RuntimeDatabaseRole role)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        return serviceProvider.GetRequiredKeyedService<RuntimeDataSource>(role).DataSource;
    }

    /// <summary>Marks that <see cref="DatabasePoolOptions"/> is bound and validated in this collection.</summary>
    private sealed class DatabasePoolOptionsMarker;
}
