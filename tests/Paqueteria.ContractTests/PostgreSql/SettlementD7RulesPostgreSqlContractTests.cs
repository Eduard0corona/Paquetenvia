using System.Text.Json;
using Finance.Application;
using Finance.Application.Cod;
using Finance.Application.Settlements;
using Finance.Infrastructure.Settlements;
using Npgsql;
using Paqueteria.ContractTests.PostgreSql.Fixtures;

namespace Paqueteria.ContractTests.PostgreSql;

/// <summary>
/// SET-001-D7-RULES-ENFORCED-2026-11-06 (D7-SETTLEMENT-RULES, AI-05 x-pilot-contract-deltas D7-SETTLEMENT-CLOSED-PERIOD,
/// D7-SETTLEMENT-NEGATIVE-TOTAL and D7-SETTLEMENT-EXPORT-AUDIT) on real PostgreSQL as the NOBYPASSRLS runtime role: only
/// a period that ended before the operational date in Culiacán is settled, no calculated or adjusted total goes below
/// zero, and every export served leaves exactly one append-only audit entry without personal data.
/// </summary>
public sealed partial class SettlementWorkflowPostgreSqlContractTests
{
    /// <summary>Local midnight of 22 Sep 2026 in Culiacán (UTC-07:00): from here the operational date is 22 Sep.</summary>
    private static readonly DateTimeOffset LocalMidnightOf22Sep = new(2026, 9, 22, 7, 0, 0, TimeSpan.Zero);

    /// <summary>The last microsecond of 21 Sep in Culiacán, although it is already 22 Sep in UTC.</summary>
    private static readonly DateTimeOffset LastMicrosecondOf21Sep = LocalMidnightOf22Sep.AddTicks(-10);

    private static readonly DateOnly September21 = new(2026, 9, 21);
    private static readonly DateOnly September22 = new(2026, 9, 22);

    [PostgreSqlContractFact]
    public async Task Only_a_period_that_ended_before_the_operational_date_in_Culiacan_is_settled()
    {
        await using var scenario = await SettlementScenario.CreateAsync(fixture);
        await scenario.AddWorkAsync("DELIVERED", "DELIVERED", InPeriod, 4_500);

        // A period ending on the operational date, or later, is open; the operational date follows Culiacán's
        // midnight, not UTC's.
        foreach (var (now, periodTo, key) in new[]
                 {
                     (LastMicrosecondOf21Sep, September21, "open-today"),
                     (LastMicrosecondOf21Sep, September22, "open-tomorrow"),
                     (LocalMidnightOf22Sep, September22, "open-today-after-midnight"),
                     (LocalMidnightOf22Sep, September22.AddDays(1), "open-future"),
                 })
        {
            var refused = await Assert.ThrowsAsync<SettlementConflictException>(() =>
                CreateService(now: now).CreateAsync(scenario.Create(key) with { PeriodTo = periodTo }, default));
            Assert.Equal(SettlementConflictCode.PeriodNotClosed, refused.Code);
        }

        // The refusals persisted nothing: no header, line, audit entry or idempotency record.
        Assert.Equal(0L, await scenario.SettlementCountAsync());
        Assert.Equal(0L, await scenario.IdempotencyCountAsync(PostgreSqlSettlementService.CreateIdempotencyScope));
        Assert.Empty(await scenario.AllAuditActionsAsync());

        // From local midnight 21 Sep is closed, and the key refused while it was open calculates normally.
        var closed = await CreateService(now: LocalMidnightOf22Sep).CreateAsync(
            scenario.Create("open-today") with { PeriodTo = September21 }, default);
        Assert.Equal(("CALCULATED", 4_500L, September21), (closed.Status, closed.TotalCents, closed.PeriodTo));
        Assert.Equal(["finance.settlement.calculated"], await scenario.AuditActionsAsync(closed.Id));
    }

