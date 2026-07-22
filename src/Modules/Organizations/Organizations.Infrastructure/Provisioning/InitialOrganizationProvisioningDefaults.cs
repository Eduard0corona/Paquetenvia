using Organizations.Application.Provisioning;

namespace Organizations.Infrastructure.Provisioning;

public sealed class DenyInitialOrganizationProvisioningAuthorizer : IInitialOrganizationProvisioningAuthorizer
{
    public ValueTask<bool> IsAuthorizedAsync(
        InitialOrganizationProvisioningCommand command,
        CancellationToken cancellationToken) => ValueTask.FromResult(false);
}

public sealed class NoOpProvisioningFailureInjector : IProvisioningFailureInjector
{
    public ValueTask AfterAsync(ProvisioningStage stage, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
