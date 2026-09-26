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
/// D8: Dispatch closes assignments by reacting, with eventual consistency, to the committed
/// <c>orders.status-changed</c> fact. No new cross-module atomic flow is introduced.
/// </summary>
public static class AssignmentLifecyclePolicy
{
    public static AssignmentClosure Resolve(string previousStatus, string newStatus) =>
        (previousStatus, newStatus) switch
        {
            ("ASSIGNED", "READY_FOR_PICKUP") => AssignmentClosure.Cancelled,
            ("RESCHEDULED", "READY_FOR_PICKUP") => AssignmentClosure.Cancelled,
            (_, "CANCELLED") => AssignmentClosure.Cancelled,
            (_, "DELIVERED") => AssignmentClosure.Completed,
            (_, "RETURNED") => AssignmentClosure.Completed,
            _ => AssignmentClosure.None,
        };

    public static string ToContractValue(this AssignmentClosure closure) => closure switch
    {
        AssignmentClosure.Cancelled => "CANCELLED",
        AssignmentClosure.Completed => "COMPLETED",
        _ => throw new ArgumentOutOfRangeException(nameof(closure), closure, null),
    };
}

/// <summary>
/// The subset of an <c>order-status-changed-v1</c> outbox payload Dispatch needs. It carries no PII.
/// </summary>
public sealed record OrderStatusChangedFact(
    Guid EventId,
    Guid OwnerOrganizationId,
    Guid OrderId,
    string PreviousStatus,
    string NewStatus,
    DateTimeOffset OccurredAt,
    Guid? AssignmentId)
{
    public const string SchemaVersion = "order-status-changed-v1";

    public static bool TryParse(
        Guid eventId,
        Guid ownerOrganizationId,
        Guid aggregateId,
        string payload,
        out OrderStatusChangedFact? fact)
    {
        fact = null;
        if (eventId == Guid.Empty || ownerOrganizationId == Guid.Empty || aggregateId == Guid.Empty)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !TryString(root, "schema_version", out var schema) || schema != SchemaVersion ||
                !TryGuid(root, "order_id", out var orderId) || orderId != aggregateId ||
                !TryString(root, "previous_status", out var previous) ||
                !TryString(root, "new_status", out var next) ||
                !root.TryGetProperty("occurred_at", out var occurred) ||
                occurred.ValueKind != JsonValueKind.String ||
                !occurred.TryGetDateTimeOffset(out var occurredAt))
            {
                return false;
            }

            Guid? assignmentId = null;
            if (root.TryGetProperty("assignment_id", out var assignment) &&
                assignment.ValueKind != JsonValueKind.Null)
            {
                if (assignment.ValueKind != JsonValueKind.String || !assignment.TryGetGuid(out var parsed))
                {
                    return false;
                }

                assignmentId = parsed;
            }

            fact = new(eventId, ownerOrganizationId, orderId, previous!, next!, occurredAt.ToUniversalTime(), assignmentId);
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
            element.TryGetGuid(out value);
    }
}

/// <summary>
/// Idempotent Dispatch reaction to a committed order transition. Replays and stale deliveries
/// are no-ops: only ACCEPTED/ACTIVE assignments created at or before the transition (and, when
/// the fact names one, only that assignment) are closed.
/// </summary>
public interface IAssignmentLifecycleReactor
{
    Task<int> ReactAsync(OrderStatusChangedFact fact, CancellationToken cancellationToken);
}
