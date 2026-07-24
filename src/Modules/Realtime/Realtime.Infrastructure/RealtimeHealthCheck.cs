using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Realtime.Application.Configuration;

namespace Realtime.Infrastructure;

internal sealed class RealtimeHealthCheck(IOptions<RealtimeOptions> options) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(options.Value.Provider == RealtimeProviderKind.SignalR
            ? HealthCheckResult.Healthy("SignalR in-process is enabled.")
            : HealthCheckResult.Degraded("Realtime is disabled and fails closed."));
}
