using Orders.Application.Tracking;
using Paqueteria.Application.Security;

namespace Paqueteria.SyntheticVerification;

public sealed record SyntheticTrackingVerificationRequest(
    Guid ActorId,
    Guid OrganizationId,
    Guid OrderId,
    string RunId);

public sealed record SyntheticTrackingVerificationResult(
    Guid ActorId,
    Guid OrganizationId,
    Guid OrderId,
    string RunId,
    string RotationARequestId,
    string RotationBRequestId,
    Guid RotationATokenId,
    Guid RotationBTokenId,
    string PublicId,
    bool ProjectionAFound,
    bool ProjectionBFound,
    bool TokenAInvalidAfterRotationB,
    bool TokenBValidAfterRotationB);

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

        var requestA = CreateRequestId(request.RunId, "rotation-a");
        var requestB = CreateRequestId(request.RunId, "rotation-b");
        PublicTrackingTokenGrant? grantA = null;
        PublicTrackingTokenGrant? grantB = null;
        string? tokenA = null;
        string? tokenB = null;
        var tokenAId = Guid.Empty;
        var tokenBId = Guid.Empty;
        try
        {
            grantA = await tokenService.RotateAsync(
                new RotatePublicTrackingTokenCommand(
                    request.ActorId,
                    request.OrganizationId,
                    request.OrderId,
                    requestA),
                cancellationToken);
            tokenA = grantA.Token;
            tokenAId = grantA.TokenId;
            grantA = null;
            var lookupA = await projectionReader.FindAsync(tokenA, cancellationToken);
            if (!lookupA.IsFound)
            {
                throw new SyntheticTrackingVerificationException(
                    "Rotation A did not produce the expected public projection.");
            }

            var publicId = lookupA.Projection!.PublicId;
            grantB = await tokenService.RotateAsync(
                new RotatePublicTrackingTokenCommand(
                    request.ActorId,
                    request.OrganizationId,
                    request.OrderId,
                    requestB),
                cancellationToken);
            tokenB = grantB.Token;
            tokenBId = grantB.TokenId;
            grantB = null;
            var lookupB = await projectionReader.FindAsync(tokenB, cancellationToken);
            if (!lookupB.IsFound ||
                !string.Equals(lookupB.Projection!.PublicId, publicId, StringComparison.Ordinal))
            {
                throw new SyntheticTrackingVerificationException(
                    "Rotation B did not produce the expected public projection.");
            }

            var supersededLookup = await projectionReader.FindAsync(tokenA, cancellationToken);
            tokenA = null;
            if (supersededLookup.IsFound)
            {
                throw new SyntheticTrackingVerificationException(
                    "Rotation A remained valid after rotation B.");
            }

            var currentLookup = await projectionReader.FindAsync(tokenB, cancellationToken);
            tokenB = null;
            if (!currentLookup.IsFound ||
                !string.Equals(currentLookup.Projection!.PublicId, publicId, StringComparison.Ordinal))
            {
                throw new SyntheticTrackingVerificationException(
                    "Rotation B did not remain valid after verification.");
            }

            return new SyntheticTrackingVerificationResult(
                request.ActorId,
                request.OrganizationId,
                request.OrderId,
                request.RunId,
                requestA,
                requestB,
                tokenAId,
                tokenBId,
                publicId,
                ProjectionAFound: true,
                ProjectionBFound: true,
                TokenAInvalidAfterRotationB: true,
                TokenBValidAfterRotationB: true);
        }
        finally
        {
            tokenA = null;
            tokenB = null;
            grantA = null;
            grantB = null;
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

    private static string CreateRequestId(string runId, string rotation) =>
        $"{runId}:{rotation}:{Guid.NewGuid():N}";
}
