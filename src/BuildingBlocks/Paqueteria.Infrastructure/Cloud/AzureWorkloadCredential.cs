using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Paqueteria.Infrastructure.Cloud;

/// <summary>
/// ADP-001: the only way the production adapters authenticate to Azure. The workload uses its
/// managed identity (user-assigned when <c>AZURE_CLIENT_ID</c> is set, system-assigned otherwise);
/// no client secret, storage account key or SAS is ever read from configuration.
/// </summary>
public static class AzureWorkloadCredential
{
    public const string ClientIdVariable = "AZURE_CLIENT_ID";

    public static TokenCredential Create(string? clientId)
    {
        var options = new ManagedIdentityCredentialOptions(
            string.IsNullOrWhiteSpace(clientId)
                ? ManagedIdentityId.SystemAssigned
                : ManagedIdentityId.FromUserAssignedClientId(clientId.Trim()));
        return new ManagedIdentityCredential(options);
    }

    /// <summary>
    /// Registers the workload credential once. Tests replace it by registering their own
    /// <see cref="TokenCredential"/> first; production code never registers another.
    /// </summary>
    public static IServiceCollection AddAzureWorkloadCredential(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<TokenCredential>(_ =>
            Create(Environment.GetEnvironmentVariable(ClientIdVariable)));
        return services;
    }
}
