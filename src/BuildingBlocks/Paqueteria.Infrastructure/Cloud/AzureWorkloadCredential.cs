using Azure.Core;
using Azure.Core.Pipeline;
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

    public static TokenCredential Create(string? clientId) => Create(clientId, transport: null);

    /// <summary>Test seam: the production credential with its token requests sent through <paramref name="transport"/>.</summary>
    internal static TokenCredential Create(string? clientId, HttpPipelineTransport? transport) =>
        new ManagedIdentityCredential(CreateOptions(clientId, transport));

    /// <summary>
    /// The credential options: system- or user-assigned identity, and no trace context on the token requests
    /// (OBS-003, <see cref="AzureClientTracing"/>). <paramref name="transport"/> is a test seam.
    /// </summary>
    internal static ManagedIdentityCredentialOptions CreateOptions(string? clientId, HttpPipelineTransport? transport = null)
    {
        var options = new ManagedIdentityCredentialOptions(
            string.IsNullOrWhiteSpace(clientId)
                ? ManagedIdentityId.SystemAssigned
                : ManagedIdentityId.FromUserAssignedClientId(clientId.Trim()));
        if (transport is not null)
        {
            options.Transport = transport;
        }

        return options.WithoutDistributedTracing();
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
