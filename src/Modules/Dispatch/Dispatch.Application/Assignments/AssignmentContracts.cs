using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Dispatch.Domain;
using Drivers.Application.Eligibility;

namespace Dispatch.Application.Assignments;

public sealed record CreateOwnDriverAssignmentCommand(
    Guid ActorId,
    Guid OrganizationId,
    string IdempotencyKey,
    Guid OrderId,
    Guid DriverId,
    string? AssignmentType,
    long? CostCents,
    Guid? RouteId,
    bool MfaSatisfied,
    string? RequestId);

public sealed record MoneyResult(string Currency, long AmountCents);

public sealed record AssignmentResult(
    Guid Id,
    Guid OrderId,
    Guid DriverId,
    string Status,
    MoneyResult Cost);

public interface IAssignmentService
{
    Task<AssignmentResult> CreateOwnDriverAssignmentAsync(
        CreateOwnDriverAssignmentCommand command,
        CancellationToken cancellationToken);
}

public enum AssignmentConflictCode
{
    InvalidRequest,
    OrderUnavailable,
    InvalidOrderState,
    ActiveAssignmentExists,
    DriverIneligible,
    DriverDocumentExpired,
    CapacityInsufficient,
    IdempotencyConflict,
    InconsistentReplayEvidence,
    ConcurrencyConflict,
}

public sealed class AssignmentConflictException(
    AssignmentConflictCode code,
    Exception? innerException = null)
    : Exception("The assignment conflicts with current state.", innerException)
{
    public AssignmentConflictCode Code { get; } = code;
}

public sealed class AssignmentForbiddenException : Exception
{
    public AssignmentForbiddenException()
        : base("The actor lacks the required dispatch capability.")
    {
    }
}

public sealed class AssignmentNotFoundException : Exception
{
    public AssignmentNotFoundException()
        : base("The assignment resource is missing or inaccessible.")
    {
    }
}

public sealed class AssignmentInfrastructureException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public static class AssignmentInputPolicy
{
    public static bool IsValid(CreateOwnDriverAssignmentCommand command) =>
        command.ActorId != Guid.Empty &&
        command.OrganizationId != Guid.Empty &&
        command.OrderId != Guid.Empty &&
        command.DriverId != Guid.Empty &&
        command.AssignmentType == "OWN" &&
        command.CostCents is >= 0 &&
        command.RouteId is null;
}

public static class AssignmentCanonicalizer
{
    public static byte[] ComputeSha256(CreateOwnDriverAssignmentCommand command)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("tenant", command.OrganizationId.ToString("D", CultureInfo.InvariantCulture));
            writer.WriteString("order_id", command.OrderId.ToString("D", CultureInfo.InvariantCulture));
            writer.WriteString("driver_id", command.DriverId.ToString("D", CultureInfo.InvariantCulture));
            writer.WriteString("assignment_type", command.AssignmentType?.ToUpperInvariant());
            writer.WriteNumber("cost_cents", command.CostCents!.Value);
            writer.WriteNull("route_id");
            writer.WriteEndObject();
        }

        return SHA256.HashData(stream.ToArray());
    }
}

public sealed record DispatchAuthorizationSnapshot(
    string? ActiveRole,
    bool UserActive,
    bool MembershipActive);

public sealed record DispatchAssignmentAuthorizationContext(
    string? ActiveRole,
    bool UserActive,
    bool MembershipActive,
    bool MfaSatisfied);

public interface IDispatchAssignmentAuthorizer
{
    bool IsAuthorized(DispatchAssignmentAuthorizationContext context);
}

public sealed class DispatchAssignmentAuthorizer : IDispatchAssignmentAuthorizer
{
    public bool IsAuthorized(DispatchAssignmentAuthorizationContext context) =>
        context.UserActive &&
        context.MembershipActive &&
        context.ActiveRole switch
        {
            "PLATFORM_ADMIN" => context.MfaSatisfied,
            "DISPATCHER" => true,
            _ => false,
        };
}

public interface IDispatchAuthorizationReader
{
    Task<DispatchAuthorizationSnapshot> ReadAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid actorId,
        Guid organizationId,
        CancellationToken cancellationToken);
}

public interface IDispatchDriverEligibilityReader
{
    Task<DriverEligibilitySnapshot?> ReadAsync(
        DbConnection connection,
        DbTransaction transaction,
        EvaluateOwnDriverEligibilityCommand command,
        CancellationToken cancellationToken);
}

public sealed record AssignmentVisibilityOrder(
    Guid Id,
    Guid OwnerOrganizationId,
    Guid? OperatorOrganizationId,
    Guid CityId,
    Guid? ServiceAreaId,
    string Status,
    int Version);

public sealed record AssignmentVisibilityPackage(
    int WeightGrams,
    string DimensionsJson);

