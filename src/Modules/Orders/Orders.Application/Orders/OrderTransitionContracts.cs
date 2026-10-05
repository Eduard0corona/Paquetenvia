using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Orders.Domain;

namespace Orders.Application.Orders;

public sealed record TransitionOrderCommand(
    Guid ActorId,
    Guid OrganizationId,
    string IdempotencyKey,
    Guid OrderId,
    string? TargetStatus,
    string? Reason,
    int ExpectedVersion,
    string? MetadataJson,
    bool MfaSatisfied,
    string? RequestId);

public interface IOrderTransitionService
{
    Task<OrderResult> TransitionAsync(
        TransitionOrderCommand command,
        CancellationToken cancellationToken);
}

public enum OrderTransitionConflictCode
{
    InvalidRequest,
    OrderUnavailable,
    InvalidState,
    TerminalState,
    VersionConflict,
    GuardNotSatisfied,
    IdempotencyConflict,
    ConcurrencyConflict,
    DependencyUnavailable,
}

public sealed class OrderTransitionConflictException(
    OrderTransitionConflictCode code,
    string? guardCode = null,
    string? rejectionCode = null)
    : Exception("The order transition conflicts with current state.")
{
    public OrderTransitionConflictCode Code { get; } = code;
    public string? GuardCode { get; } = guardCode;

    /// <summary>
    /// ORD-002-GUARD-CODES-2026-10-05: the AI-05 rule code (<see cref="OrderTransitionRejectionCodes"/>) the 409 may
    /// carry. Null keeps the uniform 409 without a code: it is set only after the order was locked for the selected
    /// owner organization and the caller holds the transitionOrder capability on it.
    /// </summary>
    public string? RejectionCode { get; } = rejectionCode;
}

public sealed class OrderTransitionForbiddenException : Exception
{
    public OrderTransitionForbiddenException()
        : base("The actor lacks the required order transition capability.")
    {
    }
}

public sealed class OrderTransitionInfrastructureException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public sealed record NormalizedTransitionMetadata(
    string Json,
    bool? RestrictedGoodsAcknowledged,
    Guid? IncidentId)
{
    public static NormalizedTransitionMetadata Empty { get; } = new("{}", null, null);
}

public static class OrderTransitionInputPolicy
{
    public const int MaximumReasonLength = 500;
    public const int MaximumMetadataDepth = 2;
    public const int DefaultMaximumMetadataUtf8Bytes = 4_096;

    /// <summary>The only metadata member DRAFT -> CONFIRMED accepts; the restricted_goods_check guard needs it true.</summary>
    public const string RestrictedGoodsAcknowledgedKey = "restricted_goods_acknowledged";

    /// <summary>The only metadata member a FAILED_ATTEMPT target accepts, and the one it requires.</summary>
    public const string IncidentIdKey = "incident_id";

    private static readonly IReadOnlyList<string> NoRequiredMetadata = [];
    private static readonly IReadOnlyList<string> RestrictedGoodsMetadata = [RestrictedGoodsAcknowledgedKey];
    private static readonly IReadOnlyList<string> IncidentMetadata = [IncidentIdKey];

    /// <summary>
    /// UI-PHASE2-SEARCH-TRANSITIONS-2026-10-05: the metadata members a transitionOrder request for this edge must
    /// carry to be accepted, read from the same keys <see cref="TryNormalizeMetadata"/> parses: DRAFT -> CONFIRMED
    /// needs <c>restricted_goods_acknowledged</c> (true) and every FAILED_ATTEMPT target needs <c>incident_id</c>.
    /// Every other edge takes no metadata. The reason is always required and is not listed here.
    /// </summary>
    public static IReadOnlyList<string> RequiredMetadataKeys(OrderStatus source, OrderStatus target)
    {
        if (source == OrderStatus.Draft && target == OrderStatus.Confirmed)
        {
            return RestrictedGoodsMetadata;
        }

        return target == OrderStatus.FailedAttempt ? IncidentMetadata : NoRequiredMetadata;
    }

