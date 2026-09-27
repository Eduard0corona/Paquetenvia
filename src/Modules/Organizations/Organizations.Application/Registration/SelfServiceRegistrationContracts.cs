namespace Organizations.Application.Registration;

/// <summary>
/// REG-SELF-SERVICE-ORGANIZATION: a signed-in, linked user with a verified email creates its own
/// BUSINESS (ACTIVE, creator BUSINESS_ADMIN) or ALLY (PENDING_APPROVAL, creator ALLY_ADMIN).
/// </summary>
public sealed record CreateSelfServiceOrganizationCommand(
    Guid UserId,
    bool EmailVerified,
    string IdempotencyKey,
    string OrganizationType,
    string LegalName,
    string DisplayName,
    string? RequestId);

public enum SelfServiceOrganizationOutcome
{
    Created,

    /// <summary>The same Idempotency-Key and request: the original organization, unchanged.</summary>
    Replayed,

    /// <summary>The same Idempotency-Key with another request.</summary>
    IdempotencyConflict,

    /// <summary>REG-ONE-ORGANIZATION-PER-PERSON: the creator already has one organization that is not CLOSED.</summary>
    LimitReached,

    /// <summary>The caller may not use self-service onboarding (no verified email or inactive user).</summary>
    Forbidden,
}

public sealed record SelfServiceOrganization(
    Guid OrganizationId,
    string OrganizationType,
    string LegalName,
    string DisplayName,
    string Status,
    string Role);

public sealed record SelfServiceOrganizationResult(
    SelfServiceOrganizationOutcome Outcome,
    SelfServiceOrganization? Organization);

/// <summary>REG-OWN-APPLICATIONS-ENDPOINT: one organization the caller administers, whatever its status.</summary>
public sealed record OrganizationApplication(
    Guid OrganizationId,
    string OrganizationType,
    string DisplayName,
    string Status,
    DateTimeOffset CreatedAt);

/// <summary>REG-ALLY-APPROVAL-PATH: an ALLY waiting for a PLATFORM_ADMIN decision.</summary>
public sealed record PendingAllyOrganization(
    Guid OrganizationId,
    string LegalName,
    string DisplayName,
    DateTimeOffset CreatedAt);

public enum AllyDecisionOutcome
{
    /// <summary>The ALLY is ACTIVE (approved now or earlier).</summary>
    Approved,

    /// <summary>The ALLY is CLOSED (rejected now or earlier).</summary>
    Rejected,

    NotFound,

    /// <summary>The ALLY is in a state the decision cannot change (for example SUSPENDED, or approved and now rejected).</summary>
    Conflict,

    /// <summary>The actor is not an active PLATFORM_ADMIN of an active PLATFORM organization.</summary>
    Forbidden,
}

/// <summary>
/// The self-service authorizer: which callers may use onboarding at all. The default admits every
/// active linked user with a verified email; REG-ONE-ORGANIZATION-PER-PERSON is enforced by the database.
/// </summary>
public interface ISelfServiceOrganizationAuthorizer
{
    bool IsAuthorized(CreateSelfServiceOrganizationCommand command);
}

public sealed class VerifiedEmailSelfServiceOrganizationAuthorizer : ISelfServiceOrganizationAuthorizer
{
    public bool IsAuthorized(CreateSelfServiceOrganizationCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return command.EmailVerified && command.UserId != Guid.Empty;
    }
}

public interface ISelfServiceRegistrationService
{
    Task<SelfServiceOrganizationResult> CreateOrganizationAsync(
        CreateSelfServiceOrganizationCommand command,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<OrganizationApplication>> ListOwnApplicationsAsync(
        Guid userId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<PendingAllyOrganization>?> ListPendingAlliesAsync(
        Guid actorUserId,
        Guid platformOrganizationId,
        int limit,
        CancellationToken cancellationToken);

    Task<AllyDecisionOutcome> DecideAllyAsync(
        Guid actorUserId,
        Guid platformOrganizationId,
        Guid allyOrganizationId,
        bool approve,
        string? requestId,
        CancellationToken cancellationToken);
}

/// <summary>The registration store is unavailable or answered outside its contract.</summary>
public sealed class SelfServiceRegistrationUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public static class SelfServiceRegistrationLimits
{
    /// <summary>The only organization types self-service onboarding creates (AI-05 contract values).</summary>
    public static IReadOnlySet<string> OrganizationTypes { get; } =
        new HashSet<string>(["BUSINESS", "ALLY"], StringComparer.Ordinal);

    public const int LegalNameMaxLength = 200;
    public const int DisplayNameMaxLength = 80;
    public const int MaximumPendingPageSize = 200;
    public const int DefaultPendingPageSize = 50;
}
