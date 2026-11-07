using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;

namespace Paqueteria.IntegrationTests.Finance;

/// <summary>
/// SET-001-D7-RULES-ENFORCED-2026-11-06 through the real API and a real PostgreSQL store: the two D7-SETTLEMENT-RULES
/// refusals as the published 409 SettlementConflict codes, and the audit entry every CSV export writes
/// (D7-SETTLEMENT-EXPORT-AUDIT) with the request's trace id and no personal data.
/// </summary>
public sealed partial class SettlementHttpTests
{
    private static readonly TimeZoneInfo Culiacan = TimeZoneInfo.FindSystemTimeZoneById("America/Mazatlan");

    [Fact]
    public async Task A_period_that_has_not_ended_before_today_in_Culiacan_is_409_SETTLEMENT_PERIOD_NOT_CLOSED()
    {
        var driver = await fixture.SeedDriverAsync(SettlementHttpFixture.TenantId);
        await fixture.SeedWorkAsync(driver, "DELIVERED", "DELIVERED", InPeriod, 4_500);

        foreach (var daysAfterToday in new[] { 0, 1 })
        {
            var today = TodayInCuliacan();
            using var response = await client.SendAsync(
                Post("/api/v1/settlements", WeekEndingBody(driver, today.AddDays(daysAfterToday))));

            // A period ending today becomes closed if Culiacán's midnight passed while the request ran.
            if (daysAfterToday == 0 && TodayInCuliacan() != today)
            {
                continue;
            }

            var body = await AssertConflictAsync(response, "SETTLEMENT_PERIOD_NOT_CLOSED");
            Assert.DoesNotContain(driver.ToString("D"), body, StringComparison.Ordinal);
        }

        Assert.Equal(0, await fixture.CountSettlementsForAsync(driver));

        // A period that ended yesterday is closed; the work of 16 Sep is not in it, so it is the empty settlement.
        using var closed = await client.SendAsync(
            Post("/api/v1/settlements", WeekEndingBody(driver, TodayInCuliacan().AddDays(-1))));
        Assert.Equal(HttpStatusCode.Created, closed.StatusCode);
        using var document = JsonDocument.Parse(await closed.Content.ReadAsStringAsync());
        Assert.Equal(0, document.RootElement.GetProperty("total_cents").GetInt64());
        Assert.Equal(1, await fixture.CountSettlementsForAsync(driver));
    }

    [Fact]
    public async Task An_adjustment_that_would_leave_the_total_below_zero_is_409_SETTLEMENT_TOTAL_NEGATIVE()
    {
        var driver = await fixture.SeedDriverAsync(SettlementHttpFixture.TenantId);
        await fixture.SeedWorkAsync(driver, "DELIVERED", "DELIVERED", InPeriod, 4_500);
        var settlement = await CreateAsync(driver);
        var before = await fixture.ReadAsync(settlement);

        using (var refused = await client.SendAsync(Post(
                   $"/api/v1/settlements/{settlement:D}/adjustments",
                   """{"amount_cents":-4501,"reason":"Descuento mayor al total"}""")))
        {
            var body = await AssertConflictAsync(refused, "SETTLEMENT_TOTAL_NEGATIVE");
            Assert.DoesNotContain("4500", body, StringComparison.Ordinal);
            Assert.DoesNotContain("CALCULATED", body, StringComparison.Ordinal);
        }

        Assert.Equal(before, await fixture.ReadAsync(settlement));

        // A total of exactly zero is admitted.
        using var zero = await client.SendAsync(Post(
            $"/api/v1/settlements/{settlement:D}/adjustments",
            """{"amount_cents":-4500,"reason":"Descuento total"}"""));
        Assert.Equal(HttpStatusCode.Created, zero.StatusCode);
        using var document = JsonDocument.Parse(await zero.Content.ReadAsStringAsync());
        Assert.Equal(0, document.RootElement.GetProperty("total_cents").GetInt64());
        Assert.Equal(new SettlementSnapshot("CALCULATED", 0, 2, 0m, 2), await fixture.ReadAsync(settlement));
    }

    [Fact]
    public async Task Every_CSV_export_writes_one_audit_entry_with_its_trace_id_and_no_personal_data()
    {
        var driver = await fixture.SeedDriverAsync(SettlementHttpFixture.TenantId);
        await fixture.SeedWorkAsync(driver, "DELIVERED", "DELIVERED", InPeriod, 4_500);
        var settlement = await CreateAsync(driver);
        using (var voided = await client.SendAsync(Post($"/api/v1/settlements/{settlement:D}/void", VoidBody)))
        {
            Assert.Equal(HttpStatusCode.OK, voided.StatusCode);
        }

        // A role outside the settlement matrix exports nothing and leaves no entry.
        using (var forbidden = await client.SendAsync(
                   Get($"/api/v1/settlements/{settlement:D}/export.csv", SettlementHttpFixture.DispatcherProfile)))
        {
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        }

        Assert.Empty(await ExportAuditsAsync(settlement));

        for (var download = 1; download <= 2; download++)
        {
            using var export = await client.SendAsync(Get($"/api/v1/settlements/{settlement:D}/export.csv"));
            Assert.Equal(HttpStatusCode.OK, export.StatusCode);
            Assert.Equal(download, (await ExportAuditsAsync(settlement)).Count);
        }

        var audits = await ExportAuditsAsync(settlement);
        Assert.Equal(2, audits.Select(audit => audit.RequestId).Distinct(StringComparer.Ordinal).Count());
        Assert.All(audits, audit =>
        {
            Assert.Matches(TraceId(), audit.RequestId);
            Assert.Equal(fixture.FinanceMfaUserId, audit.ActorId);
            using var payload = JsonDocument.Parse(audit.Payload);
            Assert.Equal(
                ["line_count", "settlement_id", "status", "total_cents"],
                payload.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
            Assert.Equal(settlement, payload.RootElement.GetProperty("settlement_id").GetGuid());
            Assert.Equal("VOID", payload.RootElement.GetProperty("status").GetString());
            Assert.Equal(4_500, payload.RootElement.GetProperty("total_cents").GetInt64());
            Assert.Equal(1, payload.RootElement.GetProperty("line_count").GetInt32());
            Assert.DoesNotContain(driver.ToString("D"), audit.Payload, StringComparison.Ordinal);
        });
    }

    private static DateOnly TodayInCuliacan() =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Culiacan).DateTime);

    /// <summary>A seven-day period ending on <paramref name="periodTo"/>, well inside the 366-day limit.</summary>
    private static string WeekEndingBody(Guid driver, DateOnly periodTo) =>
        $$"""{"driver_id":"{{driver:D}}","period_from":"{{Iso(periodTo.AddDays(-6))}}","period_to":"{{Iso(periodTo)}}"}""";

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private async Task<List<ExportAudit>> ExportAuditsAsync(Guid settlementId)
    {
        await using var connection = new NpgsqlConnection(fixture.Api.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT actor_id,request_id,payload_redacted::text
            FROM platform.audit_logs
            WHERE entity_type='Settlement' AND entity_id=@id AND action='finance.settlement.exported'
            ORDER BY occurred_at,id
            """,
            connection);
        command.Parameters.AddWithValue("id", settlementId);
        var audits = new List<ExportAudit>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            audits.Add(new(reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2)));
        }

        return audits;
    }

    [GeneratedRegex("^[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex TraceId();

    private sealed record ExportAudit(Guid ActorId, string? RequestId, string Payload);
}
