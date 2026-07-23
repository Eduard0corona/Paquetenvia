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
    string? guardCode = null)
    : Exception("The order transition conflicts with current state.")
{
    public OrderTransitionConflictCode Code { get; } = code;
    public string? GuardCode { get; } = guardCode;
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
                    !string.Equals(properties[0].Name, "restricted_goods_acknowledged", StringComparison.Ordinal) ||
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
                    !string.Equals(properties[0].Name, "incident_id", StringComparison.Ordinal) ||
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
        (OrderStatus.Rescheduled, OrderStatus.Delivering),
        (OrderStatus.Returning, OrderStatus.Returned),
    ];

    public bool IsAuthorized(OrderTransitionAuthorizationContext context) => context.ActiveRole switch
    {
        "PLATFORM_ADMIN" => context.MfaSatisfied,
        "DISPATCHER" => true,
        "DRIVER" => context.HasMatchingDriverAssignment &&
            DriverTransitions.Contains((context.Source, context.Target)),
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
        CancellationToken cancellationToken);
}

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

public sealed record IncidentGuardSnapshot(
    bool RequestedIncidentValid,
    bool RequestedIncidentCustodyAcquired,
    bool AnyCustodyAcquired,
    bool HasUnresolvedIncident);

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
