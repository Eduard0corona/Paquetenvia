using Identity.Application.Notifications;
using Organizations.Application.Notifications;

namespace Notifications.Application.Audience;

public static class NotificationAudienceErrorCodes
{
    public const string LimitExceeded = "AUDIENCE_LIMIT_EXCEEDED";
    public const string ContractViolation = "AUDIENCE_CONTRACT_VIOLATION";
}

public sealed class NotificationAudienceException(string errorCode) : Exception(errorCode)
{
    public string ErrorCode { get; } = errorCode;
}

public sealed class NotificationAudienceResolver(
    IOwnerOrganizationDispatcherReader dispatchers,
    IActiveNotificationRecipientReader activeUsers)
{
    public const int MaximumRecipients = 500;

    public async Task<IReadOnlyList<Guid>> ResolveAsync(
        Guid ownerOrganizationId,
        CancellationToken cancellationToken)
    {
        if (ownerOrganizationId == Guid.Empty)
        {
            throw new ArgumentException("A non-empty owner organization is required.", nameof(ownerOrganizationId));
        }

        var candidates = (await dispatchers.ReadActiveDispatcherUserIdsAsync(
                ownerOrganizationId,
                MaximumRecipients + 1,
                cancellationToken))
            .Where(static id => id != Guid.Empty)
            .Distinct()
            .Order()
            .ToArray();
        if (candidates.Length > MaximumRecipients)
        {
            throw new NotificationAudienceException(NotificationAudienceErrorCodes.LimitExceeded);
        }
        if (candidates.Length == 0)
        {
            return [];
        }

        var active = (await activeUsers.ReadActiveUserIdsAsync(candidates, cancellationToken))
            .Where(static id => id != Guid.Empty)
            .ToArray();
        var requested = candidates.ToHashSet();
        if (active.Any(id => !requested.Contains(id)))
        {
            throw new NotificationAudienceException(NotificationAudienceErrorCodes.ContractViolation);
        }

        return active.Distinct().Order().ToArray();
    }
}
