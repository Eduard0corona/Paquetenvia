using Drivers.Application.Eligibility;

namespace Paqueteria.UnitTests.Drivers;

/// <summary>
/// DRV-ELIGIBILITY-SORTED-DIMENSIONS-2026-10-20 (owner, H4: "Que el sistema las ordene"): before comparing, the policy
/// sorts each package's three sides and the vehicle's three limits from largest to smallest, so a package is judged by
/// whether it fits, not by the box each number was typed in. Dispatch (picker, assignDriver, external offers), Orders
/// (<c>capacity_available</c>) and Routing (<c>addRouteStop</c>) all reach this rule through
/// <see cref="DriverEligibilityPolicy"/>.
/// </summary>
public sealed class DriverEligibilitySortedSidesTests
{
    private const string Length = DriverEligibilityRejectionCodes.PackageLengthExceeded;
    private const string Width = DriverEligibilityRejectionCodes.PackageWidthExceeded;
    private const string Height = DriverEligibilityRejectionCodes.PackageHeightExceeded;

    private static readonly DateTimeOffset Now = new(2026, 10, 20, 18, 0, 0, TimeSpan.Zero);
    private static readonly Guid DriverId = Guid.Parse("4b400000-0000-4000-8000-000000000001");
    private static readonly Guid OrganizationId = Guid.Parse("4b400000-0000-4000-8000-000000000002");
    private static readonly Guid CityId = Guid.Parse("4b400000-0000-4000-8000-000000000003");

    /// <summary>The H2 bicycle and walker limits, 45 x 40 x 30 cm.</summary>
    private static readonly (int Length, int Width, int Height) Bicycle = (450, 400, 300);

    /// <summary>The H2 motorcycle limits, 40 x 40 x 35 cm: two equal limits.</summary>
    private static readonly (int Length, int Width, int Height) Motorcycle = (400, 400, 350);

    [Theory]
    [InlineData(450, 300, 200)]
    [InlineData(450, 200, 300)]
    [InlineData(300, 450, 200)]
    [InlineData(300, 200, 450)]
    [InlineData(200, 450, 300)]
    [InlineData(200, 300, 450)]
    public void A_package_fits_whichever_box_each_side_was_typed_in(int length, int width, int height)
    {
        Assert.Empty(Codes(Bicycle, requireDimensions: true, Sides(length, width, height)));
    }

    [Fact]
    public void The_30_by_45_package_fits_a_45_by_40_limit_when_turned()
    {
        // H4 question: "Un paquete de 30×45 capturado al revés" - length 30 cm, width 45 cm. Compared box by box it
        // would exceed the 40 cm width; turned, the 45 cm side takes the 45 cm limit.
        Assert.Empty(Codes(Bicycle, requireDimensions: false, Sides(300, 450, null)));
        Assert.Empty(Codes(Bicycle, requireDimensions: true, Sides(300, 450, 100)));
        Assert.Empty(Codes(Bicycle, requireDimensions: false, Sides(null, 450, 300)));
    }

    [Theory]
    [InlineData(450, 400, 300)]
    [InlineData(300, 450, 400)]
    [InlineData(400, 300, 450)]
    public void The_limits_may_be_written_in_any_order(int maximumLength, int maximumWidth, int maximumHeight)
    {
        (int, int, int) limits = (maximumLength, maximumWidth, maximumHeight);

        Assert.Empty(Codes(limits, requireDimensions: true, Sides(200, 450, 300)));
        Assert.Empty(Codes(limits, requireDimensions: true, Sides(400, 300, 450)));
        Assert.Equal([Width], Codes(limits, requireDimensions: true, Sides(410, 410, 100)));
    }

    [Theory]
    [MemberData(nameof(MisfitCases))]
    public void A_side_that_does_not_fit_reports_the_code_of_its_place_in_the_sorted_order(
        PackageSides sides,
        string[] expected)
    {
        Assert.Equal(expected, Codes(Bicycle, requireDimensions: true, sides));
    }

    public static TheoryData<PackageSides, string[]> MisfitCases()
    {
        var cases = new TheoryData<PackageSides, string[]>();
        // Every order of the same three sides reports the same codes.
        foreach (var (a, b, c) in Permutations(451, 300, 200))
        {
            cases.Add(Sides(a, b, c), [Length]);
        }

        foreach (var (a, b, c) in Permutations(450, 401, 200))
        {
            cases.Add(Sides(a, b, c), [Width]);
        }

        foreach (var (a, b, c) in Permutations(450, 400, 301))
        {
            cases.Add(Sides(a, b, c), [Height]);
        }

        foreach (var (a, b, c) in Permutations(451, 401, 301))
        {
            cases.Add(Sides(a, b, c), [Length, Width, Height]);
        }

        // 420 x 420 x 100: the largest side fits the 450 limit, the second 420 does not fit the 400 one.
        cases.Add(Sides(100, 420, 420), [Width]);
        // 310 x 310 x 310: the two largest fit, the smallest does not fit the 300 limit.
        cases.Add(Sides(310, 310, 310), [Height]);
        return cases;
    }