    public static bool TryNormalizeMetadata(
        string? metadataJson,
        OrderStatus source,
        OrderStatus target,
        int maximumUtf8Bytes,
        out NormalizedTransitionMetadata metadata)
    {
        metadata = NormalizedTransitionMetadata.Empty;
        if (metadataJson is null)
        {
            return true;
        }

        if (Encoding.UTF8.GetByteCount(metadataJson) > maximumUtf8Bytes)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(metadataJson, new JsonDocumentOptions
            {
                MaxDepth = MaximumMetadataDepth,
            });
            if (document.RootElement.ValueKind == JsonValueKind.Null)
            {
                return true;
            }

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var properties = document.RootElement.EnumerateObject().ToArray();
            if (source == OrderStatus.Draft && target == OrderStatus.Confirmed)
            {
                if (properties.Length == 0)
                {
                    return true;
                }

                if (properties.Length != 1 ||
                    !string.Equals(properties[0].Name, RestrictedGoodsAcknowledgedKey, StringComparison.Ordinal) ||
                    properties[0].Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    return false;
                }

                var value = properties[0].Value.GetBoolean();
                metadata = new NormalizedTransitionMetadata(
                    value ? "{\"restricted_goods_acknowledged\":true}" : "{\"restricted_goods_acknowledged\":false}",
                    value,
                    null);
                return true;
            }

            if (target == OrderStatus.FailedAttempt)
            {
                if (properties.Length != 1 ||
                    !string.Equals(properties[0].Name, IncidentIdKey, StringComparison.Ordinal) ||
                    properties[0].Value.ValueKind != JsonValueKind.String ||
                    !Guid.TryParseExact(properties[0].Value.GetString(), "D", out var incidentId) ||
                    incidentId == Guid.Empty)
                {
                    return false;
                }

                metadata = new NormalizedTransitionMetadata(
                    $"{{\"incident_id\":\"{incidentId:D}\"}}",
                    null,
                    incidentId);
                return true;
            }

            return properties.Length == 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool IsValidMetadataForTarget(
        string? targetStatus,
        string? metadataJson,
        int maximumUtf8Bytes) =>
        OrderContractValues.TryParseOrderStatus(targetStatus, out var target) &&
        TryNormalizeMetadata(
            metadataJson,
            target == OrderStatus.Confirmed ? OrderStatus.Draft : default,
            target,
            maximumUtf8Bytes,
            out _);

    public static bool IsValidCommandShape(TransitionOrderCommand command, int maximumMetadataBytes) =>
        command.ActorId != Guid.Empty &&
        command.OrganizationId != Guid.Empty &&
        command.OrderId != Guid.Empty &&
        !string.IsNullOrWhiteSpace(command.Reason) &&
        command.Reason.Length <= MaximumReasonLength &&
        command.ExpectedVersion >= 1 &&
        OrderContractValues.TryParseOrderStatus(command.TargetStatus, out _) &&
        maximumMetadataBytes is >= 256 and <= 16_384;
}

public static class OrderTransitionCanonicalizer
{
    public static byte[] ComputeSha256(
        TransitionOrderCommand command,
        OrderStatus target,
        NormalizedTransitionMetadata metadata)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("tenant", command.OrganizationId.ToString("D", CultureInfo.InvariantCulture));
            writer.WriteString("order_id", command.OrderId.ToString("D", CultureInfo.InvariantCulture));
            writer.WriteString("target_status", target.ToContractValue());
            writer.WriteString("reason", command.Reason);
            writer.WriteNumber("expected_version", command.ExpectedVersion);
            writer.WritePropertyName("metadata");
            using var metadataDocument = JsonDocument.Parse(metadata.Json);
            metadataDocument.RootElement.WriteTo(writer);
            writer.WriteEndObject();
        }

        return SHA256.HashData(stream.ToArray());
    }
}

public sealed record OrderTransitionAuthorizationContext(
    string? ActiveRole,
    OrderStatus Source,
    OrderStatus Target,
    bool MfaSatisfied,
    bool HasMatchingDriverAssignment);

public interface IOrderTransitionAuthorizer
{
    bool IsAuthorized(OrderTransitionAuthorizationContext context);

