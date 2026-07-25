using Drivers.Application.Locations;

namespace Drivers.Infrastructure.Locations;

public sealed class DisabledDriverLocationIngestionService : IDriverLocationIngestionService
{
    public Task<DriverLocationBatchResult> PublishAsync(
        PublishDriverLocationBatchCommand command,
        CancellationToken cancellationToken) =>
        throw new DriverLocationProviderUnavailableException();
}
