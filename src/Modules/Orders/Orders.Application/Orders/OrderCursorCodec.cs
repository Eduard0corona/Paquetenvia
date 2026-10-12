using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Orders.Application.Orders;

/// <summary>What a listOrders cursor turned out to be (ORD-LIST-DATE-FILTER-2026-10-12).</summary>
public enum OrderCursorReading
{
    /// <summary>A position: a bound cursor of this request, or an unbound one of the earlier format, paged as before.</summary>
    Position,

    /// <summary>Not a cursor at all; as before, the list answers the empty page.</summary>
    Unreadable,

    /// <summary>A well-formed bound cursor issued for another organization or other filters: 409 INVALID_REQUEST.</summary>
    BoundToOtherFilters,
}

/// <summary>
/// The listOrders keyset position (<c>created_at</c>, <c>id</c>), opaque Base64URL without PII.
/// ORD-LIST-DATE-FILTER-2026-10-12, purely additive: every cursor the server issues now is bound (version <c>o2</c>) to
/// the selected organization and to the filters of the request that received it (status, owner_org_id,
/// cod_pending_reconciliation, public_id, created_from and created_to), so a bound cursor sent with other filters is
/// refused instead of paging another list. Cursors of the earlier format (<c>ticks:id</c>, unbound) keep decoding
/// exactly as before, and any other text keeps reading as no position. The binding is a truncated SHA-256 of those
/// values: it keeps a client from mixing pages, it authenticates nothing (RLS still scopes every read).
/// </summary>
public static class OrderCursorCodec
{
    /// <summary>Longest bound cursor; every one this operation issues is shorter.</summary>
    public const int MaximumBoundLength = 128;

    private const string Version = "o2";
    private const int BindingBytes = 16;

    /// <summary>The binding of a listOrders request: its organization and its filters as the service reads them.</summary>
    public static string Binding(
        Guid organizationId,
        string? status,
        Guid? ownerOrganizationId,
        bool codPendingReconciliation,
        string? publicId,
        OrderListCreatedRange createdRange)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("org", organizationId.ToString("D"));
            WriteNullable(writer, "status", status);
            WriteNullable(writer, "owner_org_id", ownerOrganizationId?.ToString("D"));
            writer.WriteBoolean("cod_pending_reconciliation", codPendingReconciliation);
            WriteNullable(writer, "public_id", publicId);
            WriteTicks(writer, "created_from", createdRange.From);
            WriteTicks(writer, "created_to", createdRange.To);
            writer.WriteEndObject();
        }

        return ToBase64Url(SHA256.HashData(stream.ToArray()).AsSpan(0, BindingBytes).ToArray());
    }

    /// <summary>The bound cursor the server issues for the last row of a page.</summary>
    public static string Encode(DateTimeOffset createdAt, Guid id, string binding)
    {
        var value = string.Create(
            CultureInfo.InvariantCulture,
            $"{Version}:{createdAt.UtcTicks}:{id:D}:{binding}");
        return ToBase64Url(Encoding.UTF8.GetBytes(value));
    }

    /// <summary>
    /// Reads a cursor sent with a request whose binding is <paramref name="binding"/>. A canonical bound cursor with that
    /// binding, or a cursor of the earlier unbound format decoded exactly as before, is a
    /// <see cref="OrderCursorReading.Position"/>; a canonical bound cursor with another binding is
    /// <see cref="OrderCursorReading.BoundToOtherFilters"/>; anything else is <see cref="OrderCursorReading.Unreadable"/>.
    /// </summary>
    public static OrderCursorReading Read(string? cursor, string binding, out DateTimeOffset createdAt, out Guid id)
    {
        if (TryReadBound(cursor, out var boundAt, out var boundId, out var cursorBinding))
        {
            if (!string.Equals(cursorBinding, binding, StringComparison.Ordinal))
            {
                createdAt = default;
                id = default;
                return OrderCursorReading.BoundToOtherFilters;
            }

            createdAt = boundAt;
            id = boundId;
            return OrderCursorReading.Position;
        }

        return TryDecodeUnbound(cursor, out createdAt, out id)
            ? OrderCursorReading.Position
            : OrderCursorReading.Unreadable;
    }

    /// <summary>A canonical bound cursor (<c>o2:ticks:id:binding</c>) of at most <see cref="MaximumBoundLength"/>.</summary>
    private static bool TryReadBound(
        string? cursor,
        out DateTimeOffset createdAt,
        out Guid id,
        out string binding)
    {
        createdAt = default;
        id = default;
        binding = string.Empty;
        if (string.IsNullOrEmpty(cursor) || cursor.Length > MaximumBoundLength)
        {
            return false;
        }

        try
        {
            var parts = Encoding.UTF8.GetString(FromBase64Url(cursor)).Split(':');
            if (parts.Length != 4 ||
                !string.Equals(parts[0], Version, StringComparison.Ordinal) ||
                !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) ||
                ticks < DateTimeOffset.MinValue.UtcTicks ||
                ticks > DateTimeOffset.MaxValue.UtcTicks ||
                !Guid.TryParseExact(parts[2], "D", out var parsedId) ||
                parsedId == Guid.Empty ||
                parts[3].Length != 22)
            {
                return false;
            }

            var parsedAt = new DateTimeOffset(ticks, TimeSpan.Zero);
            if (!string.Equals(Encode(parsedAt, parsedId, parts[3]), cursor, StringComparison.Ordinal))
            {
                return false;
            }

            createdAt = parsedAt;
            id = parsedId;
            binding = parts[3];
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>The earlier unbound format (<c>ticks:id</c> in Base64URL), decoded exactly as before this change.</summary>
    private static bool TryDecodeUnbound(string? cursor, out DateTimeOffset createdAt, out Guid id)
    {
        createdAt = default;
        id = default;
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return false;
        }

        try
        {
            var text = Encoding.UTF8.GetString(FromBase64Url(cursor));
            var separator = text.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0 ||
                !long.TryParse(text.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) ||
                !Guid.TryParseExact(text.AsSpan(separator + 1), "D", out id) ||
                ticks < DateTimeOffset.MinValue.UtcTicks ||
                ticks > DateTimeOffset.MaxValue.UtcTicks)
            {
                return false;
            }

            createdAt = new DateTimeOffset(ticks, TimeSpan.Zero);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static void WriteNullable(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }

    private static void WriteTicks(Utf8JsonWriter writer, string name, DateTimeOffset? value)
    {
        if (value is { } instant)
        {
            writer.WriteNumber(name, instant.UtcTicks);
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    private static string ToBase64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        base64 += (base64.Length % 4) switch
        {
            2 => "==",
            3 => "=",
            0 => string.Empty,
            _ => throw new FormatException("Invalid Base64URL."),
        };
        return Convert.FromBase64String(base64);
    }
}