    /// <summary>
    /// AI-05 createSettlement: capability comes first, then the idempotency replay, then the closed-period rule, then
    /// the driver. A response already stored is replayed unchanged (the clock is moved back here to stand for a create
    /// accepted before the rule existed), and an open period is refused before any driver is read.
    /// </summary>
    [PostgreSqlContractFact]
    public async Task Capability_and_replay_come_before_the_closed_period_rule_and_the_driver_after_it()
    {
        await using var scenario = await SettlementScenario.CreateAsync(fixture);
        await scenario.AddWorkAsync("DELIVERED", "DELIVERED", InPeriod, 4_500);
        var service = CreateService();

        foreach (var actor in new[] { scenario.DispatcherUserId, scenario.DriverUserId, scenario.ViewerUserId, scenario.AdminUserId })
        {
            await Assert.ThrowsAsync<FinanceForbiddenException>(() => service.CreateAsync(
                scenario.Create("open-forbidden") with { ActorId = actor, PeriodTo = September22 }, default));
        }

        var created = await service.CreateAsync(scenario.Create("replay"), default);
        var beforeTheEnd = new DateTimeOffset(2026, 9, 20, 19, 0, 0, TimeSpan.Zero);
        Assert.Equal(Json(created), Json(await CreateService(now: beforeTheEnd).CreateAsync(scenario.Create("replay"), default)));
        var fresh = await Assert.ThrowsAsync<SettlementConflictException>(() =>
            CreateService(now: beforeTheEnd).CreateAsync(scenario.Create("fresh"), default));
        Assert.Equal(SettlementConflictCode.PeriodNotClosed, fresh.Code);

        // A missing driver and an open period: the period answers, so no driver was read.
        var missingDriver = await Assert.ThrowsAsync<SettlementConflictException>(() => service.CreateAsync(
            scenario.Create("open-missing-driver") with { DriverId = Guid.NewGuid(), PeriodTo = September22 }, default));
        Assert.Equal(SettlementConflictCode.PeriodNotClosed, missingDriver.Code);

        Assert.Equal(1L, await scenario.SettlementCountAsync());
        Assert.Equal(1L, await scenario.IdempotencyCountAsync(PostgreSqlSettlementService.CreateIdempotencyScope));
    }

    /// <summary>
    /// D7-SETTLEMENT-NEGATIVE-TOTAL at create: a calculated total is the exact sum of assignment costs, and AI-06 keeps
    /// every cost_cents at zero or above, so no calculation reaches the refusal today; the service still checks the total
    /// before the header moves (SettlementLedger.AdmitsTotal). An empty period is a total of exactly zero, admitted.
    /// </summary>
    [PostgreSqlContractFact]
    public async Task A_calculated_total_cannot_be_negative_and_an_empty_period_is_admitted_at_zero()
    {
        await using var scenario = await SettlementScenario.CreateAsync(fixture);
        var negativeCost = await Assert.ThrowsAsync<PostgresException>(() =>
            scenario.AddWorkAsync("DELIVERED", "DELIVERED", InPeriod, -1));
        Assert.Equal(PostgresErrorCodes.CheckViolation, negativeCost.SqlState);

        var empty = await CreateService().CreateAsync(scenario.Create("empty"), default);
        Assert.Equal(("CALCULATED", 0L), (empty.Status, empty.TotalCents));
        Assert.Empty(empty.Lines);
    }

