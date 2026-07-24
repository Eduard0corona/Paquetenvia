using Dispatch.Application.Assignments;
using Dispatch.Application.Stops;

namespace Dispatch.Infrastructure.Assignments;

public sealed class DisabledAssignmentService : IAssignmentService
{
    public Task<AssignmentResult> CreateOwnDriverAssignmentAsync(
        CreateOwnDriverAssignmentCommand command,
        CancellationToken cancellationToken) =>
        throw new AssignmentForbiddenException();
}

public sealed class DisabledDriverStopsQuery : IDriverStopsQuery
{
    public Task<IReadOnlyList<DriverStopResult>> ListCurrentDriverStopsAsync(
        Guid actorId,
        Guid organizationId,
        CancellationToken cancellationToken) =>
        throw new DriverStopsForbiddenException();
}
