using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Drivers.Application.Eligibility;

namespace Routing.Application.Routes;

public sealed record CreateRouteCommand(
    Guid ActorId,
    Guid OrganizationId,
    string IdempotencyKey,
    Guid DriverId,
    Guid CityId,
    Guid? ServiceAreaId,
    DateOnly? ScheduledFor,
    bool MfaSatisfied,
    string? RequestId);

public sealed record ListRoutesQuery(
    Guid ActorId,
    Guid OrganizationId,
    string? Status,
    Guid? DriverId,
    DateOnly? ScheduledFor,
    string? Cursor);

public sealed record GetRouteQuery(Guid ActorId, Guid OrganizationId, Guid RouteId);

public sealed record AddRouteStopCommand(
    Guid ActorId,
    Guid OrganizationId,
    string IdempotencyKey,
    Guid RouteId,
    Guid OrderId,
    int ExpectedVersion,
    bool MfaSatisfied,
    string? RequestId);

public sealed record RemoveRouteStopCommand(
    Guid ActorId,
    Guid OrganizationId,
    string IdempotencyKey,
    Guid RouteId,
    Guid StopId,
    int ExpectedVersion,
    bool MfaSatisfied,
    string? RequestId);

public sealed record ReorderRouteStopsCommand(
    Guid ActorId,
    Guid OrganizationId,
    string IdempotencyKey,
    Guid RouteId,
    int ExpectedVersion,
    IReadOnlyList<Guid> StopIds,
    bool MfaSatisfied,
    string? RequestId)
{
    public IReadOnlyList<Guid> StopIds { get; } = StopIds.ToArray();
}

public sealed record RouteStopResult(
    Guid Id,
    Guid OrderId,
    int Sequence,
    string StopType,
    string Status);

public sealed record RouteResult(
    Guid Id,
    string Status,
    int Version,
    Guid DriverId,
    Guid CityId,
    Guid? ServiceAreaId,
    DateOnly? ScheduledFor,
    long AssignmentCostCentsTotal,
    int StopCount);

public sealed record RouteDetailResult(
    Guid Id,
    string Status,
    int Version,
    Guid DriverId,
    Guid CityId,
    Guid? ServiceAreaId,
    DateOnly? ScheduledFor,
    long AssignmentCostCentsTotal,
    int StopCount,
    IReadOnlyList<RouteStopResult> Stops)
{
    public IReadOnlyList<RouteStopResult> Stops { get; } = Stops.ToArray();
}

public sealed record RoutePageResult(IReadOnlyList<RouteResult> Items, string? NextCursor)
{
    public IReadOnlyList<RouteResult> Items { get; } = Items.ToArray();
}

public interface IRouteService
{
    Task<RouteResult> CreateAsync(CreateRouteCommand command, CancellationToken cancellationToken);
    Task<RoutePageResult> ListAsync(ListRoutesQuery query, CancellationToken cancellationToken);
    Task<RouteDetailResult> GetAsync(GetRouteQuery query, CancellationToken cancellationToken);
    Task<RouteDetailResult> AddStopAsync(AddRouteStopCommand command, CancellationToken cancellationToken);
    Task<RouteDetailResult> RemoveStopAsync(RemoveRouteStopCommand command, CancellationToken cancellationToken);
    Task<RouteDetailResult> ReorderStopsAsync(ReorderRouteStopsCommand command, CancellationToken cancellationToken);
}

public enum RoutingConflictCode
{
    InvalidRequest,
    IdempotencyConflict,
    InconsistentReplayEvidence,
    DriverIneligible,
    RouteStateConflict,
    VersionConflict,
    AssignmentUnavailable,
    AssignmentDriverMismatch,
    DuplicateOrder,
    OrderAlreadyRouted,
    InvalidStopSet,
    ConcurrencyConflict,
}

public sealed class RoutingConflictException(RoutingConflictCode code, Exception? inner = null)
    : Exception("The routing operation conflicts with current state.", inner)
{
    public RoutingConflictCode Code { get; } = code;
}

public sealed class RoutingForbiddenException : Exception;
public sealed class RoutingNotFoundException : Exception;
public sealed class RoutingUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

public sealed record RoutingAuthorizationContext(
    string? ActiveRole,
    bool UserActive,
    bool MembershipActive,
    bool MfaSatisfied);

public static class RoutingAuthorizationPolicy
{
    public static bool CanRead(RoutingAuthorizationContext context) =>
        context.UserActive && context.MembershipActive &&
        context.ActiveRole is "PLATFORM_ADMIN" or "DISPATCHER";

    public static bool CanMutate(RoutingAuthorizationContext context) =>
        CanRead(context) &&
        (context.ActiveRole != "PLATFORM_ADMIN" || context.MfaSatisfied);
}

public static class RoutingInputPolicy
{
    private static readonly HashSet<string> Statuses =
        ["DRAFT", "PLANNED", "ACTIVE", "COMPLETED", "CANCELLED"];

    public static bool IsValid(CreateRouteCommand value) =>
        value.ActorId != Guid.Empty && value.OrganizationId != Guid.Empty &&
        value.DriverId != Guid.Empty && value.CityId != Guid.Empty &&
        value.ServiceAreaId != Guid.Empty;

    public static bool IsValid(ListRoutesQuery value) =>
        value.ActorId != Guid.Empty && value.OrganizationId != Guid.Empty &&
        (value.Status is null || Statuses.Contains(value.Status)) &&
        value.DriverId != Guid.Empty;

    public static bool IsValid(GetRouteQuery value) =>
        value.ActorId != Guid.Empty && value.OrganizationId != Guid.Empty && value.RouteId != Guid.Empty;

