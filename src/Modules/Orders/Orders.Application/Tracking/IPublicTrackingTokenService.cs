namespace Orders.Application.Tracking;

public sealed record IssuePublicTrackingTokenCommand(
    Guid ActorId,
    Guid OrganizationId,
    Guid OrderId,
    string RequestId,
    DateTimeOffset? RequestedExpiration = null);

public sealed record RotatePublicTrackingTokenCommand(
    Guid ActorId,
    Guid OrganizationId,
    Guid OrderId,
    string RequestId,
    DateTimeOffset? RequestedExpiration = null);

public sealed record RevokePublicTrackingTokenCommand(
    Guid ActorId,
    Guid OrganizationId,
    Guid OrderId,
    string RequestId);

public sealed record PublicTrackingTokenGrant(
    Guid TokenId,
    Guid OrderId,
    string Token,
    DateTimeOffset ExpiresAt);

public interface IPublicTrackingTokenService
{
    Task<PublicTrackingTokenGrant> IssueAsync(
        IssuePublicTrackingTokenCommand command,
        CancellationToken cancellationToken);

    Task<PublicTrackingTokenGrant> RotateAsync(
        RotatePublicTrackingTokenCommand command,
        CancellationToken cancellationToken);

    Task RevokeAsync(
        RevokePublicTrackingTokenCommand command,
        CancellationToken cancellationToken);
}

public sealed class PublicTrackingTokenConflictException : Exception
{
    public PublicTrackingTokenConflictException(string message)
        : base(message)
    {
    }
}

public sealed class PublicTrackingTokenNotFoundException : Exception
{
    public PublicTrackingTokenNotFoundException()
        : base("The order is unavailable.")
    {
    }
}

public sealed class PublicTrackingTokenInfrastructureException : Exception
{
    public PublicTrackingTokenInfrastructureException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