public sealed record AssignmentOrderVisibilityData(
    AssignmentVisibilityOrder? Order,
    IReadOnlyList<AssignmentVisibilityPackage> Packages)
{
    public IReadOnlyList<AssignmentVisibilityPackage> Packages { get; } = Packages.ToArray();
}

public sealed record AssignmentVisibilityResolution(
    AssignmentVisibilityOrder? Order,
    IReadOnlyList<AssignmentVisibilityPackage> Packages,
    DriverEligibilitySnapshot? Driver)
{
    public IReadOnlyList<AssignmentVisibilityPackage> Packages { get; } = Packages.ToArray();
    public bool IsVisible => Order is not null && Driver is not null;
}

public interface IAssignmentVisibilityDataReader
{
    Task<AssignmentOrderVisibilityData> ReadOrderAndPackagesAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid organizationId,
        Guid orderId,
        CancellationToken cancellationToken);

    Task<DriverEligibilitySnapshot?> ReadDriverProfileAndDocumentsAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid organizationId,
        Guid driverId,
        Guid cityId,
        Guid? serviceAreaId,
        CancellationToken cancellationToken);
}

public interface IAssignmentVisibilityResolver
{
    Task<AssignmentVisibilityResolution> ResolveAsync(
        DbConnection connection,
        DbTransaction transaction,
        CreateOwnDriverAssignmentCommand command,
        CancellationToken cancellationToken);
}

public sealed class DispatchAssignmentVisibilityResolver(
    IAssignmentVisibilityDataReader dataReader) : IAssignmentVisibilityResolver
{
    public static IReadOnlyList<string> StructuralPlan { get; } =
        ["order_packages", "driver_profile_documents"];

    public async Task<AssignmentVisibilityResolution> ResolveAsync(
        DbConnection connection,
        DbTransaction transaction,
        CreateOwnDriverAssignmentCommand command,
        CancellationToken cancellationToken)
    {
        var order = await dataReader.ReadOrderAndPackagesAsync(
            connection,
            transaction,
            command.OrganizationId,
            command.OrderId,
            cancellationToken);
        var driver = await dataReader.ReadDriverProfileAndDocumentsAsync(
            connection,
            transaction,
            command.OrganizationId,
            command.DriverId,
            order.Order?.CityId ?? Guid.Empty,
            order.Order?.ServiceAreaId,
            cancellationToken);
        return new(order.Order, order.Packages, driver);
    }
}

public sealed record AssignmentReplayEvidence(
    int MatchingAssignmentCount,
    Guid? AssignmentId,
    Guid? OrderId,
    Guid? DriverId,
    string? Status,
    long? CostCents,
    int MatchingTransitionEventCount,
    string? PreviousStatus,
    string? NewStatus);

public static class AssignmentReplayPolicy
{
    public static bool IsConsistent(
        CreateOwnDriverAssignmentCommand command,
        AssignmentResult stored,
        Guid resourceId,
        AssignmentReplayEvidence evidence) =>
        stored.Id == resourceId &&
        stored.OrderId == command.OrderId &&
        stored.DriverId == command.DriverId &&
        stored.Status == "ACCEPTED" &&
        stored.Cost.Currency == "MXN" &&
        stored.Cost.AmountCents == command.CostCents &&
        evidence.MatchingAssignmentCount == 1 &&
        evidence.AssignmentId == resourceId &&
        evidence.OrderId == command.OrderId &&
        evidence.DriverId == command.DriverId &&
        evidence.Status is "ACCEPTED" or "ACTIVE" &&
        evidence.CostCents == command.CostCents &&
        evidence.MatchingTransitionEventCount == 1 &&
        evidence.PreviousStatus is "READY_FOR_PICKUP" or "RESCHEDULED" &&
        evidence.NewStatus == "ASSIGNED";
}

public interface IAssignmentReplayEvidenceReader
{
    Task<AssignmentReplayEvidence> ReadAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid organizationId,
        Guid assignmentId,
        Guid orderId,
        Guid driverId,
        long costCents,
        CancellationToken cancellationToken);
}

public enum AssignmentTransactionStage
{
    IdempotencyReserved,
    OrderLocked,
    PackagesRead,
    EligibilityEvaluated,
    AssignmentInserted,
    OrderUpdated,
    EventInserted,
    OutboxInserted,
    AssignmentAuditInserted,
    TransitionAuditInserted,
    BeforeIdempotencyCompletion,
    IdempotencyCompleted,
    BeforeCommit,
}

public interface IAssignmentFailureInjector
{
    Task OnStageAsync(AssignmentTransactionStage stage, CancellationToken cancellationToken);
}

public sealed class NoOpAssignmentFailureInjector : IAssignmentFailureInjector
{
    public Task OnStageAsync(AssignmentTransactionStage stage, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
