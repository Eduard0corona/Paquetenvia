using Custody.Application.ProofUploads;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Paqueteria.Application.Security;

namespace Custody.Infrastructure.ProofStorage;

public sealed class ProofStorageHealthCheck(
    IProofObjectStorage storage,
    IHostEnvironment environment) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (!storage.IsEnabled)
        {
            return environment.IsDevelopment() ||
                   environment.IsEnvironment("Testing") ||
                   SyntheticEnvironmentPolicy.IsDevSynthetic(environment.EnvironmentName)
                ? HealthCheckResult.Healthy(
                    "Proof storage is intentionally disabled; proof operations fail closed.")
                : HealthCheckResult.Unhealthy(
                    "Proof storage must be configured outside Development and Testing.");
        }

        return await storage.CheckHealthAsync(cancellationToken)
            ? HealthCheckResult.Healthy("Proof storage is reachable.")
            : HealthCheckResult.Unhealthy("Proof storage is unavailable.");
    }
}
