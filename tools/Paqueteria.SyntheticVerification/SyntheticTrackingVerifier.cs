using Orders.Application.Tracking;
using Paqueteria.Application.Security;

namespace Paqueteria.SyntheticVerification;

public sealed record SyntheticTrackingVerificationRequest(
    Guid ActorId,
    Guid OrganizationId,
    Guid OrderId,
    string RunId);

/// <summary>
/// TRK-002-AUTO-LINK evidence: the link resolves and is stable across get-or-create calls with different keys, and
/// it keeps resolving afterwards (TRK-002-NO-REVOCATION: nobody revokes a link). It holds identifiers only, never a
/// token.
/// </summary>
public sealed record SyntheticTrackingVerificationResult(
    Guid ActorId,
    Guid OrganizationId,
    Guid OrderId,
    string RunId,
    string FirstRequestId,
    string RepeatRequestId,
    Guid TokenId,
    int Generation,
    string PublicId,
    bool FirstLinkFound,
    bool RepeatReturnedSameLink,
    bool LinkStillValidAfterRepeat);

public sealed class SyntheticTrackingVerificationUnauthorizedException : Exception
{
    public SyntheticTrackingVerificationUnauthorizedException()
        : base("Synthetic tracking verification is not authorized for this process.")
    {
    }
}

public sealed class SyntheticTrackingVerificationException : Exception
{
    public SyntheticTrackingVerificationException(string message)
        : base(message)
    {
    }
}

public sealed class SyntheticTrackingVerifier(
    IPublicTrackingTokenService tokenService,
    IPublicTrackingProjectionReader projectionReader)
{
    public const string TrackingOptInVariable = "PAQUETERIA_SYNTHETIC_TRACKING_ENABLED";

    public async Task<SyntheticTrackingVerificationResult> VerifyAsync(
        SyntheticTrackingVerificationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureAuthorized();
        Validate(request);

        var firstRequest = CreateRequestId(request.RunId, "first");
        var repeatRequest = CreateRequestId(request.RunId, "repeat");
        PublicTrackingTokenGrant? first = null;
        PublicTrackingTokenGrant? repeat = null;
        string? firstToken = null;
        try
        {
            first = await tokenService.GetOrCreateAsync(
                new GetOrCreatePublicTrackingLinkCommand(
                    request.ActorId,
                    request.OrganizationId,
                    request.OrderId,
                    firstRequest),
                cancellationToken);
            firstToken = first.Token;
            var tokenId = first.TokenId;
            var generation = first.Generation;
            first = null;
            var firstLookup = await projectionReader.FindAsync(firstToken, cancellationToken);
            if (!firstLookup.IsFound)
            {
                throw new SyntheticTrackingVerificationException(
                    "The first link did not produce the expected public projection.");
            }

            var publicId = firstLookup.Projection!.PublicId;

            // Another Idempotency-Key must return the same link: get-or-create never rotates.
            repeat = await tokenService.GetOrCreateAsync(
                new GetOrCreatePublicTrackingLinkCommand(
                    request.ActorId,
                    request.OrganizationId,
                    request.OrderId,
                    repeatRequest),
                cancellationToken);
            var sameLink = repeat.TokenId == tokenId &&
                repeat.Generation == generation &&
                string.Equals(repeat.Token, firstToken, StringComparison.Ordinal);
            repeat = null;
            if (!sameLink)
            {
                throw new SyntheticTrackingVerificationException(
                    "A repeated get-or-create did not return the same link.");
            }

            // TRK-002-NO-REVOCATION: nothing retires a live link, so it still resolves to the same order.
            var repeatLookup = await projectionReader.FindAsync(firstToken, cancellationToken);
            firstToken = null;
            if (!repeatLookup.IsFound ||
                !string.Equals(repeatLookup.Projection!.PublicId, publicId, StringComparison.Ordinal))
            {
                throw new SyntheticTrackingVerificationException(
                    "The link stopped producing the expected public projection.");
            }

            return new SyntheticTrackingVerificationResult(
                request.ActorId,
                request.OrganizationId,
                request.OrderId,
                request.RunId,
                firstRequest,
                repeatRequest,
                tokenId,
                generation,
                publicId,
                FirstLinkFound: true,
                RepeatReturnedSameLink: true,
                LinkStillValidAfterRepeat: true);
        }
        finally
        {
            firstToken = null;
            first = null;
            repeat = null;
        }
    }

    private static void EnsureAuthorized()
    {
        var environmentName = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        if (!SyntheticEnvironmentPolicy.IsDevSynthetic(environmentName) ||
            !string.Equals(
                Environment.GetEnvironmentVariable(TrackingOptInVariable),
                "true",
                StringComparison.Ordinal))
        {
            throw new SyntheticTrackingVerificationUnauthorizedException();
        }
    }

    private static void Validate(SyntheticTrackingVerificationRequest request)
    {
        if (request.ActorId == Guid.Empty ||
            request.OrganizationId == Guid.Empty ||
            request.OrderId == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.RunId) ||
            request.RunId.Length > 100 ||
            request.RunId.Any(static value =>
                !char.IsAsciiLetterOrDigit(value) && value is not '-' and not '_' and not '.'))
        {
            throw new SyntheticTrackingVerificationException(
                "Synthetic tracking verification identifiers are invalid.");
        }
    }

    private static string CreateRequestId(string runId, string step) =>
        $"{runId}:{step}:{Guid.NewGuid():N}";
}
