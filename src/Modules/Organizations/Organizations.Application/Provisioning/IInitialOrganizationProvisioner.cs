using Organizations.Domain;
using Paqueteria.Domain.Tenancy;

namespace Organizations.Application.Provisioning;

public sealed record InitialOrganizationProvisioningCommand(
    string IdentitySubject,
    string LegalName,
    string DisplayName,
    OrganizationType OrganizationType,
    OrganizationRole InitialRole,
    string? RequestId);

public sealed record InitialOrganizationProvisioningResult(
    Guid UserId,
    Guid OrganizationId,
    Guid MembershipId,
    Guid AuditId);

public interface IInitialOrganizationProvisioner
{
    Task<InitialOrganizationProvisioningResult> ProvisionAsync(
        InitialOrganizationProvisioningCommand command,
        CancellationToken cancellationToken);
}

public interface IInitialOrganizationProvisioningAuthorizer
{
    ValueTask<bool> IsAuthorizedAsync(
        InitialOrganizationProvisioningCommand command,
        CancellationToken cancellationToken);
}

public enum ProvisioningStage
{
    UserInserted,
    OrganizationInserted,
    MembershipInserted,
    AuditInserted,
}

public interface IProvisioningFailureInjector
{
    ValueTask AfterAsync(ProvisioningStage stage, CancellationToken cancellationToken);
}

public sealed class InitialOrganizationProvisioningForbiddenException : Exception;

public sealed class InitialOrganizationProvisioningConflictException(string message, Exception? innerException = null)
    : Exception(message, innerException);
