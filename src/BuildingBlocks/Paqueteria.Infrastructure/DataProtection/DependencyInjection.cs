using Azure.Core;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Paqueteria.Infrastructure.Cloud;
using Paqueteria.Infrastructure.Database.Pooling;

namespace Paqueteria.Infrastructure.DataProtection;

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

        // SCL-007: the key ring uses the process's one pool of its runtime role (paqueteria_app in the API,
        // paqueteria_worker in the Worker); an unknown connection name, or one paired with another role, registers no
        // pool and fails the start validation above.
        if (options.Provider != DataProtectionProviderKind.PostgreSql ||
            !DataProtectionConnectionNames.TryGetRuntimePool(
                options.ConnectionStringName,
                options.RuntimeRole,
                out var databaseRole))
        {
            return services;
        }

        services.AddRuntimeDataSource(configuration, databaseRole);

        var dataProtection = services.AddDataProtection()
            .SetApplicationName(options.ApplicationName)
            .SetDefaultKeyLifetime(TimeSpan.FromDays(options.KeyLifetimeDays));

        if (options.KeyEncryption?.Provider == DataProtectionKeyEncryptionKind.AzureKeyVault &&
            Uri.TryCreate(options.KeyEncryption.AzureKeyVault?.KeyId, UriKind.Absolute, out var keyEncryptionKeyId))
        {
            // ADP-001 / ENV-001: every key written to the shared ring is wrapped by the Key Vault key
            // (managed identity). Existing unwrapped entries stay readable; new ones are encrypted.
            services.AddAzureWorkloadCredential();
            services.TryAddSingleton(serviceProvider => DataProtectionKeyEncryptionKeyResolver.CreateKeyVault(
                serviceProvider.GetRequiredService<TokenCredential>()));
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
                    serviceProvider.GetRuntimeDataSource(databaseRole),
                    options,
                    serviceProvider.GetRequiredService<ILogger<PostgreSqlXmlRepository>>())));

        return services;
    }
}
