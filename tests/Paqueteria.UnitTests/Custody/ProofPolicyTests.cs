using System.Text;
using Custody.Application.ProofUploads;
using Custody.Domain;
using Custody.Infrastructure.ProofStorage;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Paqueteria.UnitTests.Custody;

public sealed class ProofPolicyTests
{
    public static TheoryData<string, ProofType> ProofTypes => new()
    {
        { "PICKUP_PHOTO", ProofType.PickupPhoto },
        { "DELIVERY_PHOTO", ProofType.DeliveryPhoto },
        { "SIGNATURE", ProofType.Signature },
        { "DELIVERY_CODE", ProofType.DeliveryCode },
        { "RETURN_PHOTO", ProofType.ReturnPhoto },
    };

    [Theory]
    [MemberData(nameof(ProofTypes))]
    public void Exact_proof_types_round_trip(string value, ProofType expected)
    {
        Assert.True(ProofContract.TryParse(value, out var parsed));
        Assert.Equal(expected, parsed);
        Assert.Equal(value, parsed.ToContractValue());
        Assert.True(ProofRequestPolicy.IsSupportedProofType(value));
    }

    [Theory]
    [InlineData("")]
    [InlineData("pickup_photo")]
    [InlineData("PHOTO")]
    [InlineData("DELIVERY_SIGNATURE")]
    public void Unknown_proof_types_fail_closed(string value)
    {
        Assert.False(ProofContract.TryParse(value, out _));
        Assert.False(ProofRequestPolicy.IsSupportedProofType(value));
    }

    [Theory]
    [InlineData(ProofType.PickupPhoto, "AT_PICKUP", true)]
    [InlineData(ProofType.PickupPhoto, "DELIVERING", false)]
    [InlineData(ProofType.DeliveryPhoto, "DELIVERING", true)]
    [InlineData(ProofType.Signature, "DELIVERING", true)]
    [InlineData(ProofType.DeliveryCode, "DELIVERING", true)]
    [InlineData(ProofType.ReturnPhoto, "RETURNING", true)]
    [InlineData(ProofType.ReturnPhoto, "DELIVERING", false)]
    public void Order_state_matrix_is_exact(ProofType proofType, string status, bool allowed) =>
        Assert.Equal(allowed, ProofContract.IsAllowedOrderState(proofType, status));

    [Theory]
    [InlineData(ProofType.PickupPhoto, "image/jpeg", true)]
    [InlineData(ProofType.PickupPhoto, "image/png", true)]
    [InlineData(ProofType.PickupPhoto, "text/plain", false)]
    [InlineData(ProofType.Signature, "image/png", true)]
    [InlineData(ProofType.Signature, "image/jpeg", false)]
    [InlineData(ProofType.DeliveryCode, "text/plain", true)]
    [InlineData(ProofType.DeliveryCode, "image/png", false)]
    public void Content_type_matrix_is_exact(ProofType proofType, string contentType, bool allowed) =>
        Assert.Equal(allowed, ProofContentPolicy.IsAllowed(proofType, contentType));

    [Fact]
    public void Magic_bytes_validate_jpeg_png_and_utf8_text()
    {
        Assert.True(ProofContentPolicy.MatchesMagicBytes("image/jpeg", [0xff, 0xd8, 0xff, 0x00]));
        Assert.True(ProofContentPolicy.MatchesMagicBytes(
            "image/png",
            [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]));
        Assert.True(ProofContentPolicy.MatchesMagicBytes("text/plain", Encoding.UTF8.GetBytes("ABC-123")));
        Assert.False(ProofContentPolicy.MatchesMagicBytes("image/png", Encoding.UTF8.GetBytes("not png")));
        Assert.False(ProofContentPolicy.MatchesMagicBytes("text/plain", [0x41, 0x00, 0x42]));
    }

    [Fact]
    public void Sha256_requires_exact_64_hex_characters()
    {
        var value = new string('a', 64);
        Assert.True(ProofSha256.TryParseHex(value, out var domainBytes));
        Assert.True(ProofRequestPolicy.TryParseSha256(value, out var applicationBytes));
        Assert.True(ProofSha256.FixedTimeEquals(domainBytes, applicationBytes));
        Assert.False(ProofSha256.TryParseHex(new string('a', 63), out _));
        Assert.False(ProofRequestPolicy.TryParseSha256(new string('z', 64), out _));
    }

