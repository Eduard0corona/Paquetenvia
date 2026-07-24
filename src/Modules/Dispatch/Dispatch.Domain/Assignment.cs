namespace Dispatch.Domain;

public enum AssignmentType
{
    Own,
    External,
    AllyCapacity,
}

public enum AssignmentStatus
{
    Offered,
    Accepted,
    Active,
    Completed,
    Cancelled,
}

public static class AssignmentContractValues
{
    public static string ToContractValue(this AssignmentType value) => value switch
    {
        AssignmentType.Own => "OWN",
        AssignmentType.External => "EXTERNAL",
        AssignmentType.AllyCapacity => "ALLY_CAPACITY",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public static string ToContractValue(this AssignmentStatus value) => value switch
    {
        AssignmentStatus.Offered => "OFFERED",
        AssignmentStatus.Accepted => "ACCEPTED",
        AssignmentStatus.Active => "ACTIVE",
        AssignmentStatus.Completed => "COMPLETED",
        AssignmentStatus.Cancelled => "CANCELLED",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public static bool TryParseAssignmentType(string? value, out AssignmentType type)
    {
        type = value switch
        {
            "OWN" => AssignmentType.Own,
            "EXTERNAL" => AssignmentType.External,
            "ALLY_CAPACITY" => AssignmentType.AllyCapacity,
            _ => default,
        };
        return value is not null &&
            string.Equals(type.ToContractValue(), value, StringComparison.Ordinal);
    }
}

public sealed class Assignment
{
    public Assignment(
        Guid id,
        Guid orderId,
        Guid ownerOrganizationId,
        Guid? operatorOrganizationId,
        Guid driverId,
        Guid? routeId,
        AssignmentType assignmentType,
        AssignmentStatus status,
        long costCents,
        DateTimeOffset? acceptedAt,
        DateTimeOffset createdAt)
    {
        if (id == Guid.Empty || orderId == Guid.Empty || ownerOrganizationId == Guid.Empty ||
            driverId == Guid.Empty)
        {
            throw new ArgumentException("Assignment identifiers must be non-empty.");
        }

        if (operatorOrganizationId == Guid.Empty || routeId == Guid.Empty)
        {
            throw new ArgumentException("Optional assignment identifiers must be non-empty when present.");
        }

        if (costCents < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(costCents));
        }

        if (createdAt.Offset != TimeSpan.Zero || acceptedAt?.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Assignment timestamps must be UTC.");
        }

        Id = id;
        OrderId = orderId;
        OwnerOrganizationId = ownerOrganizationId;
        OperatorOrganizationId = operatorOrganizationId;
        DriverId = driverId;
        RouteId = routeId;
        AssignmentType = assignmentType;
        Status = status;
        CostCents = costCents;
        AcceptedAt = acceptedAt;
        CreatedAt = createdAt;
    }

    public Guid Id { get; }
    public Guid OrderId { get; }
    public Guid OwnerOrganizationId { get; }
    public Guid? OperatorOrganizationId { get; }
    public Guid DriverId { get; }
    public Guid? RouteId { get; }
    public AssignmentType AssignmentType { get; }
    public AssignmentStatus Status { get; }
    public long CostCents { get; }
    public DateTimeOffset? AcceptedAt { get; }
    public DateTimeOffset CreatedAt { get; }
}

public enum AssignmentRuleCode
{
    Allowed,
    InvalidIdentifier,
    UnsupportedType,
    RouteNotSupported,
    InvalidCost,
    InvalidSourceState,
    InvalidTenant,
}

public sealed record AssignmentRuleResult(bool Allowed, AssignmentRuleCode Code)
{
    public static AssignmentRuleResult Success { get; } = new(true, AssignmentRuleCode.Allowed);
    public static AssignmentRuleResult Rejected(AssignmentRuleCode code) => new(false, code);
}

public static class ManualOwnAssignmentPolicy
{
    public static AssignmentRuleResult Evaluate(
        Guid orderId,
        Guid driverId,
        AssignmentType assignmentType,
        Guid? routeId,
        long costCents,
        string sourceStatus)
    {
        if (orderId == Guid.Empty || driverId == Guid.Empty)
        {
            return AssignmentRuleResult.Rejected(AssignmentRuleCode.InvalidIdentifier);
        }

        if (assignmentType != AssignmentType.Own)
        {
            return AssignmentRuleResult.Rejected(AssignmentRuleCode.UnsupportedType);
        }

        if (routeId is not null)
        {
            return AssignmentRuleResult.Rejected(AssignmentRuleCode.RouteNotSupported);
        }

        if (costCents < 0)
        {
            return AssignmentRuleResult.Rejected(AssignmentRuleCode.InvalidCost);
        }

        return sourceStatus is "READY_FOR_PICKUP" or "RESCHEDULED"
            ? AssignmentRuleResult.Success
            : AssignmentRuleResult.Rejected(AssignmentRuleCode.InvalidSourceState);
    }

    public static Guid? DeriveOperatorOrganization(
        Guid activeOrganizationId,
        Guid ownerOrganizationId,
        Guid? currentOperatorOrganizationId)
    {
        if (activeOrganizationId == ownerOrganizationId)
        {
            return null;
        }

        return currentOperatorOrganizationId == activeOrganizationId
            ? activeOrganizationId
            : throw new InvalidOperationException("The active organization is not an order tenant.");
    }

    public static Assignment CreateAccepted(
        Guid id,
        Guid orderId,
        Guid ownerOrganizationId,
        Guid? operatorOrganizationId,
        Guid driverId,
        long costCents,
        DateTimeOffset occurredAt) =>
        new(
            id,
            orderId,
            ownerOrganizationId,
            operatorOrganizationId,
            driverId,
            null,
            AssignmentType.Own,
            AssignmentStatus.Accepted,
            costCents,
            occurredAt,
            occurredAt);
}
