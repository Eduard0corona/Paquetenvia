using Incidents.Domain;

namespace Incidents.Application.Incidents;

public sealed record OpenIncidentCommand(
    Guid ActorId,
    Guid OrganizationId,
    bool MfaSatisfied,
    string IdempotencyKey,
    Guid OrderId,
    string IncidentType,
    string Severity,
    string Description,
    string ReasonCode,
    string NextAction,
    DateTimeOffset OccurredAt,
    IReadOnlyList<Guid> EvidenceProofIds,
    string? RequestId);

/// <summary>
/// Closes a pending incident into one terminal outcome. The reason is the operator's rationale
/// and is kept only as append-only audit evidence.
/// </summary>
public sealed record ResolveIncidentCommand(
    Guid ActorId,
    Guid OrganizationId,
    bool MfaSatisfied,
    string IdempotencyKey,
    Guid IncidentId,
    string Outcome,
    string Reason,
    string? RequestId);

public sealed record IncidentResult(
    Guid Id,
    Guid OrderId,
    string Status,
    string Severity,
    string IncidentType,
    string ReasonCode,
    string NextAction,
    bool CustodyAcquired,
    DateTimeOffset OccurredAt,
    DateTimeOffset SlaDueAt,
    IReadOnlyList<Guid> EvidenceProofIds);

public interface IIncidentService
{
    Task<IncidentResult> OpenAsync(
        OpenIncidentCommand command,
        CancellationToken cancellationToken);

    Task<IncidentResult> ResolveAsync(
        ResolveIncidentCommand command,
        CancellationToken cancellationToken);
}

/// <summary>
/// Shape validation for the INC-001 opening and resolution contracts. Every acceptance criterion
/// that the backlog calls mandatory is rejected here before any tenant transaction is opened.
/// </summary>
public static class IncidentRequestPolicy
{
    public const int MaximumDescriptionLength = 2_000;
    public const int MaximumIncidentTypeLength = 64;

    public static bool IsValidIncidentType(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= MaximumIncidentTypeLength &&
        value.All(character => char.IsAsciiLetterUpper(character) || character == '_');

    public static bool IsValidDescription(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= MaximumDescriptionLength;

    public static bool IsValidEvidence(IReadOnlyList<Guid>? evidenceProofIds) =>
        evidenceProofIds is not null &&
        IncidentEvidencePolicy.IsAllowedCount(evidenceProofIds.Count) &&
        evidenceProofIds.All(id => id != Guid.Empty) &&
        evidenceProofIds.Distinct().Count() == evidenceProofIds.Count;


    public static bool IsValidCommandShape(OpenIncidentCommand command) =>
        command.ActorId != Guid.Empty &&
        command.OrganizationId != Guid.Empty &&
        command.OrderId != Guid.Empty &&
        IsValidIncidentType(command.IncidentType) &&
        IncidentContract.TryParseSeverity(command.Severity, out _) &&
        IsValidDescription(command.Description) &&
        IncidentContract.TryParseReasonCode(command.ReasonCode, out _) &&
        IncidentContract.TryParseNextAction(command.NextAction, out _) &&
        IsValidEvidence(command.EvidenceProofIds);

    public const int MaximumResolutionReasonLength = 500;

    /// <summary>
    /// The resolution reason is bounded plain text. Surrounding whitespace is rejected rather than
    /// trimmed, so the audit records exactly what the operator sent, and control characters are
    /// rejected because the rationale is a single human-readable statement, not formatted content.
    /// </summary>
    public static bool IsValidResolutionReason(string? value) =>
        value is { Length: > 0 and <= MaximumResolutionReasonLength } &&
        !char.IsWhiteSpace(value[0]) &&
        !char.IsWhiteSpace(value[^1]) &&
        !value.Any(char.IsControl);

    public static bool IsValidResolveCommandShape(ResolveIncidentCommand command) =>
        command.ActorId != Guid.Empty &&
        command.OrganizationId != Guid.Empty &&
        command.IncidentId != Guid.Empty &&
        IncidentContract.TryParseResolutionOutcome(command.Outcome, out _) &&
        IsValidResolutionReason(command.Reason);
}

public abstract class IncidentException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

public sealed class IncidentForbiddenException() : IncidentException("FORBIDDEN");

public sealed class IncidentNotFoundException() : IncidentException("NOT_FOUND");

public sealed class IncidentConflictException(string code) : IncidentException(code);

public sealed class IncidentInfrastructureException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>
/// Protects the mandatory incident description before it reaches the store. INC-001 owns this
/// abstraction rather than borrowing another module's concrete protector, so the Incidents module
/// keeps no dependency on Locations; the shape follows the established GEO-001 precedent.
/// </summary>
public interface IIncidentPiiProtector
{
    /// <summary>
    /// Returns the ciphertext AI-06 persists in <c>description_ciphertext</c> under
    /// <paramref name="keyVersion"/>. An implementation that cannot protect the value throws
    /// <see cref="IncidentPiiProtectionUnavailableException"/> instead of returning plaintext.
    /// </summary>
    byte[] Protect(string plaintext, string keyVersion);
}

public sealed class IncidentPiiProtectionUnavailableException()
    : Exception("Incident PII protection is unavailable.");