    /// <summary>
    /// ORD-002-GUARD-CODES-2026-10-05: whether the actor holds the transitionOrder capability on this order at all,
    /// whatever the edge: the role rules of <see cref="IsAuthorized"/> without the per-edge driver list (DISPATCHER;
    /// PLATFORM_ADMIN with a satisfied MFA challenge; DRIVER only while holding the order's ACCEPTED or ACTIVE
    /// assignment). Only such an actor receives the rule code of a rejected transition.
    /// </summary>
    bool HoldsTransitionCapability(string? activeRole, bool mfaSatisfied, bool hasMatchingDriverAssignment);
}

public sealed class OrderTransitionAuthorizer : IOrderTransitionAuthorizer
{
    private static readonly HashSet<(OrderStatus Source, OrderStatus Target)> DriverTransitions =
    [
        (OrderStatus.Assigned, OrderStatus.AtPickup),
        (OrderStatus.AtPickup, OrderStatus.PickedUp),
        (OrderStatus.AtPickup, OrderStatus.FailedAttempt),
        (OrderStatus.PickedUp, OrderStatus.InTransit),
        (OrderStatus.PickedUp, OrderStatus.Returning),
        (OrderStatus.InTransit, OrderStatus.Delivering),
        (OrderStatus.InTransit, OrderStatus.FailedAttempt),
        (OrderStatus.InTransit, OrderStatus.Returning),
        (OrderStatus.Delivering, OrderStatus.Delivered),
        (OrderStatus.Delivering, OrderStatus.FailedAttempt),
        (OrderStatus.FailedAttempt, OrderStatus.Rescheduled),
        (OrderStatus.FailedAttempt, OrderStatus.Returning),
        (OrderStatus.FailedAttempt, OrderStatus.Delivering),
        (OrderStatus.Returning, OrderStatus.Returned),
    ];

    public bool IsAuthorized(OrderTransitionAuthorizationContext context) =>
        HoldsTransitionCapability(context.ActiveRole, context.MfaSatisfied, context.HasMatchingDriverAssignment) &&
        (context.ActiveRole != "DRIVER" || DriverTransitions.Contains((context.Source, context.Target)));

    public bool HoldsTransitionCapability(
        string? activeRole,
        bool mfaSatisfied,
        bool hasMatchingDriverAssignment) => activeRole switch
    {
        "PLATFORM_ADMIN" => mfaSatisfied,
        "DISPATCHER" => true,
        "DRIVER" => hasMatchingDriverAssignment,
        _ => false,
    };
}

public sealed record OrderTransitionAuthorizationSnapshot(
    string? ActiveRole,
    bool HasMatchingDriverAssignment);

public sealed record OrderTransitionReplayAuthorizationSnapshot(
    int MatchingEventCount,
    int? AggregateVersion,
    string? PreviousStatus,
    string? NewStatus,
    string? ActiveRole,
    bool HasMatchingDriverAssignment);

public interface IOrderTransitionReplayAuthorizationReader
{
    Task<OrderTransitionReplayAuthorizationSnapshot> ReadAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid actorId,
        Guid organizationId,
        Guid orderId,
        int aggregateVersion,
        CancellationToken cancellationToken);
}

public sealed record OrderTransitionReplayEvaluation(
    bool IsConsistent,
    OrderStatus Source,
    OrderStatus Target)
{
    public static OrderTransitionReplayEvaluation Inconsistent { get; } =
        new(false, default, default);
}

public static class OrderTransitionReplayPolicy
{
    public static OrderTransitionReplayEvaluation Evaluate(
        TransitionOrderCommand command,
        OrderStatus requestedTarget,
        OrderResult storedResponse,
        OrderTransitionReplayAuthorizationSnapshot snapshot)
    {
        if (command.ExpectedVersion == int.MaxValue)
        {
            return OrderTransitionReplayEvaluation.Inconsistent;
        }

        var expectedAggregateVersion = command.ExpectedVersion + 1;
        if (snapshot.MatchingEventCount != 1 ||
            snapshot.AggregateVersion != expectedAggregateVersion ||
            !OrderContractValues.TryParseOrderStatus(snapshot.PreviousStatus, out var source) ||
            !OrderContractValues.TryParseOrderStatus(snapshot.NewStatus, out var target) ||
            target != requestedTarget ||
            !OrderTransitionMatrix.AllowedTransitions.TryGetValue(source, out var allowedTargets) ||
            !allowedTargets.Contains(target) ||
            storedResponse.Id != command.OrderId ||
            storedResponse.OwnerOrganizationId != command.OrganizationId ||
            storedResponse.Version != expectedAggregateVersion ||
            !OrderContractValues.TryParseOrderStatus(storedResponse.Status, out var responseStatus) ||
            responseStatus != target)
        {
            return OrderTransitionReplayEvaluation.Inconsistent;
        }

        return new(true, source, target);
    }
}

