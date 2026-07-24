using System.Collections.Immutable;
using Paqueteria.Domain.Tenancy;

namespace Realtime.Application.Authorization;

public sealed record PrivateRealtimeConnectionRequest(
    Guid UserId,
    Guid OrganizationId,
    bool MfaSatisfied,
    string? RequestId);

public sealed record OperationsConnectionAuthorization(
    Guid OrganizationId,
    OrganizationRole Role);

public static class RealtimeOperationsRolePolicy
{
    public static bool IsAllowed(OrganizationRole role, bool mfaSatisfied) => role switch
    {
        OrganizationRole.PlatformAdmin => mfaSatisfied,
        OrganizationRole.Dispatcher => true,
        _ => false,
    };
}

public sealed record DriverConnectionAuthorization
{
    public DriverConnectionAuthorization(
        Guid organizationId,
        Guid driverId,
        IEnumerable<Guid> assignmentIds)
    {
        if (organizationId == Guid.Empty || driverId == Guid.Empty)
        {
            throw new ArgumentException("Non-empty authorization identifiers are required.");
        }

        OrganizationId = organizationId;
        DriverId = driverId;
        AssignmentIds = assignmentIds
            .Where(static id => id != Guid.Empty)
            .Distinct()
            .Order()
            .ToImmutableArray();
    }

    public Guid OrganizationId { get; }
    public Guid DriverId { get; }
    public ImmutableArray<Guid> AssignmentIds { get; }
}

public sealed class TrackingConnectionAuthorization
{
    public TrackingConnectionAuthorization(string publicOrderId)
    {
        if (!Events.RealtimePublicOrderId.IsValid(publicOrderId))
        {
            throw new ArgumentException("A valid public order id is required.", nameof(publicOrderId));
        }

        PublicOrderId = publicOrderId;
    }

    public string PublicOrderId { get; }
}

public enum ConnectionAuthorizationStatus
{
    Rejected,
    Authorized,
}

public sealed class ConnectionAuthorizationResult<T>
    where T : class
{
    private ConnectionAuthorizationResult(ConnectionAuthorizationStatus status, T? authorization)
    {
        Status = status;
        Authorization = authorization;
    }

    public ConnectionAuthorizationStatus Status { get; }
    public T? Authorization { get; }
    public bool IsAuthorized => Status == ConnectionAuthorizationStatus.Authorized;

    public static ConnectionAuthorizationResult<T> Rejected { get; } =
        new(ConnectionAuthorizationStatus.Rejected, null);

    public static ConnectionAuthorizationResult<T> Authorized(T authorization)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        return new(ConnectionAuthorizationStatus.Authorized, authorization);
    }
}

public interface IRealtimeConnectionAuthorizer
{
    ValueTask<ConnectionAuthorizationResult<OperationsConnectionAuthorization>> AuthorizeOperationsAsync(
        PrivateRealtimeConnectionRequest request,
        CancellationToken cancellationToken);

    ValueTask<ConnectionAuthorizationResult<DriverConnectionAuthorization>> AuthorizeDriverAsync(
        PrivateRealtimeConnectionRequest request,
        CancellationToken cancellationToken);

    ValueTask<ConnectionAuthorizationResult<TrackingConnectionAuthorization>> AuthorizeTrackingAsync(
        string exactToken,
        CancellationToken cancellationToken);
}

public sealed class RealtimeAuthorizationInfrastructureException : Exception
{
    public RealtimeAuthorizationInfrastructureException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
