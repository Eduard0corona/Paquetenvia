using System.Text.Json;
using Orders.Application.Tracking;
using Paqueteria.Application.Security;
using Paqueteria.SyntheticVerification;
using Paqueteria.UnitTests.Security;

namespace Paqueteria.UnitTests.Tracking;

[Collection(ProcessEnvironmentCollection.Name)]
public sealed class SyntheticTrackingVerifierTests
{
    private static readonly SyntheticTrackingVerificationRequest Request = new(
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa1"),
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Guid.Parse("66666666-6666-6666-6666-666666666666"),
        "sec003-unit-run");

    [Fact]
    public async Task Guard_rejects_every_incomplete_or_non_synthetic_combination_before_rotation()
    {
        await AssertRejectedAsync(null, "DEV_SYNTHETIC", "true");
        await AssertRejectedAsync("DevSynthetic", null, "true");
        await AssertRejectedAsync("DevSynthetic", "PRODUCTION", "true");
        await AssertRejectedAsync("DevSynthetic", "DEV_SYNTHETIC", null);
        await AssertRejectedAsync("Production", "DEV_SYNTHETIC", "true");
        await AssertRejectedAsync("Staging", "DEV_SYNTHETIC", "true");
        await AssertRejectedAsync("Testing", "DEV_SYNTHETIC", "true");
        await AssertRejectedAsync("Development", "DEV_SYNTHETIC", "true");
    }

    [Fact]
    public async Task Double_rotation_returns_only_non_secret_evidence_and_emits_nothing()
    {
        using var environment = new ProcessEnvironmentVariable("DOTNET_ENVIRONMENT", "DevSynthetic");
        using var deployment = new ProcessEnvironmentVariable(
            SyntheticEnvironmentPolicy.DeploymentClassVariable,
            SyntheticEnvironmentPolicy.DeploymentClass);
        using var optIn = new ProcessEnvironmentVariable(
            SyntheticTrackingVerifier.TrackingOptInVariable,
            "true");
        var fake = new TrackingFake();
        var verifier = new SyntheticTrackingVerifier(fake, fake);
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            var result = await verifier.VerifyAsync(Request);

            Assert.Equal(2, fake.RotateCalls);
            Assert.Equal(4, fake.LookupCalls);
            Assert.NotEqual(result.RotationARequestId, result.RotationBRequestId);
            Assert.True(result.ProjectionAFound);
            Assert.True(result.ProjectionBFound);
            Assert.True(result.TokenAInvalidAfterRotationB);
            Assert.True(result.TokenBValidAfterRotationB);
            var evidence = JsonSerializer.Serialize(result);
            Assert.DoesNotContain(TrackingFake.TokenA, evidence, StringComparison.Ordinal);
            Assert.DoesNotContain(TrackingFake.TokenB, evidence, StringComparison.Ordinal);
            Assert.DoesNotContain(TrackingFake.TokenA, result.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(TrackingFake.TokenB, result.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }

        Assert.Equal(string.Empty, output.ToString());
        Assert.Equal(string.Empty, error.ToString());
    }

    private static async Task AssertRejectedAsync(
        string? environmentName,
        string? deploymentClass,
        string? optIn)
    {
        using var environment = new ProcessEnvironmentVariable("DOTNET_ENVIRONMENT", environmentName);
        using var deployment = new ProcessEnvironmentVariable(
            SyntheticEnvironmentPolicy.DeploymentClassVariable,
            deploymentClass);
        using var tracking = new ProcessEnvironmentVariable(
            SyntheticTrackingVerifier.TrackingOptInVariable,
            optIn);
        var fake = new TrackingFake();
        var verifier = new SyntheticTrackingVerifier(fake, fake);

        await Assert.ThrowsAsync<SyntheticTrackingVerificationUnauthorizedException>(
            () => verifier.VerifyAsync(Request));
        Assert.Equal(0, fake.RotateCalls);
        Assert.Equal(0, fake.LookupCalls);
    }

    private sealed class TrackingFake : IPublicTrackingTokenService, IPublicTrackingProjectionReader
    {
        internal const string TokenA = "SEC003_SENTINEL_PLAINTEXT_ROTATION_A_0000001";
        internal const string TokenB = "SEC003_SENTINEL_PLAINTEXT_ROTATION_B_0000002";
        private string? activeToken;

        internal int RotateCalls { get; private set; }
        internal int LookupCalls { get; private set; }

        public Task<PublicTrackingTokenGrant> IssueAsync(
            IssuePublicTrackingTokenCommand command,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PublicTrackingTokenGrant> RotateAsync(
            RotatePublicTrackingTokenCommand command,
            CancellationToken cancellationToken)
        {
            RotateCalls++;
            activeToken = RotateCalls == 1 ? TokenA : TokenB;
            return Task.FromResult(new PublicTrackingTokenGrant(
                Guid.NewGuid(),
                command.OrderId,
                activeToken,
                DateTimeOffset.UtcNow.AddHours(1)));
        }

        public Task RevokeAsync(
            RevokePublicTrackingTokenCommand command,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<PublicTrackingLookupResult> FindAsync(
            string token,
            CancellationToken cancellationToken)
        {
            LookupCalls++;
            return ValueTask.FromResult(
                string.Equals(token, activeToken, StringComparison.Ordinal)
                    ? PublicTrackingLookupResult.Found(new PublicTrackingProjection(
                        "ORD_sec003synthetic",
                        PublicOrderStatus.Delivered,
                        1,
                        null,
                        []))
                    : PublicTrackingLookupResult.NotFound);
        }
    }
}
