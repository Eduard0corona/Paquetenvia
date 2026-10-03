using Drivers.Application.Eligibility;

namespace Drivers.Infrastructure.Eligibility;

/// <summary>
/// Without a provider no driver is visible, so no organization's eligibility policy version is read
/// (POLICY-VERSIONS-PER-ORG-2026-10-02) and the result carries none.
/// </summary>
public sealed class DisabledDriverEligibilityService : IDriverEligibilityService
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
            null,
            [new DriverEligibilityRejection(DriverEligibilityRejectionCodes.DriverUnavailable)]));
    }
}