    [PostgreSqlContractFact]
    public async Task An_adjustment_that_would_leave_the_total_below_zero_is_refused_and_writes_nothing()
    {
        await using var scenario = await SettlementScenario.CreateAsync(fixture);
        await scenario.AddWorkAsync("DELIVERED", "DELIVERED", InPeriod, 4_500);
        var service = CreateService();
        var created = await service.CreateAsync(scenario.Create("create"), default);

        var below = await Assert.ThrowsAsync<SettlementConflictException>(() =>
            service.AddAdjustmentAsync(scenario.Adjust(created.Id, -4_501, "Descuento mayor al total", "below"), default));
        Assert.Equal(SettlementConflictCode.TotalNegative, below.Code);
        Assert.Equal(("CALCULATED", 4_500L, 4_500m, 1L), await scenario.LedgerStateAsync(created.Id));
        Assert.Equal(["finance.settlement.calculated"], await scenario.AuditActionsAsync(created.Id));
        Assert.Equal(0L, await scenario.IdempotencyCountAsync(PostgreSqlSettlementService.AdjustIdempotencyScope));

        // Exactly zero is admitted; from zero every further deduction is refused, whatever its size.
        var zero = await service.AddAdjustmentAsync(scenario.Adjust(created.Id, -4_500, "Descuento total", "zero"), default);
        Assert.Equal(("CALCULATED", 0L, 2), (zero.Status, zero.TotalCents, zero.Lines.Count));
        foreach (var (amount, key) in new[] { (-1L, "one-cent"), (long.MinValue, "minimum") })
        {
            var refused = await Assert.ThrowsAsync<SettlementConflictException>(() =>
                service.AddAdjustmentAsync(scenario.Adjust(created.Id, amount, "Descuento", key), default));
            Assert.Equal(SettlementConflictCode.TotalNegative, refused.Code);
        }

        // The accepted deduction replays its stored response, although applying it again would go below zero.
        Assert.Equal(Json(zero), Json(await service.AddAdjustmentAsync(
            scenario.Adjust(created.Id, -4_500, "Descuento total", "zero"), default)));

        // A bonus still moves the total up, and the refused key is not burned: its retry is decided again.
        var bonus = await service.AddAdjustmentAsync(scenario.Adjust(created.Id, 300, "Bono", "bonus"), default);
        Assert.Equal(300L, bonus.TotalCents);
        var retried = await service.AddAdjustmentAsync(scenario.Adjust(created.Id, -1, "Descuento", "one-cent"), default);
        Assert.Equal(299L, retried.TotalCents);
        Assert.Equal(("CALCULATED", 299L, 299m, 4L), await scenario.LedgerStateAsync(created.Id));
        Assert.Equal(3, (await scenario.AuditActionsAsync(created.Id)).Count(action => action == "finance.settlement.adjusted"));

        // The settlement state is decided first: an approved settlement is a state conflict, not a negative total.
        await service.ApproveAsync(scenario.Transition(created.Id, "approve"), default);
        await AssertStateConflictAsync(() =>
            service.AddAdjustmentAsync(scenario.Adjust(created.Id, -10_000, "Tarde", "late"), default));
    }

    [PostgreSqlContractFact]
    public async Task Every_export_writes_one_audit_entry_without_personal_data_whatever_the_status()
    {
        await using var scenario = await SettlementScenario.CreateAsync(fixture);
        await scenario.AddWorkAsync("DELIVERED", "DELIVERED", InPeriod, 4_500);
        var service = CreateService();
        var created = await service.CreateAsync(scenario.Create("create"), default);
        await service.AddAdjustmentAsync(scenario.Adjust(created.Id, -250, "Descuento por daño en empaque", "deduction"), default);

        string[] traces = ["4bf92f3577b34da6a3ce929d0e0e4736", "0af7651916cd43dd8448eb211c80319c"];
        var first = await service.ExportCsvAsync(scenario.Get(created.Id) with { RequestId = traces[0] }, default);
        var second = await service.ExportCsvAsync(scenario.Get(created.Id) with { RequestId = traces[1] }, default);
        Assert.Equal(first.Content, second.Content);

        var calculated = await ExportAuditsAsync(scenario, created.Id);
        Assert.Equal(traces.Order(StringComparer.Ordinal), calculated.Select(row => row.RequestId).Order(StringComparer.Ordinal));
        Assert.All(calculated, row => AssertExportAudit(row, created.Id, "CALCULATED", 4_250, 2));

        // APPROVED, PAID and VOID settlements are exported and audited alike.
        await service.ApproveAsync(scenario.Transition(created.Id, "approve"), default);
        await service.ExportCsvAsync(scenario.Get(created.Id), default);
        await service.MarkPaidAsync(scenario.Transition(created.Id, "pay"), default);
        await service.ExportCsvAsync(scenario.Get(created.Id), default);
        var audited = await ExportAuditsAsync(scenario, created.Id);
        Assert.Equal(4, audited.Count);
        Assert.Equal(
            ["APPROVED", "CALCULATED", "CALCULATED", "PAID"],
            audited.Select(row => JsonDocument.Parse(row.Payload).RootElement.GetProperty("status").GetString()).Order(StringComparer.Ordinal));
        Assert.Equal(2, audited.Count(row => row.RequestId is null));

        await scenario.AddWorkAsync("RETURNED", "RETURNED", InPeriod.AddHours(1), 3_000);
        var other = await service.CreateAsync(scenario.Create("other"), default);
        await service.VoidAsync(scenario.Void(other.Id, "void-other"), default);
        await service.ExportCsvAsync(scenario.Get(other.Id), default);
        AssertExportAudit(Assert.Single(await ExportAuditsAsync(scenario, other.Id)), other.Id, "VOID", 3_000, 1);

        // An export changes nothing else: the ledgers are as they were.
        Assert.Equal(("PAID", 4_250L, 4_250m, 2L), await scenario.LedgerStateAsync(created.Id));
        Assert.Equal(("VOID", 3_000L, 3_000m, 1L), await scenario.LedgerStateAsync(other.Id));
    }

