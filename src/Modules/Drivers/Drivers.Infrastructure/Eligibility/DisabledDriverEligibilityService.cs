using Drivers.Application.Eligibility;
using Microsoft.Extensions.Options;

namespace Drivers.Infrastructure.Eligibility;

public sealed class DisabledDriverEligibilityService(IOptions<DriversOptions> options)
    : IDriverEligibilityService
{
    public Task<DriverEligibilityResult> EvaluateAsync(
        EvaluateOwnDriverEligibilityCommand command,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new DriverEligibilityResult(
            false,
            null,
            null,
            options.Value.Eligibility.PolicyVersion,
            [new DriverEligibilityRejection(DriverEligibilityRejectionCodes.DriverUnavailable)]));
    }
}
