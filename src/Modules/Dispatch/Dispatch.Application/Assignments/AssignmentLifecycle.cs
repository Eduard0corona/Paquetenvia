using System.Text.Json;

namespace Dispatch.Application.Assignments;

/// <summary>Terminal assignment status Dispatch derives from a committed order transition (D8).</summary>
public enum AssignmentClosure
{
    None,
    Cancelled,
    Completed,
}

/// <summary>
/// D8 (<c>D8-DISPATCH-OUTBOX-CLOSURE</c>): Dispatch closes assignments by reacting, with eventual
/// consistency, to committed order transitions delivered through the DISPATCH outbox lane
/// (<c>D8-OUTBOX-LANE-DISPATCH</c>). No new cross-module atomic flow is introduced (AI-13 §4).
/// </summary>
/// <remarks>
/// The map is explicit for every AI-04 <c>order_state_machine.transitions</c> edge; an unknown edge
/// resolves to <see cref="AssignmentClosure.None"/>.
/// <list type="bullet">
/// <item><c>*→CANCELLED</c> and the ORD-002 unassign <c>ASSIGNED→READY_FOR_PICKUP</c> cancel.</item>
/// <item><c>FAILED_ATTEMPT→RESCHEDULED</c> cancels (<c>D8-REASSIGNMENT-NEW-ASSIGNMENT</c>): a reschedule
/// never keeps the previous assignment, so <c>RESCHEDULED→ASSIGNED</c> always needs a new assignment
/// created by DSP-002 or a new external offer. <c>RESCHEDULED→READY_FOR_PICKUP</c> also cancels, as a
/// backstop for an assignment that survived the reschedule.</item>
/// <item><c>DELIVERING→DELIVERED</c> and <c>RETURNING→RETURNED</c> complete.</item>
/// <item>A retry (<c>FAILED_ATTEMPT→DELIVERING</c>) and a return (<c>*→RETURNING</c>) keep the active
/// assignment: the same driver still holds custody and neither is a reschedule.</item>
/// </list>
/// </remarks>
public static class AssignmentLifecyclePolicy
{
    private static readonly Dictionary<(string Previous, string New), AssignmentClosure> Map = new()
    {
        [("DRAFT", "CONFIRMED")] = AssignmentClosure.None,
        [("DRAFT", "CANCELLED")] = AssignmentClosure.Cancelled,
        [("CONFIRMED", "READY_FOR_PICKUP")] = AssignmentClosure.None,
        [("CONFIRMED", "CANCELLED")] = AssignmentClosure.Cancelled,
        [("READY_FOR_PICKUP", "ASSIGNED")] = AssignmentClosure.None,
        [("READY_FOR_PICKUP", "CANCELLED")] = AssignmentClosure.Cancelled,
        [("ASSIGNED", "AT_PICKUP")] = AssignmentClosure.None,
        [("ASSIGNED", "READY_FOR_PICKUP")] = AssignmentClosure.Cancelled,
        [("ASSIGNED", "CANCELLED")] = AssignmentClosure.Cancelled,
        [("AT_PICKUP", "PICKED_UP")] = AssignmentClosure.None,
        [("AT_PICKUP", "FAILED_ATTEMPT")] = AssignmentClosure.None,
        [("AT_PICKUP", "CANCELLED")] = AssignmentClosure.Cancelled,
        [("PICKED_UP", "IN_TRANSIT")] = AssignmentClosure.None,
        [("PICKED_UP", "RETURNING")] = AssignmentClosure.None,
        [("IN_TRANSIT", "DELIVERING")] = AssignmentClosure.None,
        [("IN_TRANSIT", "FAILED_ATTEMPT")] = AssignmentClosure.None,
        [("IN_TRANSIT", "RETURNING")] = AssignmentClosure.None,
        [("DELIVERING", "DELIVERED")] = AssignmentClosure.Completed,
        [("DELIVERING", "FAILED_ATTEMPT")] = AssignmentClosure.None,
        [("FAILED_ATTEMPT", "RESCHEDULED")] = AssignmentClosure.Cancelled,
        [("FAILED_ATTEMPT", "RETURNING")] = AssignmentClosure.None,
        [("FAILED_ATTEMPT", "DELIVERING")] = AssignmentClosure.None,
        [("RESCHEDULED", "READY_FOR_PICKUP")] = AssignmentClosure.Cancelled,
        [("RESCHEDULED", "ASSIGNED")] = AssignmentClosure.None,
        [("RESCHEDULED", "DELIVERING")] = AssignmentClosure.None,
        [("RETURNING", "RETURNED")] = AssignmentClosure.Completed,
        [("DELIVERED", "CLOSED")] = AssignmentClosure.None,
        [("DELIVERED", "CLAIM_OPEN")] = AssignmentClosure.None,
        [("CLOSED", "CLAIM_OPEN")] = AssignmentClosure.None,
        [("CLAIM_OPEN", "CLAIM_RESOLVED")] = AssignmentClosure.None,
    };

