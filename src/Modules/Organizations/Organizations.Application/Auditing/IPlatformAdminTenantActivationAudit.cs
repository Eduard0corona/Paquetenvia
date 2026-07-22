namespace Organizations.Application.Auditing;

public interface IPlatformAdminTenantActivationAudit
{
    Task RecordAsync(
        Guid actorUserId,
        Guid organizationId,
        string? requestId,
        CancellationToken cancellationToken);
}