    [Fact]
    public void Ties_fit_at_the_limit_and_fail_one_millimetre_above()
    {
        Assert.Empty(Codes(Motorcycle, requireDimensions: true, Sides(400, 400, 350)));
        Assert.Empty(Codes(Motorcycle, requireDimensions: true, Sides(350, 400, 400)));
        Assert.Empty(Codes(Motorcycle, requireDimensions: true, Sides(350, 350, 350)));
        Assert.Equal([Length], Codes(Motorcycle, requireDimensions: true, Sides(400, 401, 350)));
        Assert.Equal([Height], Codes(Motorcycle, requireDimensions: true, Sides(400, 351, 400)));
        Assert.Equal([Height], Codes(Motorcycle, requireDimensions: true, Sides(351, 400, 351)));
        // With two equal limits on top, the middle side cannot fail without the largest one.
        Assert.Equal([Length, Width], Codes(Motorcycle, requireDimensions: true, Sides(401, 401, 350)));
        Assert.Equal([Length, Width, Height], Codes(Motorcycle, requireDimensions: true, Sides(401, 351, 401)));
    }

    [Fact]
    public void Without_required_dimensions_a_missing_side_is_not_checked_and_measured_ones_take_the_largest_limits()
    {
        Assert.Empty(Codes(Bicycle, requireDimensions: false, Sides(null, null, null)));
        // One measured side is compared with the largest limit, whatever field it was typed in.
        Assert.Empty(Codes(Bicycle, requireDimensions: false, Sides(null, null, 450)));
        Assert.Equal([Length], Codes(Bicycle, requireDimensions: false, Sides(null, null, 451)));
        Assert.Equal([Length], Codes(Bicycle, requireDimensions: false, Sides(451, null, null)));
        // Two measured sides are compared with the two largest limits.
        Assert.Empty(Codes(Bicycle, requireDimensions: false, Sides(300, null, 420)));
        Assert.Empty(Codes(Bicycle, requireDimensions: false, Sides(null, 400, 450)));
        Assert.Equal([Width], Codes(Bicycle, requireDimensions: false, Sides(null, 420, 410)));
        Assert.Equal([Length, Width], Codes(Bicycle, requireDimensions: false, Sides(460, 410, null)));
    }

    [Fact]
    public void With_required_dimensions_a_missing_side_fails_with_the_code_of_its_field()
    {
        Assert.Equal([Length, Width, Height], Codes(Bicycle, requireDimensions: true, Sides(null, null, null)));
        Assert.Equal([Length], Codes(Bicycle, requireDimensions: true, Sides(null, 450, 300)));
        Assert.Equal([Width], Codes(Bicycle, requireDimensions: true, Sides(300, null, 420)));
        Assert.Equal([Height], Codes(Bicycle, requireDimensions: true, Sides(300, 450, null)));
        // The measured sides are still compared as without the requirement.
        Assert.Equal([Length, Width], Codes(Bicycle, requireDimensions: true, Sides(null, 420, 410)));
        Assert.Equal([Length, Height], Codes(Bicycle, requireDimensions: true, Sides(451, 300, null)));
    }

    [Fact]
    public void Each_package_of_an_order_is_turned_on_its_own()
    {
        // The largest length, width and height across the two packages would be 450 x 450 x 200, which fits no
        // 45 x 40 limit; each package on its own fits.
        Assert.Empty(Codes(Bicycle, requireDimensions: true, Sides(450, 300, 200), Sides(300, 450, 200)));
        Assert.Empty(Codes(Bicycle, requireDimensions: false, Sides(450, 300, 200), Sides(null, 450, null)));
        Assert.Equal(
            [Length],
            Codes(Bicycle, requireDimensions: true, Sides(450, 300, 200), Sides(200, 200, 460)));
    }

    [Fact]
    public void An_order_reports_each_code_once_in_the_fixed_order_whichever_package_fails()
    {
        Assert.Equal(
            [Length, Height],
            Codes(Bicycle, requireDimensions: true, Sides(310, 310, 310), Sides(100, 460, 100), Sides(460, 100, 100)));
    }

