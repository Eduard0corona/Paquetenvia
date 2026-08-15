namespace Identity.Application.Notifications;

public interface IActiveNotificationRecipientReader
{
    Task<IReadOnlyList<Guid>> ReadActiveUserIdsAsync(
        IReadOnlyCollection<Guid> requestedUserIds,
        CancellationToken cancellationToken);
}
