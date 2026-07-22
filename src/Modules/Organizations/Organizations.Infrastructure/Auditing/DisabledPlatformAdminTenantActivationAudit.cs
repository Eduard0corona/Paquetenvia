using Organizations.Application.Auditing;

namespace Organizations.Infrastructure.Auditing;

public sealed class DisabledPlatformAdminTenantActivationAudit : IPlatformAdminTenantActivationAudit
{
    public Task RecordAsync(
        Guid actorUserId,
        Guid organizationId,
        string? requestId,
        CancellationToken cancellationToken) => Task.CompletedTask;
}
