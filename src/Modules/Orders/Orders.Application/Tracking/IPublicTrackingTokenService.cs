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
    DateTimeOffset ExpiresAt)
{
    /// <summary>
    /// TRK-002: the plaintext token is returned once and never logged, so the generated record text, which a
    /// structured log or an assertion message could capture, never includes it.
    /// </summary>
    public override string ToString() =>
        $"{nameof(PublicTrackingTokenGrant)} {{ TokenId = {TokenId}, OrderId = {OrderId}, Token = [redacted], ExpiresAt = {ExpiresAt:O} }}";
}

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
