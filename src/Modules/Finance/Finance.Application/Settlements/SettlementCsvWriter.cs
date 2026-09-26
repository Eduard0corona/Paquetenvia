using System.Globalization;
using System.Text;

namespace Finance.Application.Settlements;

/// <summary>
/// The exportable settlement: one record per persisted line, each repeating its settlement header, so the
/// file reconciles on its own — the amount_cents column sums to settlement_total_cents. The bytes depend
/// only on the persisted settlement: RFC 4180 records ending in CRLF, UTF-8 without a byte order mark,
/// invariant integer cents, UTC timestamps with microsecond precision, and lines in (created_at, id) order.
/// No column carries personal data or free text.
/// </summary>
public static class SettlementCsvWriter
{
    public const string ContentType = "text/csv; charset=utf-8";

    public static IReadOnlyList<string> Columns { get; } =
    [
        "settlement_id",
        "payee_type",
        "payee_id",
        "period_from",
        "period_to",
        "settlement_status",
        "settlement_total_cents",
        "line_id",
        "line_type",
        "order_id",
        "amount_cents",
        "source_reference",
        "line_created_at",
    ];

    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static string FileName(Guid settlementId) =>
        $"settlement-{settlementId.ToString("D", CultureInfo.InvariantCulture)}.csv";

    public static SettlementCsvDocument Write(SettlementResult settlement)
    {
        ArgumentNullException.ThrowIfNull(settlement);
        var builder = new StringBuilder();
        AppendRecord(builder, Columns);
        // Canonical UUID text sorts ordinally exactly as PostgreSQL sorts uuid values, so this is the
        // order the ledger is read in.
        foreach (var line in settlement.Lines
                     .OrderBy(line => line.CreatedAt)
                     .ThenBy(line => Uuid(line.Id), StringComparer.Ordinal))
        {
            AppendRecord(builder,
            [
                Uuid(settlement.Id),
                settlement.PayeeType,
                Uuid(settlement.PayeeId),
                Date(settlement.PeriodFrom),
                Date(settlement.PeriodTo),
                settlement.Status,
                Integer(settlement.TotalCents),
                Uuid(line.Id),
                line.LineType,
                line.OrderId is { } orderId ? Uuid(orderId) : string.Empty,
                Integer(line.AmountCents),
                line.SourceReference,
                Timestamp(line.CreatedAt),
            ]);
        }

        return new(FileName(settlement.Id), Utf8WithoutBom.GetBytes(builder.ToString()));
    }

    /// <summary>
    /// RFC 4180 escaping: a field containing a comma, a double quote, CR or LF is enclosed in double quotes
    /// and its double quotes are doubled; every other field is written as is.
    /// </summary>
    public static string Escape(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.AsSpan().IndexOfAny(",\"\r\n") < 0
            ? value
            : "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private static void AppendRecord(StringBuilder builder, IReadOnlyList<string> fields)
    {
        for (var index = 0; index < fields.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(',');
            }

            builder.Append(Escape(fields[index]));
        }

        builder.Append("\r\n");
    }

    private static string Uuid(Guid value) => value.ToString("D", CultureInfo.InvariantCulture);

    private static string Date(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Integer(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Timestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);
}
