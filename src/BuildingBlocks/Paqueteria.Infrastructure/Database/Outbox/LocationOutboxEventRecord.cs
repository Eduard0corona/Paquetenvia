namespace Paqueteria.Infrastructure.Database.Outbox;

internal sealed class LocationOutboxEventRecord
{
    internal Guid Id { get; init; }

    internal Guid OwnerOrganizationId { get; init; }

    internal Guid DriverPositionId { get; init; }

    internal required string Topic { get; init; }

    internal required string Payload { get; init; }

    internal required string Status { get; init; }

    internal int Attempts { get; init; }

    internal DateTimeOffset AvailableAt { get; init; }

    internal DateTimeOffset? LockedAt { get; init; }

    internal string? LockedBy { get; init; }

    internal Guid? LeaseToken { get; init; }

    internal DateTimeOffset? LeaseExpiresAt { get; init; }

    internal string? LastError { get; init; }

    internal DateTimeOffset CreatedAt { get; init; }

    internal DateTimeOffset? ProcessedAt { get; init; }
}
