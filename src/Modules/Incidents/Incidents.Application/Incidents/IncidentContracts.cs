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
}

/// <summary>
/// Shape validation for the INC-001 opening contract. Every acceptance criterion that the
/// backlog calls mandatory is rejected here before any tenant transaction is opened.
/// </summary>
public static class IncidentRequestPolicy
{
    public const int MaximumDescriptionLength = 2_000;
    public const int MaximumIncidentTypeLength = 64;

    /// <summary>
    /// An attempt cannot be reported as having happened in the future, and reports older than
    /// this are treated as malformed rather than silently backdating the SLA clock.
    /// </summary>
    public static readonly TimeSpan MaximumOccurrenceAge = TimeSpan.FromHours(24);
    public static readonly TimeSpan MaximumOccurrenceSkew = TimeSpan.FromMinutes(5);

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

    public static bool IsValidOccurrence(DateTimeOffset occurredAt, DateTimeOffset now) =>
        occurredAt != default &&
        occurredAt <= now + MaximumOccurrenceSkew &&
        occurredAt >= now - MaximumOccurrenceAge;

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
