namespace Drivers.Application.Locations;

public sealed record DriverLocationPointInput(
    Guid? ClientEventId,
    double? Latitude,
    double? Longitude,
    double? AccuracyMeters,
    DateTimeOffset? CapturedAt,
    double? HeadingDegrees,
    double? SpeedMetersPerSecond);

public sealed record PublishDriverLocationBatchCommand(
    Guid ActorId,
    Guid OrganizationId,
    IReadOnlyList<DriverLocationPointInput> Positions);

public enum DriverLocationItemStatus
{
    Accepted,
    Duplicate,
    Rejected,
}

public sealed record DriverLocationItemResult(
    Guid? ClientEventId,
    Guid? PositionId,
    DriverLocationItemStatus Status,
    string? ErrorCode)
{
    public bool Duplicate => Status == DriverLocationItemStatus.Duplicate;
}

public sealed record DriverLocationBatchResult(
    IReadOnlyList<DriverLocationItemResult> Items,
    int PublishedCount = 0)
{
    public IReadOnlyList<DriverLocationItemResult> Items { get; } = Items.ToArray();
    public int AcceptedCount => Items.Count(item => item.Status == DriverLocationItemStatus.Accepted);
    public int DuplicateCount => Items.Count(item => item.Status == DriverLocationItemStatus.Duplicate);
    public int RejectedCount => Items.Count(item => item.Status == DriverLocationItemStatus.Rejected);
    public int SuppressedCount => AcceptedCount - PublishedCount;
}

public interface IDriverLocationIngestionService
{
    Task<DriverLocationBatchResult> PublishAsync(
        PublishDriverLocationBatchCommand command,
        CancellationToken cancellationToken);
}

public sealed record DriverLocationAuthorizationSnapshot(
    bool UserActive,
    bool MembershipActive,
    string? MembershipRole);

public interface IDriverLocationAuthorizer
{
    bool IsAuthorized(DriverLocationAuthorizationSnapshot snapshot);
}

public sealed class DriverLocationAuthorizer : IDriverLocationAuthorizer
{
    public bool IsAuthorized(DriverLocationAuthorizationSnapshot snapshot) =>
        snapshot.UserActive &&
        snapshot.MembershipActive &&
        string.Equals(snapshot.MembershipRole, "DRIVER", StringComparison.Ordinal);
}

public enum DriverLocationTransactionStage
{
    AuthorizationCompleted,
    DriverLocked,
    DuplicatesRead,
    PositionInserted,
    LocationOutboxInserted,
    BeforeCommit,
}

public interface IDriverLocationFailureInjector
{
    Task OnStageAsync(DriverLocationTransactionStage stage, CancellationToken cancellationToken);
}

public sealed class NoOpDriverLocationFailureInjector : IDriverLocationFailureInjector
{
    public Task OnStageAsync(DriverLocationTransactionStage stage, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

public sealed class DriverLocationForbiddenException : Exception
{
    public DriverLocationForbiddenException() : base("The actor lacks the current driver capability.")
    {
    }
}

public sealed class DriverLocationNotFoundException : Exception
{
    public DriverLocationNotFoundException() : base("The driver profile is missing or inaccessible.")
    {
    }
}

public sealed class DriverLocationProviderUnavailableException : Exception
{
    public DriverLocationProviderUnavailableException() : base("Driver location ingestion is unavailable.")
    {
    }
}

public sealed class DriverLocationInfrastructureException(Exception innerException)
    : Exception("Driver location persistence failed.", innerException);
