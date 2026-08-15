using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Notifications.Infrastructure.Dispatching;

internal sealed class NotificationsHealthCheck(
    INotificationsStore store,
    IOptions<NotificationsOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (options.Value.Provider == NotificationsDispatcherProviderKind.Disabled)
        {
            return HealthCheckResult.Healthy("Notifications dispatcher is disabled.");
        }

        try
        {
            var owner = await store.ResolveConsumerAsync("orders.created", cancellationToken);
            var unrouted = await store.ResolveConsumerAsync("ntf001-health-unknown", cancellationToken);
            return owner == "NOTIFICATIONS" && unrouted == "UNROUTED"
                ? HealthCheckResult.Healthy("Notifications routing functions are available.")
                : HealthCheckResult.Unhealthy("Notifications routing functions returned an invalid owner.");
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy("Notifications routing functions are unavailable.", exception);
        }
    }
}
