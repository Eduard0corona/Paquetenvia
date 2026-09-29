namespace Orders.Application.Tracking;

/// <summary>
/// TRK-002-AUTO-LINK: returns the order's current public tracking link, re-derived from its generation, and
/// creates one only when the order has none that can still be shown. It never rotates a live link.
/// TRK-002-NO-REVOCATION: nobody revokes a link; it lives until 24 hours after the order reaches a final public
/// status, so the service has no revocation.
/// </summary>
public sealed record GetOrCreatePublicTrackingLinkCommand(
    Guid ActorId,
    Guid OrganizationId,
    Guid OrderId,
    string RequestId);

/// <param name="TokenId">The token row of the current generation.</param>
/// <param name="OrderId">The order the link shows.</param>
/// <param name="Token">The plaintext token, re-derived on every call and never stored or logged.</param>
/// <param name="Generation">
/// The link generation: 1 for the link written with the order; it grows only when a pre-derivation token or a link
/// of a key version no longer configured is replaced, and a retired generation is never derived again.
/// </param>
/// <param name="ValidUntil">
/// Null while the order is in progress; once it is DELIVERED, RETURNED or CANCELLED for the public, the end of
/// the 24-hour grace after which the public lookup answers the uniform 404.
/// </param>
public sealed record PublicTrackingTokenGrant(
    Guid TokenId,
    Guid OrderId,
    string Token,
    int Generation,
    DateTimeOffset? ValidUntil)
{
    /// <summary>
    /// The plaintext token is never logged, so the generated record text, which a structured log or an assertion
    /// message could capture, never includes it.
    /// </summary>
    public override string ToString() =>
        $"{nameof(PublicTrackingTokenGrant)} {{ TokenId = {TokenId}, OrderId = {OrderId}, Token = [redacted], Generation = {Generation}, ValidUntil = {ValidUntil:O} }}";
}

public interface IPublicTrackingTokenService
{
    Task<PublicTrackingTokenGrant> GetOrCreateAsync(
        GetOrCreatePublicTrackingLinkCommand command,
        CancellationToken cancellationToken);
}

public sealed class PublicTrackingTokenConflictException : Exception
{
    public PublicTrackingTokenConflictException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// TRK-002-AUTO-LINK: the order is DELIVERED, RETURNED or CANCELLED for the public and has no link that can still
/// be shown, so no new one is created (AI-05 problem code <c>TRACKING_LINK_ORDER_FINISHED</c>).
/// </summary>
public sealed class PublicTrackingLinkOrderFinishedException : Exception
{
    public const string ProblemCode = "TRACKING_LINK_ORDER_FINISHED";

    public PublicTrackingLinkOrderFinishedException()
        : base("The order is finished and gets no new public tracking link.")
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
