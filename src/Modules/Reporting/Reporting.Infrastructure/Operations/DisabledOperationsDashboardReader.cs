using Reporting.Application.Operations;

namespace Reporting.Infrastructure.Operations;

internal sealed class DisabledOperationsDashboardReader : IOperationsDashboardReader
{
    public Task<OperationsDashboardPage> ReadAsync(
        OperationsDashboardRequest request,
        CancellationToken cancellationToken) =>
        Task.FromException<OperationsDashboardPage>(
            new OperationsDashboardUnavailableException("The operations dashboard is disabled."));
}
