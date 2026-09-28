using Azure.Core;
using Azure.Security.KeyVault.Keys.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Paqueteria.Infrastructure.Cloud;

namespace Paqueteria.Infrastructure.DataProtection;

/// <summary>
/// Owns the lifetime of the key-ring data source so the repository and the health check share a
/// single pool without competing with the tenant-scoped application data sources.
/// </summary>
internal sealed class DataProtectionDataSource(string connectionString) : IAsyncDisposable
{
    private readonly Lazy<NpgsqlDataSource> _dataSource = new(() => NpgsqlDataSource.Create(
        !string.IsNullOrWhiteSpace(connectionString)
            ? connectionString
            : throw new InvalidOperationException(
                "A connection string is required when DataProtection:Provider=PostgreSql.")));

    public NpgsqlDataSource Value => _dataSource.Value;

    public ValueTask DisposeAsync() =>
        _dataSource.IsValueCreated ? _dataSource.Value.DisposeAsync() : ValueTask.CompletedTask;
}

public static class DependencyInjection
{
    /// <summary>
    /// Externalizes the ASP.NET Data Protection key ring so API and Worker replicas can be
    /// replaced or scaled without invalidating payloads protected by another node.
    /// </summary>
    /// <remarks>
    /// With <c>DataProtection:Provider=Disabled</c> the host keeps the framework default
    /// single-node key ring, which preserves current single-instance behaviour.
    /// </remarks>
    public static IServiceCollection AddPlatformDataProtection(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(DataProtectionOptions.SectionName);
        var options = new DataProtectionOptions();
        section.Bind(options);

        services.AddOptions<DataProtectionOptions>()
            .Bind(section)
            .Validate(
                DataProtectionOptionsValidator.IsValid,
                "DataProtection contains an invalid bounded option.")
            .Validate(
                value => value.Provider != DataProtectionProviderKind.PostgreSql ||
                    !string.IsNullOrWhiteSpace(
                        configuration.GetConnectionString(value.ConnectionStringName)),
                "DataProtection:Provider=PostgreSql requires the configured connection string.")
            .ValidateOnStart();

        services.AddHealthChecks()
            .AddCheck<DataProtectionHealthCheck>("data_protection_keyring", tags: ["ready"]);

        if (options.Provider != DataProtectionProviderKind.PostgreSql)
        {
            return services;
        }

        services.AddSingleton(_ => new DataProtectionDataSource(
            configuration.GetConnectionString(options.ConnectionStringName) ?? string.Empty));

        var dataProtection = services.AddDataProtection()
            .SetApplicationName(options.ApplicationName)
            .SetDefaultKeyLifetime(TimeSpan.FromDays(options.KeyLifetimeDays));

        if (options.KeyEncryption?.Provider == DataProtectionKeyEncryptionKind.AzureKeyVault &&
            Uri.TryCreate(options.KeyEncryption.AzureKeyVault?.KeyId, UriKind.Absolute, out var keyEncryptionKeyId))
        {
            // ADP-001 / ENV-001: every key written to the shared ring is wrapped by the Key Vault key
            // (managed identity). Existing unwrapped entries stay readable; new ones are encrypted.
            services.AddAzureWorkloadCredential();
            services.TryAddSingleton(serviceProvider => new DataProtectionKeyEncryptionKeyResolver(
                new KeyResolver(serviceProvider.GetRequiredService<TokenCredential>())));
            dataProtection.ProtectKeysWithAzureKeyVault(
                keyEncryptionKeyId,
                serviceProvider => serviceProvider.GetRequiredService<DataProtectionKeyEncryptionKeyResolver>().Resolver);
            services.AddSingleton(new DataProtectionKeyEncryptionKeyId(keyEncryptionKeyId));
            services.AddHealthChecks()
                .AddCheck<DataProtectionKeyEncryptionHealthCheck>("data_protection_key_encryption", tags: ["ready"]);
        }

        // Registered after AddDataProtection so it replaces the framework's local key repository.
        services.AddSingleton<IConfigureOptions<KeyManagementOptions>>(serviceProvider =>
            new ConfigureOptions<KeyManagementOptions>(keyManagement =>
                keyManagement.XmlRepository = new PostgreSqlXmlRepository(
                    serviceProvider.GetRequiredService<DataProtectionDataSource>().Value,
                    options,
                    serviceProvider.GetRequiredService<ILogger<PostgreSqlXmlRepository>>())));

        return services;
    }
}
