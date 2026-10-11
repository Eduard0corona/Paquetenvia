using Incidents.Domain;

namespace Incidents.Application.Incidents;

/// <summary>
/// An INC-001 opening as the client sent it. <see cref="Severity"/>, <see cref="Description"/> and
/// <see cref="NextAction"/> are <see langword="null"/> when absent: a dispatch report carries all
/// three, a driver report none (INC-FAILED-ATTEMPT-DISPATCH-DECIDES-2026-10-11).
/// </summary>
public sealed record OpenIncidentCommand(
    Guid ActorId,
    Guid OrganizationId,
    bool MfaSatisfied,
    string IdempotencyKey,
    Guid OrderId,
    string IncidentType,
    string? Severity,
    string? Description,
    string ReasonCode,
    string? NextAction,
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

/// <param name="NextAction">
/// <see langword="null"/> when dispatch decides the successor of the failed attempt (a driver report,
/// INC-FAILED-ATTEMPT-DISPATCH-DECIDES-2026-10-11).
/// </param>
public sealed record IncidentResult(
    Guid Id,
    Guid OrderId,
    string Status,
    string Severity,
    string IncidentType,
    string ReasonCode,
    string? NextAction,
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


    /// <summary>
    /// The shape every opening must have before any transaction: identifiers, type, reason and
    /// evidence, and then either the complete dispatch report or the bare driver report. A request
    /// that mixes them (for example a next action without a description) is never a valid opening.
    /// Which of the two the actor may send is settled by <see cref="IsValidReportShape"/> once the
    /// actor's authorization on the order is known.
    /// </summary>
    public static bool IsValidCommandShape(OpenIncidentCommand command) =>
        command.ActorId != Guid.Empty &&
        command.OrganizationId != Guid.Empty &&
        command.OrderId != Guid.Empty &&
        IsValidIncidentType(command.IncidentType) &&
        IncidentContract.TryParseReasonCode(command.ReasonCode, out _) &&
        IsValidEvidence(command.EvidenceProofIds) &&
        (IsDispatchReportShape(command) || IsDriverReportShape(command));

    /// <summary>A dispatch report, as before: severity, description and next action, all valid.</summary>
    public static bool IsDispatchReportShape(OpenIncidentCommand command) =>
        IncidentContract.TryParseSeverity(command.Severity, out _) &&
        IsValidDescription(command.Description) &&
        IncidentContract.TryParseNextAction(command.NextAction, out _);

    /// <summary>
    /// INC-FAILED-ATTEMPT-DISPATCH-DECIDES-2026-10-11: a driver report sends no severity, no
    /// description and no next action; the server derives the first two and dispatch decides the
    /// third. A client value, even one equal to what the server would derive, is refused.
    /// </summary>
    public static bool IsDriverReportShape(OpenIncidentCommand command) =>
        command.Severity is null &&
        command.Description is null &&
        command.NextAction is null;

    /// <summary>The shape the reporter must send: dispatch the complete report, a driver the bare one.</summary>
    public static bool IsValidReportShape(OpenIncidentCommand command, IncidentReporter reporter) => reporter switch
    {
        IncidentReporter.Dispatch => IsDispatchReportShape(command),
        IncidentReporter.Driver => IsDriverReportShape(command),
        _ => false,
    };

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
    /// Returns the ciphertext AI-06 persists in <c>description_ciphertext</c> together with the key
    /// version the protector selected for <c>pii_key_version</c> (ADP-001: the server chooses it,
    /// never the client). An implementation that cannot protect the value throws
    /// <see cref="IncidentPiiProtectionUnavailableException"/> instead of returning plaintext.
    /// </summary>
    Task<ProtectedIncidentDescription> ProtectAsync(
        IncidentPiiBinding binding,
        string plaintext,
        CancellationToken cancellationToken);
}

/// <summary>
/// The incident row a protected description belongs to: the order owner organization (the
/// incident's <c>owner_org_id</c>) and the incident id. Production protectors authenticate both.
/// </summary>
public readonly record struct IncidentPiiBinding(Guid OwnerOrganizationId, Guid IncidentId);

public sealed record ProtectedIncidentDescription(byte[] Ciphertext, string KeyVersion);

public sealed class IncidentPiiProtectionUnavailableException(Exception? innerException = null)
    : Exception("Incident PII protection is unavailable.", innerException);
