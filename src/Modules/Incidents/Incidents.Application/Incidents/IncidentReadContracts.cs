using System.Buffers.Binary;
using Incidents.Domain;

namespace Incidents.Application.Incidents;

/// <summary>
/// API-INC-LIST-PROOFS-2026-09-29: one page of the incidents the selected organization may see,
/// newest first by <c>created_at</c> then <c>id</c>. <see cref="Status"/> and <see cref="OrderId"/>
/// are optional exact filters; <see cref="Cursor"/> is a position this operation issued.
/// </summary>
public sealed record ListIncidentsQuery(
    Guid ActorId,
    Guid OrganizationId,
    bool MfaSatisfied,
    string? Status,
    Guid? OrderId,
    IncidentCursor? Cursor);

/// <summary>One incident of the selected organization, read by its id.</summary>
public sealed record GetIncidentQuery(
    Guid ActorId,
    Guid OrganizationId,
    bool MfaSatisfied,
    Guid IncidentId);

public sealed record IncidentPage(IReadOnlyList<IncidentResult> Items, string? NextCursor);

/// <summary>
/// The incident reads. They share the resolution capability — DISPATCHER, or PLATFORM_ADMIN with
/// a satisfied MFA challenge — settled before any incident is read, and they never write.
/// </summary>
public interface IIncidentReadService
{
    Task<IncidentPage> ListAsync(ListIncidentsQuery query, CancellationToken cancellationToken);

    Task<IncidentResult> GetAsync(GetIncidentQuery query, CancellationToken cancellationToken);
}

/// <summary>
/// The read side of INC-001. Reading an incident is the supervisory view of the incident desk, so
/// it admits exactly who may resolve one; a DRIVER, a VIEWER and every other role are refused
/// before any incident is read.
/// </summary>
public static class IncidentReadPolicy
{
    /// <summary>The server-owned page size of listIncidents; clients cannot configure it.</summary>
    public const int PageSize = 50;

    public static bool MayRead(bool isActiveDispatcher, bool isActivePlatformAdmin, bool mfaSatisfied) =>
        IncidentResolutionAuthorizationPolicy.MayResolve(isActiveDispatcher, isActivePlatformAdmin, mfaSatisfied);

    public static bool IsValidStatusFilter(string? value) =>
        value is null || IncidentContract.TryParseStatus(value, out _);

    public static bool IsValidShape(ListIncidentsQuery query) =>
        query.ActorId != Guid.Empty &&
        query.OrganizationId != Guid.Empty &&
        IsValidStatusFilter(query.Status) &&
        query.OrderId != Guid.Empty &&
        (query.Cursor is null || query.Cursor.Id != Guid.Empty);

    public static bool IsValidShape(GetIncidentQuery query) =>
        query.ActorId != Guid.Empty &&
        query.OrganizationId != Guid.Empty &&
        query.IncidentId != Guid.Empty;
}

/// <summary>The keyset position after the last incident of a page.</summary>
public sealed record IncidentCursor(DateTimeOffset CreatedAt, Guid Id);

/// <summary>
/// Opaque Base64URL cursor of listIncidents: a tag byte, the UTC ticks of <c>created_at</c> and the id.
/// The tag is this operation's own, so a cursor issued by another list operation never decodes here.
/// </summary>
public static class IncidentCursorCodec
{
    public const int MaximumLength = 128;

    private const byte Tag = 0x49;

    private const int PayloadLength = 1 + sizeof(long) + 16;

    public static string Encode(IncidentCursor cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        var payload = new byte[PayloadLength];
        payload[0] = Tag;
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(1, sizeof(long)), cursor.CreatedAt.UtcTicks);
        cursor.Id.TryWriteBytes(payload.AsSpan(1 + sizeof(long)), bigEndian: true, out _);
        return Base64Url.Encode(payload);
    }

    public static bool TryDecode(string? value, out IncidentCursor? cursor)
    {
        cursor = null;
        if (string.IsNullOrEmpty(value) || value.Length > MaximumLength ||
            !Base64Url.TryDecode(value, out var bytes) ||
            bytes.Length != PayloadLength ||
            bytes[0] != Tag)
        {
            return false;
        }

        var ticks = BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(1, sizeof(long)));
        var id = new Guid(bytes.AsSpan(1 + sizeof(long)), bigEndian: true);
        if (ticks < DateTimeOffset.MinValue.UtcTicks || ticks > DateTimeOffset.MaxValue.UtcTicks || id == Guid.Empty)
        {
            return false;
        }

        cursor = new IncidentCursor(new DateTimeOffset(ticks, TimeSpan.Zero), id);
        // Only the canonical encoding of a position is accepted, so one position has one cursor.
        return string.Equals(Encode(cursor), value, StringComparison.Ordinal);
    }
}

/// <summary>Base64URL without padding, strict on the alphabet.</summary>
internal static class Base64Url
{
    internal static string Encode(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static bool TryDecode(string value, out byte[] bytes)
    {
        bytes = [];
        if (value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')) ||
            value.Length % 4 == 1)
        {
            return false;
        }

        var base64 = value.Replace('-', '+').Replace('_', '/');
        base64 += (base64.Length % 4) switch
        {
            2 => "==",
            3 => "=",
            _ => string.Empty,
        };
        try
        {
            bytes = Convert.FromBase64String(base64);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
