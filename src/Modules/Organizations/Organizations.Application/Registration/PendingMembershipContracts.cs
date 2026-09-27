namespace Organizations.Application.Registration;

/// <summary>
/// REG-JOIN-BY-ADMIN-EMAIL: an administrator of the selected organization adds a person by email and
/// role. The email is hashed before it leaves the service and is never stored or returned.
/// </summary>
public sealed record AddPendingMembershipCommand(
    Guid ActorUserId,
    Guid OrganizationId,
    string IdempotencyKey,
    string Email,
    string Role,
    string? RequestId);

/// <summary>Renew or revoke one pending membership of the selected organization.</summary>
public sealed record PendingMembershipActionCommand(
    Guid ActorUserId,
    Guid OrganizationId,
    Guid PendingMembershipId,
    string IdempotencyKey,
    string? RequestId);

/// <summary>What the organization may see of an entry: never the email or its hash.</summary>
public sealed record PendingMembership(
    Guid Id,
    string Role,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

public enum PendingMembershipOutcome
{
    /// <summary>
    /// Added, re-armed (the same email and role was already PENDING), revoked, or the original result of a
    /// replayed Idempotency-Key.
    /// </summary>
    Succeeded,

    /// <summary>The same Idempotency-Key with another request.</summary>
    IdempotencyConflict,

    NotFound,

    /// <summary>The entry is ACCEPTED (renew or revoke) or REVOKED (renew).</summary>
    NotPending,

    /// <summary>Not an active administrator of the active organization, or the role is above its ceiling.</summary>
    Forbidden,
}

public sealed record PendingMembershipResult(
    PendingMembershipOutcome Outcome,
    PendingMembership? Entry);

public interface IPendingMembershipService
{
    Task<PendingMembershipResult> AddAsync(AddPendingMembershipCommand command, CancellationToken cancellationToken);

    Task<IReadOnlyList<PendingMembership>> ListAsync(
        Guid actorUserId,
        Guid organizationId,
        int limit,
        CancellationToken cancellationToken);

    Task<PendingMembershipResult> RenewAsync(PendingMembershipActionCommand command, CancellationToken cancellationToken);

    Task<PendingMembershipResult> RevokeAsync(PendingMembershipActionCommand command, CancellationToken cancellationToken);
}

public static class PendingMembershipLimits
{
    /// <summary>REG-PENDING-EXPIRY-7D.</summary>
    public const int LifetimeDays = 7;

    public const int DefaultPageSize = 50;
    public const int MaximumPageSize = 200;

    /// <summary>The AI-05 role values an entry may carry; the ceiling is enforced by the database.</summary>
    public static IReadOnlySet<string> Roles { get; } = new HashSet<string>(
        [
            "PLATFORM_ADMIN", "DISPATCHER", "FINANCE", "ALLY_ADMIN", "ALLY_OPERATOR",
            "BUSINESS_ADMIN", "BUSINESS_OPERATOR", "DRIVER", "VIEWER",
        ],
        StringComparer.Ordinal);
}
