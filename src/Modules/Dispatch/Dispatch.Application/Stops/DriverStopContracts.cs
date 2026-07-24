namespace Dispatch.Application.Stops;

public sealed record DriverStopResult(
    string OrderPublicId,
    string StopType,
    string Status,
    string AddressSummary);

public interface IDriverStopsQuery
{
    Task<IReadOnlyList<DriverStopResult>> ListCurrentDriverStopsAsync(
        Guid actorId,
        Guid organizationId,
        CancellationToken cancellationToken);
}

public sealed class DriverStopsForbiddenException : Exception
{
    public DriverStopsForbiddenException()
        : base("The actor has no visible active own-driver profile.")
    {
    }
}

public sealed class DriverStopsInfrastructureException(string message, Exception? innerException = null)
    : Exception(message, innerException);