    /// <summary>Every AI-04 transition the map covers, for exhaustiveness checks.</summary>
    public static IReadOnlyCollection<(string Previous, string New)> Transitions => Map.Keys;

    public static AssignmentClosure Resolve(string previousStatus, string newStatus) =>
        Map.TryGetValue((previousStatus, newStatus), out var closure) ? closure : AssignmentClosure.None;

    public static string ToContractValue(this AssignmentClosure closure) => closure switch
    {
        AssignmentClosure.Cancelled => "CANCELLED",
        AssignmentClosure.Completed => "COMPLETED",
        _ => throw new ArgumentOutOfRangeException(nameof(closure), closure, null),
    };
}

/// <summary>
/// The <c>dispatch.order-status-reaction-requested</c> outbox payload (<c>order-status-reaction-v1</c>)
/// Orders writes in the ORD-002 transaction when a committed transition closes the assignment that was
/// active at that moment (D8-OUTBOX-LANE-DISPATCH). It carries identifiers and statuses only, no PII.
/// </summary>
public sealed record OrderStatusChangedFact(
    Guid EventId,
    Guid OwnerOrganizationId,
    Guid OrderId,
    int OrderVersion,
    Guid OrderEventId,
    string PreviousStatus,
    string NewStatus,
    DateTimeOffset OccurredAt,
    Guid AssignmentId)
{
    public const string Topic = "dispatch.order-status-reaction-requested";
    public const string SchemaVersion = "order-status-reaction-v1";

    private static readonly string[] Properties =
    [
        "schema_version",
        "order_event_id",
        "order_id",
        "previous_status",
        "new_status",
        "occurred_at",
        "assignment_id",
    ];

    public static bool TryParse(
        Guid eventId,
        Guid ownerOrganizationId,
        Guid aggregateId,
        int? aggregateVersion,
        string payload,
        out OrderStatusChangedFact? fact)
    {
        fact = null;
        if (eventId == Guid.Empty || ownerOrganizationId == Guid.Empty || aggregateId == Guid.Empty ||
            aggregateVersion is not > 0)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                root.EnumerateObject().Count() != Properties.Length ||
                Properties.Any(name => !root.TryGetProperty(name, out _)) ||
                !TryString(root, "schema_version", out var schema) || schema != SchemaVersion ||
                !TryGuid(root, "order_event_id", out var orderEventId) ||
                !TryGuid(root, "order_id", out var orderId) || orderId != aggregateId ||
                !TryString(root, "previous_status", out var previous) ||
                !TryString(root, "new_status", out var next) ||
                !TryGuid(root, "assignment_id", out var assignmentId) ||
                root.GetProperty("occurred_at").ValueKind != JsonValueKind.String ||
                !root.GetProperty("occurred_at").TryGetDateTimeOffset(out var occurredAt))
            {
                return false;
            }

            fact = new(
                eventId,
                ownerOrganizationId,
                orderId,
                aggregateVersion.Value,
                orderEventId,
                previous!,
                next!,
                occurredAt.ToUniversalTime(),
                assignmentId);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryString(JsonElement root, string name, out string? value)
    {
        value = null;
        if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString();
        return !string.IsNullOrEmpty(value);
    }

    private static bool TryGuid(JsonElement root, string name, out Guid value)
    {
        value = Guid.Empty;
        return root.TryGetProperty(name, out var element) &&
            element.ValueKind == JsonValueKind.String &&
            element.TryGetGuid(out value) &&
            value != Guid.Empty;
    }
}

/// <summary>Outcome of one DISPATCH lane reaction.</summary>
public enum AssignmentReactionOutcome
{
    /// <summary>The named assignment was closed; AssignmentChanged and audit were written.</summary>
    Closed,

    /// <summary>Replay, stale redelivery or non-closing transition: nothing changed, row settled.</summary>
    NoOp,

    /// <summary>The lease was lost before settlement; the whole reaction rolled back.</summary>
    LeaseLost,
}
