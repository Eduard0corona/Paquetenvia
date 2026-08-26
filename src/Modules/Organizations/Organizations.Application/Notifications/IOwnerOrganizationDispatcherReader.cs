namespace Organizations.Application.Notifications;

public interface IOwnerOrganizationDispatcherReader
{
    Task<IReadOnlyList<Guid>> ReadActiveDispatcherUserIdsAsync(
        Guid ownerOrganizationId,
        int limit,
        CancellationToken cancellationToken);
}
