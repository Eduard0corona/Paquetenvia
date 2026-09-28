using Custody.Application.ProofUploads;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Paqueteria.Application.Security;

namespace Custody.Infrastructure.ProofStorage;

public sealed class ProofScannerHealthCheck(
    IProofThreatScanner scanner,
    IProofObjectStorage storage,
    IHostEnvironment environment) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default) =>
            scanner.IsEnabled
                ? await scanner.CheckHealthAsync(cancellationToken)
                    ? HealthCheckResult.Healthy("Proof threat scanner is configured.")
                    : HealthCheckResult.Unhealthy("Proof threat scanner is unavailable; proof operations fail closed.")
                : !storage.IsEnabled &&
                  (environment.IsDevelopment() ||
                   environment.IsEnvironment("Testing") ||
                   SyntheticEnvironmentPolicy.IsDevSynthetic(environment.EnvironmentName))
                    ? HealthCheckResult.Healthy(
                        "Proof scanner is intentionally disabled with proof storage; proof operations fail closed.")
                    : HealthCheckResult.Unhealthy(
                        "Proof threat scanner is disabled; proof operations fail closed.");
}
