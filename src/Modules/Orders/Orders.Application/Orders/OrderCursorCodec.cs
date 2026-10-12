using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Orders.Application.Orders;

/// <summary>
/// The listOrders keyset position (<c>created_at</c>, <c>id</c>), opaque Base64URL without PII.
/// ORD-LIST-DATE-FILTER-2026-10-12: a cursor is bound to the selected organization and to the filters of the request
/// that received it (status, owner_org_id, cod_pending_reconciliation, public_id, created_from and created_to), so a
/// cursor issued for other filters, another organization or by an earlier version of the operation never decodes.
/// The binding is a truncated SHA-256 of those values: it keeps a client from mixing pages, it authenticates nothing
/// (RLS still scopes every read).
/// </summary>
public static class OrderCursorCodec
{
    /// <summary>Longest cursor accepted; every cursor this operation issues is shorter.</summary>
    public const int MaximumLength = 128;

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

    public static string Encode(DateTimeOffset createdAt, Guid id, string binding)
    {
        var value = string.Create(
            CultureInfo.InvariantCulture,
            $"{Version}:{createdAt.UtcTicks}:{id:D}:{binding}");
        return ToBase64Url(Encoding.UTF8.GetBytes(value));
    }

    /// <summary>
    /// True only for a cursor this codec issued with exactly <paramref name="binding"/>: canonical Base64URL of the
    /// current version, a valid instant and id, and the same binding. Anything else, including a cursor of an earlier
    /// version, is not a position of this request.
    /// </summary>
    public static bool TryDecode(string? cursor, string binding, out DateTimeOffset createdAt, out Guid id)
    {
        createdAt = default;
        id = default;
        if (string.IsNullOrEmpty(cursor) || cursor.Length > MaximumLength)
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
                !string.Equals(parts[3], binding, StringComparison.Ordinal))
            {
                return false;
            }

            var parsedAt = new DateTimeOffset(ticks, TimeSpan.Zero);
            if (!string.Equals(Encode(parsedAt, parsedId, binding), cursor, StringComparison.Ordinal))
            {
                return false;
            }

            createdAt = parsedAt;
            id = parsedId;
            return true;
        }
        catch (Exception exception) when (exception is FormatException or DecoderFallbackException)
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
