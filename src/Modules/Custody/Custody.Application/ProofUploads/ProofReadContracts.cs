using System.Buffers.Binary;

namespace Custody.Application.ProofUploads;

/// <summary>
/// API-INC-LIST-PROOFS-2026-09-29: one page of the POD-001 proofs of one order, newest first by
/// <c>created_at</c> then <c>id</c>. Only metadata is read: never the object, its storage key, a
/// signed URL, the capture point or the protected recipient name.
/// </summary>
public sealed record ListOrderProofsQuery(
    Guid ActorId,
    Guid OrganizationId,
    bool MfaSatisfied,
    Guid OrderId,
    ProofCursor? Cursor);

/// <summary>The published Proof representation: id, proof_type, lowercase hex sha256 and captured_at.</summary>
public sealed record ProofSummary(Guid Id, string ProofType, string Sha256, DateTimeOffset CapturedAt);

public sealed record ProofPage(IReadOnlyList<ProofSummary> Items, string? NextCursor);

public interface IProofReadService
{
    Task<ProofPage> ListOrderProofsAsync(ListOrderProofsQuery query, CancellationToken cancellationToken);
}

/// <summary>
/// Reading the proofs of an order is part of the supervisory incident desk, so it admits exactly who
/// may resolve an incident: an active DISPATCHER, or an active PLATFORM_ADMIN with a satisfied MFA
/// challenge. A DRIVER, a VIEWER and every other role are refused before any order or proof is read.
/// </summary>
public static class ProofReadPolicy
{
    /// <summary>The server-owned page size of listOrderProofs; clients cannot configure it.</summary>
    public const int PageSize = 50;

    public static bool MayRead(bool isActiveDispatcher, bool isActivePlatformAdmin, bool mfaSatisfied) =>
        isActiveDispatcher || (isActivePlatformAdmin && mfaSatisfied);

    public static bool IsValidShape(ListOrderProofsQuery query) =>
        query.ActorId != Guid.Empty &&
        query.OrganizationId != Guid.Empty &&
        query.OrderId != Guid.Empty &&
        (query.Cursor is null || (query.Cursor.OrderId == query.OrderId && query.Cursor.Id != Guid.Empty));
}

/// <summary>The keyset position after the last proof of a page, bound to the order it was issued for.</summary>
public sealed record ProofCursor(Guid OrderId, DateTimeOffset CreatedAt, Guid Id);

/// <summary>
/// Opaque Base64URL cursor of listOrderProofs: a tag byte, the order it was issued for, the UTC ticks of
/// <c>created_at</c> and the id. It never decodes for another list operation and never pages another order.
/// </summary>
public static class ProofCursorCodec
{
    public const int MaximumLength = 128;

    private const byte Tag = 0x50;

    private const int PayloadLength = 1 + 16 + sizeof(long) + 16;

    public static string Encode(ProofCursor cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        var payload = new byte[PayloadLength];
        payload[0] = Tag;
        cursor.OrderId.TryWriteBytes(payload.AsSpan(1, 16), bigEndian: true, out _);
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(17, sizeof(long)), cursor.CreatedAt.UtcTicks);
        cursor.Id.TryWriteBytes(payload.AsSpan(17 + sizeof(long)), bigEndian: true, out _);
        return ToBase64Url(payload);
    }

    public static bool TryDecode(string? value, Guid orderId, out ProofCursor? cursor)
    {
        cursor = null;
        if (string.IsNullOrEmpty(value) || value.Length > MaximumLength ||
            !TryFromBase64Url(value, out var bytes) ||
            bytes.Length != PayloadLength ||
            bytes[0] != Tag)
        {
            return false;
        }

        var cursorOrder = new Guid(bytes.AsSpan(1, 16), bigEndian: true);
        var ticks = BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(17, sizeof(long)));
        var id = new Guid(bytes.AsSpan(17 + sizeof(long)), bigEndian: true);
        if (cursorOrder != orderId ||
            ticks < DateTimeOffset.MinValue.UtcTicks ||
            ticks > DateTimeOffset.MaxValue.UtcTicks ||
            id == Guid.Empty)
        {
            return false;
        }

        cursor = new ProofCursor(cursorOrder, new DateTimeOffset(ticks, TimeSpan.Zero), id);
        // Only the canonical encoding of a position is accepted, so one position has one cursor.
        return string.Equals(Encode(cursor), value, StringComparison.Ordinal);
    }

    private static string ToBase64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryFromBase64Url(string value, out byte[] bytes)
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
