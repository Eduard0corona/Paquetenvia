using System.Globalization;
using System.Text;
using Finance.Application.Settlements;

namespace Paqueteria.UnitTests.Finance;

/// <summary>
/// The settlement CSV is a pure function of the persisted settlement: the same settlement always
/// yields the same bytes, whatever the host culture or the order its lines were handed over in.
/// </summary>
public sealed class SettlementCsvWriterTests
{
    private const string Header =
        "settlement_id,payee_type,payee_id,period_from,period_to,settlement_status,settlement_total_cents," +
        "line_id,line_type,order_id,amount_cents,source_reference,line_created_at\r\n";

    private static readonly Guid SettlementId = Guid.Parse("11111111-2222-4333-8444-555555555555");
    private static readonly Guid DriverId = Guid.Parse("66666666-7777-4888-9999-aaaaaaaaaaaa");
    private static readonly Guid OrderA = Guid.Parse("0a0a0a0a-0000-4000-8000-000000000001");
    private static readonly Guid OrderB = Guid.Parse("0b0b0b0b-0000-4000-8000-000000000002");
    private static readonly DateTimeOffset Calculated = new(2026, 9, 22, 17, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_export_is_exactly_the_documented_bytes()
    {
        var document = SettlementCsvWriter.Write(Settlement(
            "APPROVED",
            14_250,
            Line("cccccccc-0000-4000-8000-000000000003", "ADJUSTMENT", null, -750,
                "platform.audit_logs/dddddddd-0000-4000-8000-000000000004", Calculated.AddHours(1)),
            Line("bbbbbbbb-0000-4000-8000-000000000002", "RETURN", OrderB, 3_000,
                "dispatch.assignments/2b2b2b2b-0000-4000-8000-000000000002", Calculated),
            Line("aaaaaaaa-0000-4000-8000-000000000001", "DELIVERY", OrderA, 12_000,
                "dispatch.assignments/1a1a1a1a-0000-4000-8000-000000000001", Calculated)));

        Assert.Equal("settlement-11111111-2222-4333-8444-555555555555.csv", document.FileName);
        Assert.Equal(
            Header +
            "11111111-2222-4333-8444-555555555555,DRIVER,66666666-7777-4888-9999-aaaaaaaaaaaa,2026-09-14,2026-09-20,APPROVED,14250," +
            "aaaaaaaa-0000-4000-8000-000000000001,DELIVERY,0a0a0a0a-0000-4000-8000-000000000001,12000," +
            "dispatch.assignments/1a1a1a1a-0000-4000-8000-000000000001,2026-09-22T17:00:00.000000Z\r\n" +
            "11111111-2222-4333-8444-555555555555,DRIVER,66666666-7777-4888-9999-aaaaaaaaaaaa,2026-09-14,2026-09-20,APPROVED,14250," +
            "bbbbbbbb-0000-4000-8000-000000000002,RETURN,0b0b0b0b-0000-4000-8000-000000000002,3000," +
            "dispatch.assignments/2b2b2b2b-0000-4000-8000-000000000002,2026-09-22T17:00:00.000000Z\r\n" +
            "11111111-2222-4333-8444-555555555555,DRIVER,66666666-7777-4888-9999-aaaaaaaaaaaa,2026-09-14,2026-09-20,APPROVED,14250," +
            "cccccccc-0000-4000-8000-000000000003,ADJUSTMENT,,-750," +
            "platform.audit_logs/dddddddd-0000-4000-8000-000000000004,2026-09-22T18:00:00.000000Z\r\n",
            Encoding.UTF8.GetString(document.Content));
    }

    [Fact]
    public void The_bytes_are_UTF8_without_a_byte_order_mark_and_identical_on_every_render()
    {
        var settlement = Settlement("CALCULATED", 12_000, Delivery("aaaaaaaa-0000-4000-8000-000000000001", 12_000));
        var first = SettlementCsvWriter.Write(settlement).Content;
        var second = SettlementCsvWriter.Write(settlement with { }).Content;

        Assert.Equal(first, second);
        Assert.Equal((byte)'s', first[0]);
        Assert.Equal("text/csv; charset=utf-8", SettlementCsvWriter.ContentType);
    }

    [Fact]
    public void Lines_are_ordered_by_creation_then_id_whatever_order_they_arrive_in()
    {
        var early = Line("ffffffff-0000-4000-8000-000000000009", "DELIVERY", OrderA, 1, "dispatch.assignments/e1", Calculated.AddMinutes(-1));
        var sameTimeLowId = Line("0fffffff-0000-4000-8000-000000000001", "DELIVERY", OrderA, 2, "dispatch.assignments/e2", Calculated);
        var sameTimeHighId = Line("f0000000-0000-4000-8000-000000000001", "RETURN", OrderB, 3, "dispatch.assignments/e3", Calculated);

        var forward = SettlementCsvWriter.Write(Settlement("CALCULATED", 6, early, sameTimeLowId, sameTimeHighId));
        var reversed = SettlementCsvWriter.Write(Settlement("CALCULATED", 6, sameTimeHighId, sameTimeLowId, early));

        Assert.Equal(forward.Content, reversed.Content);
        Assert.Equal(
            ["ffffffff-0000-4000-8000-000000000009", "0fffffff-0000-4000-8000-000000000001", "f0000000-0000-4000-8000-000000000001"],
            Records(forward).Skip(1).Select(record => record[7]));
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("-750", "-750")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("line\nbreak", "\"line\nbreak\"")]
    [InlineData("line\r\nbreak", "\"line\r\nbreak\"")]
    [InlineData("", "")]
    public void Fields_are_escaped_as_RFC_4180_prescribes(string value, string expected) =>
        Assert.Equal(expected, SettlementCsvWriter.Escape(value));

    [Fact]
    public void A_source_reference_that_needs_quoting_is_quoted_and_nothing_else_moves()
    {
        var document = SettlementCsvWriter.Write(Settlement(
            "CALCULATED",
            1,
            Line("aaaaaaaa-0000-4000-8000-000000000001", "DELIVERY", OrderA, 1, "dispatch.assignments/\"x,y\"", Calculated)));

        var text = Encoding.UTF8.GetString(document.Content);
        Assert.Contains(",1,\"dispatch.assignments/\"\"x,y\"\"\",2026-09-22T17:00:00.000000Z\r\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Money_is_invariant_integer_cents_whatever_the_host_culture()
    {
        var settlement = Settlement(
            "PAID",
            long.MaxValue,
            Delivery("aaaaaaaa-0000-4000-8000-000000000001", long.MaxValue - 1),
            Line("bbbbbbbb-0000-4000-8000-000000000002", "ADJUSTMENT", null, 1, "platform.audit_logs/a", Calculated));
        var invariant = SettlementCsvWriter.Write(settlement).Content;

        var original = CultureInfo.CurrentCulture;
        try
        {
            foreach (var culture in new[] { "es-MX", "de-DE", "ar-SA", "fr-CH" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                Assert.Equal(invariant, SettlementCsvWriter.Write(settlement).Content);
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }

        var records = Records(SettlementCsvWriter.Write(settlement)).Skip(1).ToArray();
        Assert.Equal(["9223372036854775806", "1"], records.Select(record => record[10]));
        Assert.All(records, record => Assert.Equal("9223372036854775807", record[6]));
    }

    [Fact]
    public void The_line_amounts_sum_to_the_settlement_total()
    {
        var document = SettlementCsvWriter.Write(Settlement(
            "CALCULATED",
            14_250,
            Delivery("aaaaaaaa-0000-4000-8000-000000000001", 12_000),
            Line("bbbbbbbb-0000-4000-8000-000000000002", "RETURN", OrderB, 3_000, "dispatch.assignments/r", Calculated),
            Line("cccccccc-0000-4000-8000-000000000003", "ADJUSTMENT", null, -750, "platform.audit_logs/a", Calculated)));

        var records = Records(document).Skip(1).ToArray();
        Assert.Equal(
            records.Select(record => long.Parse(record[6], CultureInfo.InvariantCulture)).Distinct().Single(),
            records.Sum(record => long.Parse(record[10], CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void An_empty_settlement_exports_only_the_header()
    {
        var document = SettlementCsvWriter.Write(Settlement("CALCULATED", 0));
        Assert.Equal(Header, Encoding.UTF8.GetString(document.Content));
        Assert.Equal(13, SettlementCsvWriter.Columns.Count);
    }

    /// <summary>Splits the export back into fields; only used on exports whose fields need no quoting.</summary>
    private static IEnumerable<string[]> Records(SettlementCsvDocument document) =>
        Encoding.UTF8.GetString(document.Content)
            .Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(record => record.Split(','));

    private static SettlementResult Settlement(string status, long total, params SettlementLineResult[] lines) => new(
        SettlementId,
        "DRIVER",
        DriverId,
        status,
        total,
        new DateOnly(2026, 9, 14),
        new DateOnly(2026, 9, 20),
        Calculated,
        lines);

    private static SettlementLineResult Delivery(string id, long amount) =>
        Line(id, "DELIVERY", OrderA, amount, "dispatch.assignments/" + id, Calculated);

    private static SettlementLineResult Line(
        string id,
        string lineType,
        Guid? orderId,
        long amount,
        string source,
        DateTimeOffset createdAt) =>
        new(Guid.Parse(id), lineType, orderId, amount, source, createdAt);
}
