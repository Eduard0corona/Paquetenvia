using Orders.Application.Tracking;
using Paqueteria.Application.Security;

namespace Paqueteria.SyntheticVerification;

public sealed record SyntheticTrackingVerificationRequest(
    Guid ActorId,
    Guid OrganizationId,
    Guid OrderId,
    string RunId);

/// <summary>
/// TRK-002-AUTO-LINK evidence: the link is stable across get-or-create calls, a revoked link stops resolving and
/// the next get-or-create derives the next generation. It holds identifiers only, never a token.
/// </summary>
public sealed record SyntheticTrackingVerificationResult(
    Guid ActorId,
    Guid OrganizationId,
    Guid OrderId,
    string RunId,
    string FirstRequestId,
    string RepeatRequestId,
    string RevokeRequestId,
    string NextRequestId,
    Guid FirstTokenId,
    Guid NextTokenId,
    int FirstGeneration,
    int NextGeneration,
    string PublicId,
    bool FirstLinkFound,
    bool RepeatReturnedSameLink,
    bool FirstLinkInvalidAfterRevoke,
    bool NextLinkValid);

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
        var revokeRequest = CreateRequestId(request.RunId, "revoke");
        var nextRequest = CreateRequestId(request.RunId, "next");
        PublicTrackingTokenGrant? first = null;
        PublicTrackingTokenGrant? repeat = null;
        PublicTrackingTokenGrant? next = null;
        string? firstToken = null;
        string? nextToken = null;
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
            var firstTokenId = first.TokenId;
            var firstGeneration = first.Generation;
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
            var sameLink = repeat.TokenId == firstTokenId &&
                repeat.Generation == firstGeneration &&
                string.Equals(repeat.Token, firstToken, StringComparison.Ordinal);
            repeat = null;
            if (!sameLink)
            {
                throw new SyntheticTrackingVerificationException(
                    "A repeated get-or-create did not return the same link.");
            }

            await tokenService.RevokeAsync(
                new RevokePublicTrackingTokenCommand(
                    request.ActorId,
                    request.OrganizationId,
                    request.OrderId,
                    revokeRequest),
                cancellationToken);
            var revokedLookup = await projectionReader.FindAsync(firstToken, cancellationToken);
            if (revokedLookup.IsFound)
            {
                throw new SyntheticTrackingVerificationException(
                    "The first link remained valid after revocation.");
            }

            next = await tokenService.GetOrCreateAsync(
                new GetOrCreatePublicTrackingLinkCommand(
                    request.ActorId,
                    request.OrganizationId,
                    request.OrderId,
                    nextRequest),
                cancellationToken);
            nextToken = next.Token;
            var nextTokenId = next.TokenId;
            var nextGeneration = next.Generation;
            next = null;
            if (nextGeneration != firstGeneration + 1 ||
                string.Equals(nextToken, firstToken, StringComparison.Ordinal))
            {
                throw new SyntheticTrackingVerificationException(
                    "The link issued after revocation is not the next generation.");
            }

            var supersededLookup = await projectionReader.FindAsync(firstToken, cancellationToken);
            firstToken = null;
            if (supersededLookup.IsFound)
            {
                throw new SyntheticTrackingVerificationException(
                    "The revoked link came back after the next generation.");
            }

            var nextLookup = await projectionReader.FindAsync(nextToken, cancellationToken);
            nextToken = null;
            if (!nextLookup.IsFound ||
                !string.Equals(nextLookup.Projection!.PublicId, publicId, StringComparison.Ordinal))
            {
                throw new SyntheticTrackingVerificationException(
                    "The next link did not produce the expected public projection.");
            }

            return new SyntheticTrackingVerificationResult(
                request.ActorId,
                request.OrganizationId,
                request.OrderId,
                request.RunId,
                firstRequest,
                repeatRequest,
                revokeRequest,
                nextRequest,
                firstTokenId,
                nextTokenId,
                firstGeneration,
                nextGeneration,
                publicId,
                FirstLinkFound: true,
                RepeatReturnedSameLink: true,
                FirstLinkInvalidAfterRevoke: true,
                NextLinkValid: true);
        }
        finally
        {
            firstToken = null;
            nextToken = null;
            first = null;
            repeat = null;
            next = null;
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