    [PostgreSqlContractFact]
    public async Task A_refused_or_failed_export_writes_no_audit_entry_and_serves_no_CSV()
    {
        await using var scenario = await SettlementScenario.CreateAsync(fixture);
        await using var foreign = await SettlementScenario.CreateAsync(fixture);
        await scenario.AddWorkAsync("DELIVERED", "DELIVERED", InPeriod, 4_500);
        await foreign.AddWorkAsync("DELIVERED", "DELIVERED", InPeriod, 9_000);
        var service = CreateService();
        var created = await service.CreateAsync(scenario.Create("create"), default);
        var foreignCreated = await CreateService().CreateAsync(foreign.Create("create"), default);

        foreach (var actor in new[] { scenario.DispatcherUserId, scenario.DriverUserId, scenario.ViewerUserId, scenario.AdminUserId })
        {
            await Assert.ThrowsAsync<FinanceForbiddenException>(() =>
                service.ExportCsvAsync(scenario.Get(created.Id) with { ActorId = actor }, default));
        }

        await Assert.ThrowsAsync<FinanceNotFoundException>(() => service.ExportCsvAsync(scenario.Get(foreignCreated.Id), default));
        await Assert.ThrowsAsync<FinanceNotFoundException>(() => service.ExportCsvAsync(scenario.Get(Guid.NewGuid()), default));

        // The audit entry and the read share one transaction: if it cannot commit, no CSV leaves.
        foreach (var stage in new[] { FinanceTransactionStage.AuditInserted, FinanceTransactionStage.BeforeCommit })
        {
            var failing = CreateService(new ThrowingFailureInjector(stage, new NpgsqlException("Simulated store failure.")));
            var failure = await Assert.ThrowsAsync<FinanceUnavailableException>(() =>
                failing.ExportCsvAsync(scenario.Get(created.Id), default));
            Assert.IsType<NpgsqlException>(failure.InnerException);
        }

        // A ledger that does not reconcile is never exported, and its refusal is not an export either.
        await scenario.CorruptTotalBypassingTriggersAsync(created.Id, 4_501);
        await Assert.ThrowsAsync<FinanceUnavailableException>(() => service.ExportCsvAsync(scenario.Get(created.Id), default));

        Assert.Empty(await ExportAuditsAsync(scenario, created.Id));
        Assert.Empty(await ExportAuditsAsync(foreign, foreignCreated.Id));
        Assert.DoesNotContain(PostgreSqlSettlementService.ExportAuditAction, await scenario.AllAuditActionsAsync());
    }

    private static async Task<List<AuditRow>> ExportAuditsAsync(SettlementScenario scenario, Guid settlementId)
    {
        var rows = new List<AuditRow>();
        foreach (var id in await scenario.AuditIdsAsync(settlementId, PostgreSqlSettlementService.ExportAuditAction))
        {
            rows.Add(await scenario.AuditAsync(id));
        }

        return rows;
    }

    /// <summary>
    /// The export entry names the settlement and the state exported - status, total and line count - and nothing
    /// else: no payee, no order, no adjustment reason or other free text.
    /// </summary>
    private static void AssertExportAudit(AuditRow row, Guid settlementId, string status, long totalCents, int lineCount)
    {
        Assert.Equal(
            (PostgreSqlSettlementService.ExportAuditAction, PostgreSqlSettlementService.AuditEntityType, settlementId),
            (row.Action, row.EntityType, row.EntityId));
        using var payload = JsonDocument.Parse(row.Payload);
        var root = payload.RootElement;
        Assert.Equal(
            ["line_count", "settlement_id", "status", "total_cents"],
            root.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(settlementId, root.GetProperty("settlement_id").GetGuid());
        Assert.Equal(status, root.GetProperty("status").GetString());
        Assert.Equal(totalCents, root.GetProperty("total_cents").GetInt64());
        Assert.Equal(lineCount, root.GetProperty("line_count").GetInt32());
        Assert.DoesNotContain("Descuento", row.Payload, StringComparison.Ordinal);
        Assert.DoesNotContain("REDACTED", row.Payload, StringComparison.Ordinal);
    }
}