    [Fact]
    public void Captured_at_requires_utc_and_is_canonicalized_to_microseconds()
    {
        var input = new DateTimeOffset(638890416001234567, TimeSpan.Zero);
        Assert.True(ProofCapturedAtPolicy.TryNormalizeUtc(input, out var normalized));
        Assert.Equal(0, normalized.UtcTicks % 10);
        Assert.Equal(input.UtcTicks - 7, normalized.UtcTicks);
        Assert.False(ProofCapturedAtPolicy.TryNormalizeUtc(
            input.ToOffset(TimeSpan.FromHours(-6)),
            out _));
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData(28.0, -106.0, true)]
    [InlineData(28.0, null, false)]
    [InlineData(null, -106.0, false)]
    [InlineData(91.0, -106.0, false)]
    [InlineData(28.0, -181.0, false)]
    public void Coordinates_require_a_valid_pair(
        double? latitude,
        double? longitude,
        bool expected) =>
        Assert.Equal(expected, ProofLocationPolicy.IsValid(latitude, longitude));

    [Fact]
    public void Object_keys_are_deterministic_and_separate_quarantine_from_final()
    {
        var owner = Guid.Parse("10000000-0000-0000-0000-000000000001");
        var order = Guid.Parse("20000000-0000-0000-0000-000000000002");
        var session = Guid.Parse("30000000-0000-0000-0000-000000000003");

        Assert.Equal(
            "quarantine/10000000-0000-0000-0000-000000000001/20000000-0000-0000-0000-000000000002/30000000-0000-0000-0000-000000000003",
            ProofObjectKeys.Quarantine(owner, order, session));
        Assert.Equal(
            "proofs/10000000-0000-0000-0000-000000000001/20000000-0000-0000-0000-000000000002/30000000-0000-0000-0000-000000000003",
            ProofObjectKeys.Final(owner, order, session));
    }

    [Fact]
    public async Task Synthetic_scanner_rejects_eicar_and_accepts_safe_content()
    {
        var scanner = new StrictSyntheticThreatScanner();
        await using var safe = new MemoryStream(Encoding.ASCII.GetBytes("safe proof"));
        Assert.True((await scanner.ScanAsync(safe, default)).IsSafe);

        await using var eicar = new MemoryStream(
            Encoding.ASCII.GetBytes("prefix EICAR-STANDARD-ANTIVIRUS-TEST-FILE suffix"));
        var result = await scanner.ScanAsync(eicar, default);
        Assert.False(result.IsSafe);
        Assert.Equal("THREAT_DETECTED", result.Code);
    }

    [Fact]
    public async Task Disabled_storage_and_scanner_are_neutral_when_proof_operations_are_disabled()
    {
        var storage = new DisabledProofObjectStorage();
        var environment = new TestHostEnvironment("Testing");
        var storageHealth = await new ProofStorageHealthCheck(storage, environment)
            .CheckHealthAsync(new HealthCheckContext());
        var scannerHealth = await new ProofScannerHealthCheck(
                new DisabledProofThreatScanner(),
                storage,
                environment)
            .CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, storageHealth.Status);
        Assert.Equal(HealthStatus.Healthy, scannerHealth.Status);
    }

    [Fact]
    public async Task Production_reports_disabled_storage_and_scanner_as_unhealthy()
    {
        var storage = new DisabledProofObjectStorage();
        var environment = new TestHostEnvironment(Environments.Production);
        var storageHealth = await new ProofStorageHealthCheck(storage, environment)
            .CheckHealthAsync(new HealthCheckContext());
        var scannerHealth = await new ProofScannerHealthCheck(
                new DisabledProofThreatScanner(),
                storage,
                environment)
            .CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, storageHealth.Status);
        Assert.Equal(HealthStatus.Unhealthy, scannerHealth.Status);
    }

    [Fact]
    public void Upload_session_transition_graph_is_closed()
    {
        Assert.True(ProofUploadSessionTransitions.IsAllowed(
            ProofUploadSessionStatus.Created,
            ProofUploadSessionStatus.Uploaded));
        Assert.True(ProofUploadSessionTransitions.IsAllowed(
            ProofUploadSessionStatus.Validating,
            ProofUploadSessionStatus.Ready));
        Assert.True(ProofUploadSessionTransitions.IsAllowed(
            ProofUploadSessionStatus.Ready,
            ProofUploadSessionStatus.Consumed));
        Assert.False(ProofUploadSessionTransitions.IsAllowed(
            ProofUploadSessionStatus.Created,
            ProofUploadSessionStatus.Ready));
        Assert.False(ProofUploadSessionTransitions.IsAllowed(
            ProofUploadSessionStatus.Consumed,
            ProofUploadSessionStatus.Ready));
    }

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = nameof(ProofPolicyTests);

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
