using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Dispatch.Application.ExternalOffers;

public sealed record ExternalOfferConstraints(
    IReadOnlyList<string> VehicleTypes,
    IReadOnlyList<Guid> ServiceAreaIds,
    bool RequiresCod)
{
    private static readonly HashSet<string> AllowedVehicleTypes =
        ["MOTORCYCLE", "CAR", "VAN", "BICYCLE", "WALKER"];

    public IReadOnlyList<string> VehicleTypes { get; } = VehicleTypes.ToArray();
    public IReadOnlyList<Guid> ServiceAreaIds { get; } = ServiceAreaIds.ToArray();

    public bool IsValid() =>
        VehicleTypes.Count <= AllowedVehicleTypes.Count &&
        VehicleTypes.Distinct(StringComparer.Ordinal).Count() == VehicleTypes.Count &&
        VehicleTypes.All(AllowedVehicleTypes.Contains) &&
        ServiceAreaIds.Count <= 100 &&
        ServiceAreaIds.All(value => value != Guid.Empty) &&
        ServiceAreaIds.Distinct().Count() == ServiceAreaIds.Count;

    public bool Allows(string vehicleType, Guid? serviceAreaId, long codExpectedCents) =>
        (VehicleTypes.Count == 0 || VehicleTypes.Contains(vehicleType, StringComparer.Ordinal)) &&
        (ServiceAreaIds.Count == 0 || serviceAreaId is { } value && ServiceAreaIds.Contains(value)) &&
        (!RequiresCod || codExpectedCents > 0);
}

public sealed record CreateExternalOfferCommand(
    Guid ActorId,
    Guid OrganizationId,
    string IdempotencyKey,
    Guid OrderId,
    long CommissionCents,
    DateTimeOffset ExpiresAt,
    ExternalOfferConstraints Constraints,
    bool MfaSatisfied,
    string? RequestId);

public sealed record AcceptExternalOfferCommand(
    Guid ActorId,
    Guid OrganizationId,
    string IdempotencyKey,
    Guid OfferId,
    string? RequestId);

public sealed record ExternalOfferResult(
    Guid Id,
    Guid OrderId,
    string Status,
    Assignments.MoneyResult Commission,
    DateTimeOffset ExpiresAt,
    Guid? AcceptedByDriverId,
    DateTimeOffset? AcceptedAt,
    int Version);

public sealed record ExternalOfferPageResult(
    IReadOnlyList<ExternalOfferResult> Items,
    string? NextCursor)
{
    public IReadOnlyList<ExternalOfferResult> Items { get; } = Items.ToArray();
}

public interface IExternalOfferService
{
    Task<ExternalOfferResult> CreateAsync(
        CreateExternalOfferCommand command,
        CancellationToken cancellationToken);

    Task<ExternalOfferPageResult> ListEligibleAsync(
        Guid actorId,
        Guid organizationId,
        string? cursor,
        CancellationToken cancellationToken);

    Task<Assignments.AssignmentResult> AcceptAsync(
        AcceptExternalOfferCommand command,
        CancellationToken cancellationToken);
}

public enum ExternalOfferConflictCode
{
    InvalidRequest,
    OrderUnavailable,
    InvalidOrderState,
    OpenOfferExists,
    OfferUnavailable,
    OfferExpired,
    DriverIneligible,
    ActiveAssignmentExists,
    IdempotencyConflict,
    InconsistentReplayEvidence,
    ConcurrencyConflict,
}

public sealed class ExternalOfferConflictException(
    ExternalOfferConflictCode code,
    Exception? innerException = null)
    : Exception("The external offer conflicts with current state.", innerException)
{
    public ExternalOfferConflictCode Code { get; } = code;
}

public sealed class ExternalOfferForbiddenException : Exception
{
}

public sealed class ExternalOfferInfrastructureException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public static class ExternalOfferInputPolicy
{
    public static bool IsValid(CreateExternalOfferCommand command) =>
        command.ActorId != Guid.Empty &&
        command.OrganizationId != Guid.Empty &&
        command.OrderId != Guid.Empty &&
        command.CommissionCents >= 0 &&
        command.ExpiresAt.Offset == TimeSpan.Zero &&
        command.Constraints.IsValid();

    public static bool IsValid(AcceptExternalOfferCommand command) =>
        command.ActorId != Guid.Empty &&
        command.OrganizationId != Guid.Empty &&
        command.OfferId != Guid.Empty;
}

public static class ExternalOfferCanonicalizer
{
    public static byte[] ComputeSha256(CreateExternalOfferCommand command)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("tenant", command.OrganizationId.ToString("D", CultureInfo.InvariantCulture));
            writer.WriteString("order_id", command.OrderId.ToString("D", CultureInfo.InvariantCulture));
            writer.WriteNumber("commission_cents", command.CommissionCents);
            writer.WriteString("expires_at", command.ExpiresAt);
            writer.WriteStartArray("vehicle_types");
            foreach (var value in command.Constraints.VehicleTypes.Order(StringComparer.Ordinal))
            {
                writer.WriteStringValue(value);
            }
            writer.WriteEndArray();
            writer.WriteStartArray("service_area_ids");
            foreach (var value in command.Constraints.ServiceAreaIds.Order())
            {
                writer.WriteStringValue(value.ToString("D", CultureInfo.InvariantCulture));
            }
            writer.WriteEndArray();
            writer.WriteBoolean("requires_cod", command.Constraints.RequiresCod);
            writer.WriteEndObject();
        }
        return SHA256.HashData(stream.ToArray());
    }

    public static byte[] ComputeSha256(AcceptExternalOfferCommand command, Guid driverId) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(string.Create(
            CultureInfo.InvariantCulture,
            $"{command.OrganizationId:D}|{command.OfferId:D}|{driverId:D}")));
}

public static class ExternalOfferCursorCodec
{
    public static string Encode(DateTimeOffset createdAt, Guid id)
    {
        var text = string.Create(CultureInfo.InvariantCulture, $"{createdAt.UtcTicks}:{id:D}");
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(text))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static bool TryDecode(string? cursor, out DateTimeOffset createdAt, out Guid id)
    {
        createdAt = default;
        id = default;
        if (string.IsNullOrWhiteSpace(cursor)) return false;
        try
        {
            var base64 = cursor.Replace('-', '+').Replace('_', '/');
            base64 += (base64.Length % 4) switch { 2 => "==", 3 => "=", 0 => "", _ => throw new FormatException() };
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
            var separator = text.IndexOf(':', StringComparison.Ordinal);
            var parsed = separator > 0 &&
                long.TryParse(text.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) &&
                Guid.TryParseExact(text.AsSpan(separator + 1), "D", out id) &&
                ticks >= DateTimeOffset.MinValue.UtcTicks && ticks <= DateTimeOffset.MaxValue.UtcTicks &&
                (createdAt = new DateTimeOffset(ticks, TimeSpan.Zero)) != default;
            if (!parsed || !string.Equals(Encode(createdAt, id), cursor, StringComparison.Ordinal))
            {
                createdAt = default;
                id = default;
                return false;
            }
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
