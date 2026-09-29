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
    public async Task Guard_rejects_every_incomplete_or_non_synthetic_combination_before_any_link_call()
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
    public async Task Stable_link_that_keeps_resolving_returns_only_non_secret_evidence_and_emits_nothing()
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

            Assert.Equal(2, fake.GetOrCreateCalls);
            Assert.Equal(2, fake.LookupCalls);
            Assert.NotEqual(result.FirstRequestId, result.RepeatRequestId);
            Assert.Equal(1, result.Generation);
            Assert.Equal(fake.FirstTokenId, result.TokenId);
            Assert.True(result.FirstLinkFound);
            Assert.True(result.RepeatReturnedSameLink);
            Assert.True(result.LinkStillValidAfterRepeat);
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

    [Fact]
    public async Task A_get_or_create_that_rotates_the_link_fails_the_verification()
    {
        using var environment = new ProcessEnvironmentVariable("DOTNET_ENVIRONMENT", "DevSynthetic");
        using var deployment = new ProcessEnvironmentVariable(
            SyntheticEnvironmentPolicy.DeploymentClassVariable,
            SyntheticEnvironmentPolicy.DeploymentClass);
        using var optIn = new ProcessEnvironmentVariable(
            SyntheticTrackingVerifier.TrackingOptInVariable,
            "true");
        var fake = new TrackingFake { RotateOnEveryCall = true };

        await Assert.ThrowsAsync<SyntheticTrackingVerificationException>(
            () => new SyntheticTrackingVerifier(fake, fake).VerifyAsync(Request));
        Assert.Equal(2, fake.GetOrCreateCalls);
    }

    [Fact]
    public async Task A_link_that_stops_resolving_fails_the_verification()
    {
        using var environment = new ProcessEnvironmentVariable("DOTNET_ENVIRONMENT", "DevSynthetic");
        using var deployment = new ProcessEnvironmentVariable(
            SyntheticEnvironmentPolicy.DeploymentClassVariable,
            SyntheticEnvironmentPolicy.DeploymentClass);
        using var optIn = new ProcessEnvironmentVariable(
            SyntheticTrackingVerifier.TrackingOptInVariable,
            "true");
        var fake = new TrackingFake { StopResolvingAfterLookups = 1 };

        await Assert.ThrowsAsync<SyntheticTrackingVerificationException>(
            () => new SyntheticTrackingVerifier(fake, fake).VerifyAsync(Request));
        Assert.Equal(2, fake.LookupCalls);
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
        Assert.Equal(0, fake.GetOrCreateCalls);
        Assert.Equal(0, fake.LookupCalls);
    }

    private sealed class TrackingFake : IPublicTrackingTokenService, IPublicTrackingProjectionReader
    {
        internal const string TokenA = "SEC003_SENTINEL_PLAINTEXT_GENERATION_1_0001";
        internal const string TokenB = "SEC003_SENTINEL_PLAINTEXT_GENERATION_2_0002";
        private readonly Guid nextTokenId = Guid.NewGuid();
        private string? activeToken;
        private int generation;

        internal Guid FirstTokenId { get; } = Guid.NewGuid();
        internal bool RotateOnEveryCall { get; init; }
        internal int? StopResolvingAfterLookups { get; init; }
        internal int GetOrCreateCalls { get; private set; }
        internal int LookupCalls { get; private set; }

        public Task<PublicTrackingTokenGrant> GetOrCreateAsync(
            GetOrCreatePublicTrackingLinkCommand command,
            CancellationToken cancellationToken)
        {
            GetOrCreateCalls++;
            if (activeToken is null || RotateOnEveryCall)
            {
                generation++;
                activeToken = generation == 1 ? TokenA : TokenB;
            }

            return Task.FromResult(new PublicTrackingTokenGrant(
                generation == 1 ? FirstTokenId : nextTokenId,
                command.OrderId,
                activeToken,
                generation,
                null));
        }

        public ValueTask<PublicTrackingLookupResult> FindAsync(
            string token,
            CancellationToken cancellationToken)
        {
            LookupCalls++;
            var resolves = StopResolvingAfterLookups is not { } limit || LookupCalls <= limit;
            return ValueTask.FromResult(
                resolves && string.Equals(token, activeToken, StringComparison.Ordinal)
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