    public static bool IsValid(AddRouteStopCommand value) =>
        value.ActorId != Guid.Empty && value.OrganizationId != Guid.Empty &&
        value.RouteId != Guid.Empty && value.OrderId != Guid.Empty && value.ExpectedVersion >= 1;

    public static bool IsValid(RemoveRouteStopCommand value) =>
        value.ActorId != Guid.Empty && value.OrganizationId != Guid.Empty &&
        value.RouteId != Guid.Empty && value.StopId != Guid.Empty && value.ExpectedVersion >= 1;

    public static bool IsValid(ReorderRouteStopsCommand value) =>
        value.ActorId != Guid.Empty && value.OrganizationId != Guid.Empty &&
        value.RouteId != Guid.Empty && value.ExpectedVersion >= 1 &&
        value.StopIds.All(id => id != Guid.Empty) &&
        value.StopIds.Distinct().Count() == value.StopIds.Count;
}

public static class RoutingCanonicalizer
{
    public static byte[] Create(CreateRouteCommand value) => Hash(writer =>
    {
        Tenant(writer, value.OrganizationId);
        writer.WriteString("driver_id", value.DriverId);
        writer.WriteString("city_id", value.CityId);
        WriteGuidOrNull(writer, "service_area_id", value.ServiceAreaId);
        WriteDateOrNull(writer, "scheduled_for", value.ScheduledFor);
    });

    public static byte[] Add(AddRouteStopCommand value) => Hash(writer =>
    {
        Tenant(writer, value.OrganizationId);
        writer.WriteString("route_id", value.RouteId);
        writer.WriteString("order_id", value.OrderId);
        writer.WriteNumber("expected_version", value.ExpectedVersion);
    });

    public static byte[] Remove(RemoveRouteStopCommand value) => Hash(writer =>
    {
        Tenant(writer, value.OrganizationId);
        writer.WriteString("route_id", value.RouteId);
        writer.WriteString("stop_id", value.StopId);
        writer.WriteNumber("expected_version", value.ExpectedVersion);
    });

    public static byte[] Reorder(ReorderRouteStopsCommand value) => Hash(writer =>
    {
        Tenant(writer, value.OrganizationId);
        writer.WriteString("route_id", value.RouteId);
        writer.WriteNumber("expected_version", value.ExpectedVersion);
        writer.WriteStartArray("stop_ids");
        foreach (var stopId in value.StopIds) writer.WriteStringValue(stopId);
        writer.WriteEndArray();
    });

    private static byte[] Hash(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }
        return SHA256.HashData(stream.ToArray());
    }

    private static void Tenant(Utf8JsonWriter writer, Guid organizationId) =>
        writer.WriteString("tenant", organizationId.ToString("D", CultureInfo.InvariantCulture));

    private static void WriteGuidOrNull(Utf8JsonWriter writer, string name, Guid? value)
    {
        if (value is { } id) writer.WriteString(name, id); else writer.WriteNull(name);
    }

    private static void WriteDateOrNull(Utf8JsonWriter writer, string name, DateOnly? value)
    {
        if (value is { } date) writer.WriteString(name, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        else writer.WriteNull(name);
    }
}

public sealed record RouteCursor(DateTimeOffset CreatedAt, Guid Id);

public static class RouteCursorCodec
{
    public static string Encode(RouteCursor value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(
        string.Create(CultureInfo.InvariantCulture, $"{value.CreatedAt:O}|{value.Id:D}")));

    public static bool TryDecode(string? value, out RouteCursor? cursor)
    {
        cursor = null;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256) return false;
        try
        {
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(value)).Split('|');
            if (parts.Length != 2 ||
                !DateTimeOffset.TryParseExact(parts[0], "O", CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var createdAt) ||
                createdAt.Offset != TimeSpan.Zero ||
                !Guid.TryParseExact(parts[1], "D", out var id) || id == Guid.Empty)
            {
                return false;
            }
            cursor = new(createdAt, id);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

public static class RoutingCapacityAggregator
{
    public static bool TryAggregate(
        IReadOnlyList<(long WeightGrams, int? Length, int? Width, int? Height)> packages,
        out DriverCapacityRequirement? result)
    {
        result = null;
        if (packages.Count == 0) return false;
        try
        {
            long total = 0;
            long maximumWeight = 0;
            int? length = null;
            int? width = null;
            int? height = null;
            foreach (var package in packages)
            {
                if (package.WeightGrams <= 0 || package.Length is <= 0 ||
                    package.Width is <= 0 || package.Height is <= 0) return false;
                total = checked(total + package.WeightGrams);
                maximumWeight = Math.Max(maximumWeight, package.WeightGrams);
                length = Max(length, package.Length);
                width = Max(width, package.Width);
                height = Max(height, package.Height);
            }
            result = new(packages.Count, total, maximumWeight, length, width, height);
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static int? Max(int? left, int? right) =>
        right is null ? left : left is null ? right : Math.Max(left.Value, right.Value);
}

public enum RoutingTransactionStage
{
    RouteInserted,
    StopInserted,
    AssignmentLinked,
    StopRemoved,
    AssignmentUnlinked,
    StopsReordered,
    OutboxInserted,
    AuditInserted,
    BeforeCommit,
}

public interface IRoutingFailureInjector
{
    Task OnStageAsync(RoutingTransactionStage stage, CancellationToken cancellationToken);
}

public sealed class NoOpRoutingFailureInjector : IRoutingFailureInjector
{
    public Task OnStageAsync(RoutingTransactionStage stage, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
