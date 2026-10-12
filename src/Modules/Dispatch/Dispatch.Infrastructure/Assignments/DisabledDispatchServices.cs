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
    public Task<DriverStopListResult> ListCurrentDriverStopsAsync(
        Guid actorId,
        Guid organizationId,
        bool includeDetails,
        CancellationToken cancellationToken) =>
        throw new DriverStopsForbiddenException();
}
