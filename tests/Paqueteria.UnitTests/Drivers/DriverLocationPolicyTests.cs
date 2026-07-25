using Drivers.Application.Locations;
using Drivers.Domain.Location;
using Drivers.Infrastructure;

namespace Paqueteria.UnitTests.Drivers;

public sealed class DriverLocationPolicyTests
{
    private static readonly DateTimeOffset CapturedAt =
        new(2026, 7, 24, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Validation_accepts_boundaries_zeroes_and_normalizes_UTC()
    {
        var value = DriverLocationValidationPolicy.Validate(new(
            Guid.NewGuid(),
            -90,
            180,
            0,
            new DateTimeOffset(2026, 7, 24, 14, 0, 0, TimeSpan.FromHours(-6)),
            360,
            0));

        Assert.True(value.IsValid);
        Assert.Equal(CapturedAt, value.Location!.CapturedAt);
        Assert.Equal(0m, value.Location.AccuracyMeters);
        Assert.Equal(360m, value.Location.HeadingDegrees);
        Assert.Equal(0m, value.Location.SpeedMetersPerSecond);
    }

    [Theory]
    [MemberData(nameof(InvalidValues))]
    public void Validation_returns_stable_allowlisted_code(
        DriverLocationInput input,
        string expected)
    {
        var result = DriverLocationValidationPolicy.Validate(input);
        Assert.False(result.IsValid);
        Assert.Equal(expected, result.RejectionCode);
    }

    public static TheoryData<DriverLocationInput, string> InvalidValues => new()
    {
        { Valid() with { ClientEventId = Guid.Empty }, DriverLocationRejectionCodes.InvalidClientEventId },
        { Valid() with { Latitude = double.NaN }, DriverLocationRejectionCodes.InvalidCoordinates },
        { Valid() with { Latitude = 90.00001 }, DriverLocationRejectionCodes.InvalidCoordinates },
        { Valid() with { Longitude = -180.00001 }, DriverLocationRejectionCodes.InvalidCoordinates },
        { Valid() with { AccuracyMeters = -0.01 }, DriverLocationRejectionCodes.InvalidAccuracy },
        { Valid() with { AccuracyMeters = 1_000_000 }, DriverLocationRejectionCodes.InvalidAccuracy },
        { Valid() with { HeadingDegrees = -0.01 }, DriverLocationRejectionCodes.InvalidHeading },
        { Valid() with { HeadingDegrees = 360.01 }, DriverLocationRejectionCodes.InvalidHeading },
        { Valid() with { SpeedMetersPerSecond = double.PositiveInfinity }, DriverLocationRejectionCodes.InvalidSpeed },
        { Valid() with { SpeedMetersPerSecond = -0.01 }, DriverLocationRejectionCodes.InvalidSpeed },
        { Valid() with { CapturedAt = default }, DriverLocationRejectionCodes.InvalidCapturedAt },
    };

    [Fact]
    public void Publication_policy_handles_first_interval_distance_silence_and_old_points()
    {
        var options = new DriverLocationPublicationOptions(
            TimeSpan.FromSeconds(10),
            25,
            TimeSpan.FromSeconds(60));
        var candidate = DriverLocationValidationPolicy.Validate(Valid()).Location!;
        Assert.True(DriverLocationPublicationPolicy.ShouldPublish(null, candidate, options));

        var baseline = new PublishedLocationBaseline(
            Guid.NewGuid(),
            candidate.Latitude,
            candidate.Longitude,
            candidate.CapturedAt);
        Assert.False(DriverLocationPublicationPolicy.ShouldPublish(
            baseline,
            candidate with { CapturedAt = candidate.CapturedAt.AddSeconds(9), Latitude = 25 },
            options));
        Assert.False(DriverLocationPublicationPolicy.ShouldPublish(
            baseline,
            candidate with { CapturedAt = candidate.CapturedAt.AddSeconds(10), Latitude = candidate.Latitude + 0.00001 },
            options));
        Assert.True(DriverLocationPublicationPolicy.ShouldPublish(
            baseline,
            candidate with { CapturedAt = candidate.CapturedAt.AddSeconds(10), Latitude = candidate.Latitude + 0.001 },
            options));
        Assert.True(DriverLocationPublicationPolicy.ShouldPublish(
            baseline,
            candidate with { CapturedAt = candidate.CapturedAt.AddSeconds(60) },
            options));
        Assert.False(DriverLocationPublicationPolicy.ShouldPublish(
            baseline,
            candidate with { CapturedAt = candidate.CapturedAt.AddSeconds(-1), Latitude = 25 },
            options));
    }

    [Fact]
    public void Haversine_uses_meters_and_known_equatorial_vector()
    {
        var meters = DriverLocationPublicationPolicy.HaversineMeters(0, 0, 0, 1);
        Assert.InRange(meters, 111_194d, 111_196d);
        Assert.Equal(0d, DriverLocationPublicationPolicy.HaversineMeters(24.8, -107.4, 24.8, -107.4));
    }

    [Fact]
    public void Batch_result_counts_each_classification_exactly()
    {
        var id = Guid.NewGuid();
        var result = new DriverLocationBatchResult(
        [
            new(id, Guid.NewGuid(), DriverLocationItemStatus.Accepted, null),
            new(id, Guid.NewGuid(), DriverLocationItemStatus.Duplicate, null),
            new(Guid.Empty, null, DriverLocationItemStatus.Rejected, DriverLocationRejectionCodes.InvalidClientEventId),
        ], 1);
        Assert.Equal(1, result.AcceptedCount);
        Assert.Equal(1, result.DuplicateCount);
        Assert.Equal(1, result.RejectedCount);
        Assert.Equal(3, result.AcceptedCount + result.DuplicateCount + result.RejectedCount);
        Assert.True(result.Items[1].Duplicate);
    }

    [Theory]
    [InlineData(0, 25, 60)]
    [InlineData(301, 25, 301)]
    [InlineData(10, 0, 60)]
    [InlineData(10, 1001, 60)]
    [InlineData(10, 25, 9)]
    [InlineData(10, 25, 3601)]
    public void Telemetry_options_fail_closed_outside_approved_ranges(
        int interval,
        double distance,
        int silence)
    {
        Assert.False(new DriverLocationTelemetryOptions
        {
            MinimumPublishIntervalSeconds = interval,
            MinimumPublishDistanceMeters = distance,
            MaximumSilenceSeconds = silence,
        }.IsValid());
    }

    [Fact]
    public void Telemetry_default_options_are_valid_and_synthetic()
    {
        var options = new DriverLocationTelemetryOptions();
        Assert.True(options.IsValid());
        Assert.Equal(10, options.MinimumPublishIntervalSeconds);
        Assert.Equal(25d, options.MinimumPublishDistanceMeters);
        Assert.Equal(60, options.MaximumSilenceSeconds);
    }

    private static DriverLocationInput Valid() => new(
        Guid.NewGuid(),
        24.8091,
        -107.3940,
        8.5,
        CapturedAt,
        180,
        7.2);
}