    [Fact]
    public void With_required_dimensions_every_package_of_the_order_needs_its_three_sides()
    {
        Assert.Empty(Codes(Bicycle, requireDimensions: false, Sides(450, 300, 200), Sides(null, null, null)));
        Assert.Equal(
            [Length, Width, Height],
            Codes(Bicycle, requireDimensions: true, Sides(450, 300, 200), Sides(null, null, null)));
        Assert.Equal(
            [Width],
            Codes(Bicycle, requireDimensions: true, Sides(450, 300, 200), Sides(450, null, 300)));
    }

    [Fact]
    public void Count_and_weight_codes_come_before_the_side_codes_and_keep_their_meaning()
    {
        var requirement = new DriverCapacityRequirement(
            4,
            21_000,
            11_000,
            [Sides(100, 100, 100), Sides(500, 100, 100), Sides(100, 100, 100), Sides(100, 100, 100)]);

        Assert.Equal(
            [
                DriverEligibilityRejectionCodes.PackageCountExceeded,
                DriverEligibilityRejectionCodes.TotalWeightExceeded,
                DriverEligibilityRejectionCodes.SinglePackageWeightExceeded,
                Length,
            ],
            Codes(Bicycle, requireDimensions: true, requirement));
    }

    [Fact]
    public void The_rule_is_exactly_whether_some_turn_of_the_package_fits()
    {
        // An independent definition: a package fits when some assignment of the limits to its fields leaves each
        // measured side at most its limit; a missing side takes whatever limit is left.
        int?[] values = [null, 100, 299, 300, 301, 400, 401, 449, 450, 451];
        (int, int, int)[] limitsInAnyOrder = [(450, 400, 300), (300, 450, 400), (400, 400, 350), (350, 400, 400)];
        foreach (var limits in limitsInAnyOrder)
        {
            foreach (var length in values)
            {
                foreach (var width in values)
                {
                    foreach (var height in values)
                    {
                        var sides = Sides(length, width, height);
                        var fitsSomeTurn = Permutations(limits.Item1, limits.Item2, limits.Item3).Any(turn =>
                            (length is null || length <= turn.A) &&
                            (width is null || width <= turn.B) &&
                            (height is null || height <= turn.C));
                        Assert.True(
                            fitsSomeTurn == (Codes(limits, requireDimensions: false, sides).Length == 0),
                            $"{sides} against {limits}");
                    }
                }
            }
        }
    }

    private static PackageSides Sides(int? length, int? width, int? height) => new(length, width, height);

    private static IEnumerable<(int A, int B, int C)> Permutations(int first, int second, int third)
    {
        yield return (first, second, third);
        yield return (first, third, second);
        yield return (second, first, third);
        yield return (second, third, first);
        yield return (third, first, second);
        yield return (third, second, first);
    }

    private static string[] Codes(
        (int Length, int Width, int Height) limits,
        bool requireDimensions,
        params PackageSides[] packages) =>
        Codes(
            limits,
            requireDimensions,
            new DriverCapacityRequirement(packages.Length, packages.Length * 100L, 100, packages));

    private static string[] Codes(
        (int Length, int Width, int Height) limits,
        bool requireDimensions,
        DriverCapacityRequirement requirement)
    {
        var policy = new DriverEligibilityPolicyConfiguration(
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["BICYCLE"] = ["IDENTITY"],
            },
            new HashSet<string>(StringComparer.Ordinal) { "IDENTITY" },
            new Dictionary<string, VehicleCapacityLimits>(StringComparer.Ordinal)
            {
                ["BICYCLE"] = new(3, 20_000, 10_000, limits.Length, limits.Width, limits.Height, requireDimensions),
            });
        var snapshot = new DriverEligibilitySnapshot(
            DriverId,
            OrganizationId,
            Guid.NewGuid(),
            CityId,
            "OWN",
            "BICYCLE",
            "ACTIVE",
            "ACTIVE",
            true,
            null,
            new Dictionary<string, DriverDocumentSnapshot>(StringComparer.Ordinal)
            {
                ["IDENTITY"] = new("IDENTITY", "VALID", "synthetic/identity", new byte[32], null),
            },
            "h4-sorted-sides-unit-v1");
        var result = DriverEligibilityPolicy.Evaluate(
            new EvaluateOwnDriverEligibilityCommand(
                Guid.NewGuid(),
                OrganizationId,
                DriverId,
                CityId,
                null,
                requirement,
                Now),
            snapshot,
            policy);

        Assert.Equal(result.Rejections.Count == 0, result.IsEligible);
        return result.Rejections.Select(rejection => rejection.Code).ToArray();
    }
}
