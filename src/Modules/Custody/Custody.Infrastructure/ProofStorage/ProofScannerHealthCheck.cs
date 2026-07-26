using Custody.Application.ProofUploads;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace Custody.Infrastructure.ProofStorage;

public sealed class ProofScannerHealthCheck(
    IProofThreatScanner scanner,
    IProofObjectStorage storage,
    IHostEnvironment environment) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(
            scanner.IsEnabled
                ? HealthCheckResult.Healthy("Proof threat scanner is configured.")
                : !storage.IsEnabled &&
                  (environment.IsDevelopment() || environment.IsEnvironment("Testing"))
                    ? HealthCheckResult.Healthy(
                        "Proof scanner is intentionally disabled with proof storage; proof operations fail closed.")
                    : HealthCheckResult.Unhealthy(
                        "Proof threat scanner is disabled; proof operations fail closed."));
}
