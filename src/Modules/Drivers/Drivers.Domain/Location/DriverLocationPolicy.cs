namespace Drivers.Domain.Location;

public static class DriverLocationRejectionCodes
{
    public const string InvalidClientEventId = "INVALID_CLIENT_EVENT_ID";
    public const string InvalidCoordinates = "INVALID_COORDINATES";
    public const string InvalidAccuracy = "INVALID_ACCURACY";
    public const string InvalidHeading = "INVALID_HEADING";
    public const string InvalidSpeed = "INVALID_SPEED";
    public const string InvalidCapturedAt = "INVALID_CAPTURED_AT";
}

public sealed record DriverLocationInput(
    Guid? ClientEventId,
    double? Latitude,
    double? Longitude,
    double? AccuracyMeters,
    DateTimeOffset? CapturedAt,
    double? HeadingDegrees,
    double? SpeedMetersPerSecond);

public sealed record ValidatedDriverLocation(
    Guid ClientEventId,
    double Latitude,
    double Longitude,
    decimal AccuracyMeters,
    DateTimeOffset CapturedAt,
    decimal? HeadingDegrees,
    decimal? SpeedMetersPerSecond);

public sealed record DriverLocationValidation(
    ValidatedDriverLocation? Location,
    string? RejectionCode)
{
    public bool IsValid => Location is not null;
}

public static class DriverLocationValidationPolicy
{
    private const double NumericEightTwoMaximum = 999_999.99d;

    public static DriverLocationValidation Validate(DriverLocationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (input.ClientEventId is not { } clientEventId || clientEventId == Guid.Empty)
        {
            return Rejected(DriverLocationRejectionCodes.InvalidClientEventId);
        }

        if (!IsFiniteInRange(input.Latitude, -90d, 90d) ||
            !IsFiniteInRange(input.Longitude, -180d, 180d))
        {
            return Rejected(DriverLocationRejectionCodes.InvalidCoordinates);
        }

        if (!IsFiniteInRange(input.AccuracyMeters, 0d, NumericEightTwoMaximum))
        {
            return Rejected(DriverLocationRejectionCodes.InvalidAccuracy);
        }

        if (input.HeadingDegrees is { } heading &&
            (!double.IsFinite(heading) || heading is < 0d or > 360d))
        {
            return Rejected(DriverLocationRejectionCodes.InvalidHeading);
        }

        if (input.SpeedMetersPerSecond is { } speed &&
            (!double.IsFinite(speed) || speed is < 0d or > NumericEightTwoMaximum))
        {
            return Rejected(DriverLocationRejectionCodes.InvalidSpeed);
        }

        if (input.CapturedAt is not { } capturedAt || capturedAt == default)
        {
            return Rejected(DriverLocationRejectionCodes.InvalidCapturedAt);
        }

        return new DriverLocationValidation(
            new ValidatedDriverLocation(
                clientEventId,
                input.Latitude!.Value,
                input.Longitude!.Value,
                ToNumeric(input.AccuracyMeters!.Value),
                capturedAt.ToUniversalTime(),
                input.HeadingDegrees is null ? null : ToNumeric(input.HeadingDegrees.Value),
                input.SpeedMetersPerSecond is null ? null : ToNumeric(input.SpeedMetersPerSecond.Value)),
            null);
    }

    private static bool IsFiniteInRange(double? value, double minimum, double maximum) =>
        value is { } number && double.IsFinite(number) && number >= minimum && number <= maximum;

    private static decimal ToNumeric(double value) =>
        decimal.Round((decimal)value, 2, MidpointRounding.AwayFromZero);

    private static DriverLocationValidation Rejected(string code) => new(null, code);
}

public sealed record DriverLocationPublicationOptions(
    TimeSpan MinimumPublishInterval,
    double MinimumPublishDistanceMeters,
    TimeSpan MaximumSilence);

public sealed record PublishedLocationBaseline(
    Guid PositionId,
    double Latitude,
    double Longitude,
    DateTimeOffset CapturedAt);

public static class DriverLocationPublicationPolicy
{
    public const double MeanEarthRadiusMeters = 6_371_008.8d;

    public static bool ShouldPublish(
        PublishedLocationBaseline? baseline,
        ValidatedDriverLocation candidate,
        DriverLocationPublicationOptions options)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(options);

        if (baseline is null)
        {
            return true;
        }

        var elapsed = candidate.CapturedAt - baseline.CapturedAt;
        if (elapsed <= TimeSpan.Zero)
        {
            return false;
        }

        if (elapsed >= options.MaximumSilence)
        {
            return true;
        }

        return elapsed >= options.MinimumPublishInterval &&
            HaversineMeters(
                baseline.Latitude,
                baseline.Longitude,
                candidate.Latitude,
                candidate.Longitude) >= options.MinimumPublishDistanceMeters;
    }

    public static double HaversineMeters(
        double latitude1,
        double longitude1,
        double latitude2,
        double longitude2)
    {
        var latitudeDelta = DegreesToRadians(latitude2 - latitude1);
        var longitudeDelta = DegreesToRadians(longitude2 - longitude1);
        var firstLatitude = DegreesToRadians(latitude1);
        var secondLatitude = DegreesToRadians(latitude2);
        var haversine =
            Math.Pow(Math.Sin(latitudeDelta / 2d), 2d) +
            Math.Cos(firstLatitude) * Math.Cos(secondLatitude) *
            Math.Pow(Math.Sin(longitudeDelta / 2d), 2d);
        return 2d * MeanEarthRadiusMeters *
            Math.Asin(Math.Min(1d, Math.Sqrt(haversine)));
    }

    private static double DegreesToRadians(double value) => value * Math.PI / 180d;
}
