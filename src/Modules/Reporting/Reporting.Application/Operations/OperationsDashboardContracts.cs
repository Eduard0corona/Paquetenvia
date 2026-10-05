using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Paqueteria.Domain.Tenancy;

namespace Reporting.Application.Operations;

public static class OperationsDashboardVocabulary
{
    public static readonly ImmutableArray<string> Statuses =
    [
        "DRAFT",
        "CONFIRMED",
        "READY_FOR_PICKUP",
        "ASSIGNED",
        "AT_PICKUP",
        "PICKED_UP",
        "IN_TRANSIT",
        "DELIVERING",
        "FAILED_ATTEMPT",
        "RESCHEDULED",
        "RETURNING",
        "RETURNED",
        "DELIVERED",
        "CLOSED",
        "CLAIM_OPEN",
        "CLAIM_RESOLVED",
        "CANCELLED",
    ];

    public static readonly ImmutableArray<string> ServiceTypes =
        ["SAME_DAY", "URGENT", "SCHEDULED_ROUTE"];

    public static bool IsStatus(string? value) =>
        value is not null && Statuses.Contains(value, StringComparer.Ordinal);

    public static bool IsServiceType(string? value) =>
        value is not null && ServiceTypes.Contains(value, StringComparer.Ordinal);
}

public sealed record OperationsDashboardFilters(
    Guid? OrderId,
    string? Status,
    Guid? DeliveryZoneId,
    Guid? ClientAccountId,
    Guid? OwnerOrganizationId,
    Guid? OperatorOrganizationId,
    string? ServiceType,
    DateTimeOffset? CreatedFrom,
    DateTimeOffset? CreatedTo,
    bool? Unassigned,
    OperationsDashboardCursor? Cursor);

public sealed record OperationsDashboardRequest(
    Guid ActorId,
    Guid OrganizationId,
    bool MfaSatisfied,
    OperationsDashboardFilters Filters);

public sealed record OperationsDashboardCursor(
    int Version,
    DateTimeOffset UpdatedAt,
    Guid OrderId);

public sealed record OperationsOrganizationSummary(Guid OrganizationId, string DisplayName);

public sealed record OperationsClientSummary(Guid ClientAccountId, string DisplayName);

public sealed record OperationsZoneSummary(Guid OperatingZoneId, string Name, string ZoneType);

public sealed record OperationsAssignmentSummary(
    Guid AssignmentId,
    string AssignmentType,
    string Status,
    Guid DriverId,
    string DriverReference);

public sealed record OperationsDriverLocation(
    double Latitude,
    double Longitude,
    double AccuracyMeters,
    DateTimeOffset CapturedAt);

/// <summary>A time window as UTC instants (OBS-001 pickup_window/delivery_window).</summary>
public sealed record OperationsTimeWindow(DateTimeOffset From, DateTimeOffset To);

public sealed record OperationsDashboardOrder(
    Guid OrderId,
    int AggregateVersion,
    string PublicId,
    OperationsOrganizationSummary Owner,
    OperationsOrganizationSummary? Operator,
    OperationsClientSummary? Client,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string ServiceType,
    OperationsZoneSummary? DeliveryZone,
    OperationsAssignmentSummary? Assignment,
    OperationsDriverLocation? LatestDriverLocation,
    string? CostWarning,
    bool UnassignedAlert,
    OperationsTimeWindow? DeliveryWindow = null);

public sealed record OperationsDashboardPage(
    DateTimeOffset GeneratedAt,
    IReadOnlyList<OperationsDashboardOrder> Items,
    string? NextCursor)
{
    public IReadOnlyList<OperationsDashboardOrder> Items { get; } = Items.ToImmutableArray();
}

public interface IOperationsDashboardReader
{
    Task<OperationsDashboardPage> ReadAsync(
        OperationsDashboardRequest request,
        CancellationToken cancellationToken);
}

public interface IOperationsDashboardTelemetry
{
    IDisposable MeasureLookup();
    void LookupCompleted();
    void LookupFailed(string category);
    void AuthorizationRejected();
    void ContractInvalid(string category);
    void ActiveAssignmentInconsistent();
}

public sealed class OperationsDashboardForbiddenException : Exception;

public sealed class OperationsDashboardUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public sealed class OperationsDashboardContractException(string message)
    : Exception(message);

public static class OperationsDashboardAuthorizationPolicy
{
    public static bool IsAllowed(OrganizationRole role, bool mfaSatisfied) =>
        OperationsRolePolicy.IsAllowed(role, mfaSatisfied);
}

public static class OperationsDashboardProjectionPolicy
{
    public static bool IsUnassignedAlert(string status, bool hasActiveAssignment) =>
        status is "READY_FOR_PICKUP" or "RESCHEDULED" && !hasActiveAssignment;

    /// <summary>The shared non-PII driver label; the assignable-driver list uses the same one.</summary>
    public static string DriverReference(Guid driverId) =>
        Paqueteria.Application.Privacy.DriverReference.From(driverId);

    public static bool IsValidLocation(
        double latitude,
        double longitude,
        double accuracyMeters,
        DateTimeOffset capturedAt) =>
        double.IsFinite(latitude) &&
        latitude is >= -90 and <= 90 &&
        double.IsFinite(longitude) &&
        longitude is >= -180 and <= 180 &&
        double.IsFinite(accuracyMeters) &&
        accuracyMeters >= 0 &&
        capturedAt.Offset == TimeSpan.Zero;
}

public static class OperationsDashboardCursorCodec
{
    public const int CurrentVersion = 1;
    public const int MaximumLength = 512;

    public static string Encode(DateTimeOffset updatedAt, Guid orderId)
    {
        if (updatedAt.Offset != TimeSpan.Zero || orderId == Guid.Empty)
        {
            throw new ArgumentException("A UTC timestamp and non-empty order id are required.");
        }

        var plain = string.Create(
            CultureInfo.InvariantCulture,
            $"{CurrentVersion}|{updatedAt.UtcTicks}|{orderId:D}");
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(plain))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static bool TryDecode(string? encoded, out OperationsDashboardCursor? cursor)
    {
        cursor = null;
        if (string.IsNullOrWhiteSpace(encoded) ||
            encoded.Length > MaximumLength ||
            encoded.Contains('=', StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var base64 = encoded.Replace('-', '+').Replace('_', '/');
            base64 += (base64.Length % 4) switch
            {
                0 => string.Empty,
                2 => "==",
                3 => "=",
                _ => throw new FormatException(),
            };
            var plain = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
            var parts = plain.Split('|');
            if (parts.Length != 3 ||
                !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var version) ||
                version != CurrentVersion ||
                !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) ||
                ticks < DateTimeOffset.MinValue.UtcTicks ||
                ticks > DateTimeOffset.MaxValue.UtcTicks ||
                !Guid.TryParseExact(parts[2], "D", out var orderId) ||
                orderId == Guid.Empty)
            {
                return false;
            }

            cursor = new OperationsDashboardCursor(
                version,
                new DateTimeOffset(ticks, TimeSpan.Zero),
                orderId);
            return string.Equals(Encode(cursor.UpdatedAt, cursor.OrderId), encoded, StringComparison.Ordinal);
        }
        catch (Exception exception) when (
            exception is FormatException or ArgumentException or DecoderFallbackException)
        {
            return false;
        }
    }
}
