using Drivers.Application.Eligibility;

namespace Paqueteria.UnitTests.Drivers;

public sealed class DriverEligibilityPolicyTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 23, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Fully_compliant_own_driver_is_eligible()
    {
        var result = Evaluate();

        Assert.True(result.IsEligible);
        Assert.Equal(DriverId, result.DriverId);
        Assert.Equal("MOTORCYCLE", result.VehicleType);
        Assert.Equal("dsp-synthetic-v1", result.PolicyVersion);
        Assert.Empty(result.Rejections);
    }

    [Fact]
    public void Invisible_profile_returns_only_uniform_unavailable()
    {
        var result = DriverEligibilityPolicy.Evaluate(Command(), null, Policy());

        Assert.False(result.IsEligible);
        Assert.Null(result.DriverId);
        Assert.Null(result.VehicleType);
        Assert.Equal([DriverEligibilityRejectionCodes.DriverUnavailable], Codes(result));
    }

    [Fact]
    public void Rejections_follow_the_normative_category_order()
    {
        var snapshot = Snapshot() with
        {
            DriverType = "EXTERNAL",
            ProfileStatus = "SUSPENDED",
            UserStatus = "INACTIVE",
            HasActiveDriverMembership = false,
            HomeCityId = Guid.NewGuid(),
            ServiceAreaEligible = false,
            LatestDocuments = new Dictionary<string, DriverDocumentSnapshot>(),
        };
        var command = Command() with
        {
            ServiceAreaId = Guid.NewGuid(),
            Capacity = new DriverCapacityRequirement(3, 2_001, 1_500, 400, 300, 200),
        };

        var result = Evaluate(command, snapshot);

        Assert.Equal(
        [
            DriverEligibilityRejectionCodes.DriverTypeNotOwn,
            DriverEligibilityRejectionCodes.DriverStatusNotActive,
            DriverEligibilityRejectionCodes.UserNotActive,
            DriverEligibilityRejectionCodes.DriverMembershipNotActive,
            DriverEligibilityRejectionCodes.HomeCityMismatch,
            DriverEligibilityRejectionCodes.ServiceAreaNotEligible,
            DriverEligibilityRejectionCodes.RequiredDocumentMissing,
            DriverEligibilityRejectionCodes.PackageCountExceeded,
            DriverEligibilityRejectionCodes.TotalWeightExceeded,
            DriverEligibilityRejectionCodes.SinglePackageWeightExceeded,
            DriverEligibilityRejectionCodes.PackageLengthExceeded,
            DriverEligibilityRejectionCodes.PackageWidthExceeded,
            DriverEligibilityRejectionCodes.PackageHeightExceeded,
        ], Codes(result));
    }

    [Theory]
    [InlineData("PENDING", 32, 1, DriverEligibilityRejectionCodes.DocumentStatusNotValid)]
    [InlineData("VALID", 31, 1, DriverEligibilityRejectionCodes.DocumentHashInvalid)]
    [InlineData("VALID", 32, 0, DriverEligibilityRejectionCodes.DocumentExpired)]
    [InlineData("VALID", 32, -1, DriverEligibilityRejectionCodes.DocumentExpired)]
    public void Latest_required_document_fails_closed(
        string status,
        int hashLength,
        int expiryMinutes,
        string expected)
    {
        var document = Document(status: status, hashLength: hashLength, expiresAt: Now.AddMinutes(expiryMinutes));
        var snapshot = Snapshot() with
        {
            LatestDocuments = new Dictionary<string, DriverDocumentSnapshot>
            {
                ["IDENTITY"] = document,
            },
        };

        var result = Evaluate(snapshot: snapshot);

        Assert.Contains(expected, Codes(result));
        Assert.False(result.IsEligible);
    }

    [Fact]
    public void Expiry_is_required_unless_document_type_is_explicitly_non_expiring()
    {
        var snapshot = Snapshot() with
        {
            LatestDocuments = new Dictionary<string, DriverDocumentSnapshot>
            {
                ["IDENTITY"] = Document(hasExpiry: false),
            },
        };

        var blocked = Evaluate(snapshot: snapshot);
        var allowed = DriverEligibilityPolicy.Evaluate(
            Command(),
            snapshot,
            Policy(nonExpiring: ["IDENTITY"]));

        Assert.Equal([DriverEligibilityRejectionCodes.DocumentExpiryMissing], Codes(blocked));
        Assert.True(allowed.IsEligible);
    }

    [Fact]
    public void Blank_object_key_and_wrong_hash_emit_only_non_pii_hash_code()
    {
        var snapshot = Snapshot() with
        {
            LatestDocuments = new Dictionary<string, DriverDocumentSnapshot>
            {
                ["IDENTITY"] = Document(objectKey: " ", hashLength: 5),
            },
        };

        var result = Evaluate(snapshot: snapshot);

        Assert.Equal([DriverEligibilityRejectionCodes.DocumentHashInvalid], Codes(result));
        Assert.DoesNotContain(result.Rejections, rejection => rejection.Code.Contains("object", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [MemberData(nameof(CapacityCases))]
    public void Capacity_limits_are_evaluated_independently(
        DriverCapacityRequirement requirement,
        string expected)
    {
        var result = Evaluate(Command() with { Capacity = requirement });

        Assert.Contains(expected, Codes(result));
    }

    public static TheoryData<DriverCapacityRequirement, string> CapacityCases => new()
    {
        { new(3, 1_000, 700, 100, 100, 100), DriverEligibilityRejectionCodes.PackageCountExceeded },
        { new(1, 2_001, 700, 100, 100, 100), DriverEligibilityRejectionCodes.TotalWeightExceeded },
        { new(1, 1_001, 1_001, 100, 100, 100), DriverEligibilityRejectionCodes.SinglePackageWeightExceeded },
        { new(1, 500, 500, 301, 100, 100), DriverEligibilityRejectionCodes.PackageLengthExceeded },
        { new(1, 500, 500, 100, 201, 100), DriverEligibilityRejectionCodes.PackageWidthExceeded },
        { new(1, 500, 500, 100, 100, 151), DriverEligibilityRejectionCodes.PackageHeightExceeded },
    };

    [Fact]
    public void Missing_required_dimensions_use_the_corresponding_dimension_codes()
    {
        var result = Evaluate(Command() with
        {
            Capacity = new DriverCapacityRequirement(1, 500, 500, null, null, null),
        });

        Assert.Equal(
        [
            DriverEligibilityRejectionCodes.PackageLengthExceeded,
            DriverEligibilityRejectionCodes.PackageWidthExceeded,
            DriverEligibilityRejectionCodes.PackageHeightExceeded,
        ], Codes(result));
    }

    [Theory]
    [MemberData(nameof(InvalidCapacityCases))]
    public void Invalid_package_requirements_fail_before_limit_comparisons(DriverCapacityRequirement requirement)
    {
        var result = Evaluate(Command() with { Capacity = requirement });

        Assert.Equal([DriverEligibilityRejectionCodes.PackageRequirementInvalid], Codes(result));
    }

    public static TheoryData<DriverCapacityRequirement> InvalidCapacityCases => new()
    {
        new(0, 500, 500, 100, 100, 100),
        new(1, 0, 1, 100, 100, 100),
        new(1, 500, 501, 100, 100, 100),
        new(1, 500, 500, 0, 100, 100),
        new(1, 500, 500, 100, -1, 100),
    };

    [Fact]
    public void Missing_policy_and_default_evaluation_time_fail_closed()
    {
        var emptyPolicy = Policy() with
        {
            RequiredDocumentTypesByVehicleType =
                new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal),
            VehicleCapacity =
                new Dictionary<string, VehicleCapacityLimits>(StringComparer.Ordinal),
        };
        var result = DriverEligibilityPolicy.Evaluate(
            Command() with { EvaluatedAt = default },
            Snapshot(),
            emptyPolicy);

        Assert.Equal(
        [
            DriverEligibilityRejectionCodes.DocumentPolicyUnavailable,
            DriverEligibilityRejectionCodes.VehicleCapacityPolicyUnavailable,
        ], Codes(result));
    }

    [Fact]
    public void Null_service_area_is_allowed_and_does_not_require_area_evaluation()
    {
        var result = Evaluate(Command() with { ServiceAreaId = null }, Snapshot() with { ServiceAreaEligible = null });

        Assert.True(result.IsEligible);
    }

    private static readonly Guid DriverId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid UserId = Guid.Parse("10000000-0000-0000-0000-000000000002");
    private static readonly Guid OrganizationId = Guid.Parse("10000000-0000-0000-0000-000000000003");
    private static readonly Guid CityId = Guid.Parse("10000000-0000-0000-0000-000000000004");

    private static DriverEligibilityResult Evaluate(
        EvaluateOwnDriverEligibilityCommand? command = null,
        DriverEligibilitySnapshot? snapshot = null) =>
        DriverEligibilityPolicy.Evaluate(command ?? Command(), snapshot ?? Snapshot(), Policy());

    private static EvaluateOwnDriverEligibilityCommand Command() => new(
        UserId,
        OrganizationId,
        DriverId,
        CityId,
        null,
        new DriverCapacityRequirement(1, 500, 500, 100, 100, 100),
        Now);

    private static DriverEligibilitySnapshot Snapshot() => new(
        DriverId,
        OrganizationId,
        UserId,
        CityId,
        "OWN",
        "MOTORCYCLE",
        "ACTIVE",
        "ACTIVE",
        true,
        null,
        new Dictionary<string, DriverDocumentSnapshot>(StringComparer.Ordinal)
        {
            ["IDENTITY"] = Document(),
        });

    private static DriverDocumentSnapshot Document(
        string status = "VALID",
        string objectKey = "synthetic/identity",
        int hashLength = 32,
        DateTimeOffset? expiresAt = null,
        bool hasExpiry = true) =>
        new("IDENTITY", status, objectKey, new byte[hashLength],
            hasExpiry ? expiresAt ?? Now.AddDays(1) : null);

    private static DriverEligibilityPolicyConfiguration Policy(string[]? nonExpiring = null) => new(
        "dsp-synthetic-v1",
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["MOTORCYCLE"] = ["IDENTITY"],
        },
        (nonExpiring ?? []).ToHashSet(StringComparer.Ordinal),
        new Dictionary<string, VehicleCapacityLimits>(StringComparer.Ordinal)
        {
            ["MOTORCYCLE"] = new(2, 2_000, 1_000, 300, 200, 150, true),
        });

    private static string[] Codes(DriverEligibilityResult result) =>
        result.Rejections.Select(rejection => rejection.Code).ToArray();
}