public interface IOrderTransitionAuthorizationReader
{
    Task<OrderTransitionAuthorizationSnapshot> ReadAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid actorId,
        Guid organizationId,
        Guid orderId,
        CancellationToken cancellationToken);
}

public sealed record QuoteAcceptanceGuardSnapshot(
    bool ValidConsumedQuote,
    bool ValidAcceptance);

public interface IOrderQuoteAcceptanceGuardReader
{
    Task<QuoteAcceptanceGuardSnapshot> ReadAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid organizationId,
        Guid orderId,
        CancellationToken cancellationToken);
}

/// <summary>
/// The current assignment as the ASSIGNED and retry guards read it. <c>EligibleDriver</c> and
/// <c>CapacityAttested</c> are the DSP-002 <c>DriverEligibilityPolicy</c> verdict for the assigned
/// driver and the order's packages: eligibility covers profile, membership, city, service area and
/// documents; capacity covers the vehicle limits.
/// </summary>
public sealed record AssignmentGuardSnapshot(
    bool ExactlyOneActive,
    bool EligibleDriver,
    bool CapacityAttested,
    bool CostPresent);

public interface IOrderAssignmentGuardReader
{
    Task<AssignmentGuardSnapshot> ReadAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid organizationId,
        Guid orderId,
        Guid orderCityId,
        DateTimeOffset evaluatedAt,
        CancellationToken cancellationToken);
}

/// <summary>
/// Proofs of the current attempt only: a pickup photo created after the latest entry into
/// <c>AT_PICKUP</c> and a delivery photo or code created after the latest entry into
/// <c>DELIVERING</c>, never a proof that is already evidence of an incident.
/// </summary>
public sealed record ProofGuardSnapshot(
    bool PickupProofComplete,
    bool DeliveryProofComplete);

public interface IOrderProofGuardReader
{
    Task<ProofGuardSnapshot> ReadAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid organizationId,
        Guid orderId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Custody is acquired exactly when the order history holds a <c>PICKED_UP</c> status change.
/// ORD-002, INC-001 and the driver stops view share this single derivation.
/// </summary>
public sealed record CustodyGuardSnapshot(bool PickedUpRecorded);

public interface IOrderCustodyGuardReader
{
    Task<CustodyGuardSnapshot> ReadAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid organizationId,
        Guid orderId,
        CancellationToken cancellationToken);
}

/// <summary>
/// <c>RequestedIncidentValid</c> holds only for a pending incident of this order opened during
/// the current attempt (after the latest entry into the current status) that no earlier
/// <c>FAILED_ATTEMPT</c> already consumed. <c>LatestFailedAttemptNextAction</c> is the next action
/// of the incident that justified the latest <c>FAILED_ATTEMPT</c>.
/// <c>LatestFailedAttemptIncidentAdopted</c> marks that incident as one INC-001 adopted from a
/// pre-INC-001 installation: its next action was derived by the adoption backfill, never chosen by
/// anyone, so it does not bind the successor of the failed attempt.
/// </summary>
public sealed record IncidentGuardSnapshot(
    bool RequestedIncidentValid,
    bool RequestedIncidentCustodyAcquired,
    bool AnyCustodyAcquired,
    bool HasUnresolvedIncident,
    string? LatestFailedAttemptNextAction = null,
    bool LatestFailedAttemptIncidentAdopted = false);

public interface IOrderIncidentGuardReader
{
    Task<IncidentGuardSnapshot> ReadAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid organizationId,
        Guid orderId,
        Guid? requestedIncidentId,
        CancellationToken cancellationToken);
}

public sealed record CodGuardSnapshot(
    bool HasRecord,
    string? Status,
    long? AmountCents);

public interface IOrderCodGuardReader
{
    Task<CodGuardSnapshot> ReadAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid organizationId,
        Guid orderId,
        CancellationToken cancellationToken);
}
