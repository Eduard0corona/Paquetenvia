using NetTopologySuite.Geometries;

namespace Drivers.Infrastructure.Persistence;

internal sealed class DriverPositionRecord
{
    public Guid Id { get; set; }
    public Guid DriverId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid CityId { get; set; }
    public Guid ClientEventId { get; set; }
    public Point Point { get; set; } = null!;
    public decimal AccuracyMeters { get; set; }
    public decimal? HeadingDegrees { get; set; }
    public decimal? SpeedMetersPerSecond { get; set; }
    public DateTimeOffset CapturedAt { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public bool PublishRealtime { get; set; }
}
