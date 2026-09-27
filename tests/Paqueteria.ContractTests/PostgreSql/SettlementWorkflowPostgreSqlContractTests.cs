using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Finance.Application;
using Finance.Application.Cod;
using Finance.Application.Settlements;
using Finance.Infrastructure;
using Finance.Infrastructure.Persistence;
using Finance.Infrastructure.Settlements;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Paqueteria.Application;
using Paqueteria.Application.Auditing;
using Paqueteria.ContractTests.PostgreSql.Fixtures;
using Paqueteria.Infrastructure.Auditing;
using Paqueteria.Infrastructure.Tenancy;

namespace Paqueteria.ContractTests.PostgreSql;

/// <summary>
/// SET-001 Slice 2 on a real PostgreSQL: the production settlement service on the runtime application
/// role, under FORCE ROW LEVEL SECURITY, against the Slice 1 ledger guards and the canonical Dispatch,
/// Orders, Incidents and COD rows it derives from. Nothing here stubs the database.
/// </summary>
[Collection(PostgreSqlContractCollection.Name)]
[Trait("Category", "PostgreSqlContract")]
public sealed class SettlementWorkflowPostgreSqlContractTests(PostgreSqlContractFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 17, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly PeriodFrom = new(2026, 9, 14);
    private static readonly DateOnly PeriodTo = new(2026, 9, 20);

    /// <summary>America/Mazatlan is UTC-07:00, so the period is [14 Sep 07:00Z, 21 Sep 07:00Z).</summary>
    private static readonly DateTimeOffset PeriodStartsAt = new(2026, 9, 14, 7, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset PeriodEndsBefore = new(2026, 9, 21, 7, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset InPeriod = new(2026, 9, 16, 18, 30, 0, TimeSpan.Zero);

    [PostgreSqlContractFact]
    public async Task Create_calculates_exactly_the_proven_deliveries_and_returns_of_the_period()
    {
        await using var scenario = await SettlementScenario.CreateAsync(fixture);
        var delivered = await scenario.AddWorkAsync("DELIVERED", "DELIVERED", InPeriod, 4_500);
        var closed = await scenario.AddWorkAsync("CLOSED", "DELIVERED", InPeriod.AddHours(1), 8_000, assignmentType: "EXTERNAL");
        var claimResolved = await scenario.AddWorkAsync("CLAIM_RESOLVED", "DELIVERED", InPeriod.AddHours(2), 2_000);
        var returned = await scenario.AddWorkAsync("RETURNED", "RETURNED", InPeriod.AddHours(3), 3_000, codExpected: 5_000);
        var free = await scenario.AddWorkAsync("DELIVERED", "DELIVERED", InPeriod.AddHours(4), 0);
        var atStart = await scenario.AddWorkAsync("DELIVERED", "DELIVERED", PeriodStartsAt, 100);
        var lastMicrosecond = await scenario.AddWorkAsync("DELIVERED", "DELIVERED", PeriodEndsBefore.AddTicks(-10), 200);

        // Nothing else is payable to this driver in this period.
        await scenario.AddWorkAsync("DELIVERED", "DELIVERED", PeriodStartsAt.AddTicks(-10), 11);
        await scenario.AddWorkAsync("DELIVERED", "DELIVERED", PeriodEndsBefore, 12);
        await scenario.AddWorkAsync("DELIVERED", "DELIVERED", InPeriod, 13, assignmentType: "ALLY_CAPACITY");
        await scenario.AddWorkAsync("DELIVERED", "DELIVERED", InPeriod, 14, assignmentStatus: "CANCELLED");
        await scenario.AddWorkAsync("DELIVERED", "DELIVERED", InPeriod, 15, assignmentStatus: "OFFERED");
        await scenario.AddWorkAsync("DELIVERED", "DELIVERED", InPeriod, 16, driverId: scenario.OtherDriverId);
        await scenario.AddWorkAsync("DELIVERING", null, InPeriod, 17);
        await scenario.AddWorkAsync("FAILED_ATTEMPT", null, InPeriod, 18);
        await scenario.AddWorkAsync("RETURNING", null, InPeriod, 19);
        await scenario.AddWorkAsync("CANCELLED", null, InPeriod, 20);

        var service = CreateService();
        var created = await service.CreateAsync(scenario.Create("create"), default);

        Assert.Equal("CALCULATED", created.Status);
        Assert.Equal("DRIVER", created.PayeeType);
        Assert.Equal(scenario.DriverId, created.PayeeId);
        Assert.Equal((PeriodFrom, PeriodTo), (created.PeriodFrom, created.PeriodTo));
        Assert.Equal(Now, created.CreatedAt);
        var expected = new[] { delivered, closed, claimResolved, returned, free, atStart, lastMicrosecond };
        Assert.Equal(
            expected.Select(work => work.Source).Order(StringComparer.Ordinal),
            created.Lines.Select(line => line.SourceReference).Order(StringComparer.Ordinal));
        Assert.All(created.Lines, line =>
        {
            var work = expected.Single(candidate => candidate.Source == line.SourceReference);
            Assert.Equal(work.OrderId, line.OrderId);
            Assert.Equal(work.CostCents, line.AmountCents);
            Assert.Equal(work == returned ? "RETURN" : "DELIVERY", line.LineType);
            Assert.Equal(Now, line.CreatedAt);
        });
        Assert.Equal(expected.Sum(work => work.CostCents), created.TotalCents);
        Assert.Equal(17_800, created.TotalCents);

        // The lines come back in the ledger's (created_at, id) order, and the persisted settlement is what
        // create returned.
        Assert.Equal(
            created.Lines.Select(line => line.Id.ToString("D")).Order(StringComparer.Ordinal),
            created.Lines.Select(line => line.Id.ToString("D")));
        Assert.Equal(Json(created), Json(await service.GetAsync(scenario.Get(created.Id), default)));
        Assert.Equal(("CALCULATED", 17_800L, 17_800m, 7L), await scenario.LedgerStateAsync(created.Id));
        Assert.Equal(["finance.settlement.calculated"], await scenario.AuditActionsAsync(created.Id));
        Assert.Equal(1L, await scenario.IdempotencyCountAsync(PostgreSqlSettlementService.CreateIdempotencyScope));
    }

    [PostgreSqlContractFact]
    public async Task Create_replays_its_original_response_and_a_reused_key_never_writes_twice()
    {
        await using var scenario = await SettlementScenario.CreateAsync(fixture);
        await scenario.AddWorkAsync("DELIVERED", "DELIVERED", InPeriod, 4_500);
        var service = CreateService();

        var created = await service.CreateAsync(scenario.Create("replay"), default);
        await scenario.AddWorkAsync("DELIVERED", "DELIVERED", InPeriod.AddHours(1), 1_000);
        var replay = await CreateService().CreateAsync(scenario.Create("replay"), default);
        Assert.Equal(Json(created), Json(replay));

        var reused = await Assert.ThrowsAsync<SettlementConflictException>(() =>
            service.CreateAsync(scenario.Create("replay") with { PeriodTo = PeriodTo.AddDays(1) }, default));
        Assert.Equal(SettlementConflictCode.IdempotencyConflict, reused.Code);
        Assert.Equal(1L, await scenario.SettlementCountAsync());
        Assert.Equal(["finance.settlement.calculated"], await scenario.AuditActionsAsync(created.Id));
    }

    [PostgreSqlContractFact]
    public async Task An_economic_source_is_settled_at_most_once_until_its_settlement_is_voided()
    {
        await using var scenario = await SettlementScenario.CreateAsync(fixture);
        var delivered = await scenario.AddWorkAsync("DELIVERED", "DELIVERED", InPeriod, 4_500);
        var returned = await scenario.AddWorkAsync("RETURNED", "RETURNED", InPeriod.AddHours(1), 3_000);
        var service = CreateService();

        var first = await service.CreateAsync(scenario.Create("first"), default);
        Assert.Equal(7_500, first.TotalCents);

        // An overlapping period finds nothing left to pay: DELIVERY and RETURN share one source identity.
        var overlapping = await service.CreateAsync(
            scenario.Create("overlapping") with { PeriodFrom = PeriodFrom.AddDays(-3), PeriodTo = PeriodTo.AddDays(3) },
            default);
        Assert.Equal(("CALCULATED", 0L), (overlapping.Status, overlapping.TotalCents));
        Assert.Empty(overlapping.Lines);

        // Voiding releases the sources without touching the voided ledger.
        var voided = await service.VoidAsync(scenario.Void(first.Id, "void-first"), default);
        Assert.Equal(("VOID", 7_500L, 2), (voided.Status, voided.TotalCents, voided.Lines.Count));
        var again = await service.CreateAsync(scenario.Create("again"), default);
        Assert.Equal(
            new[] { delivered.Source, returned.Source }.Order(StringComparer.Ordinal),
            again.Lines.Select(line => line.SourceReference).Order(StringComparer.Ordinal));
        Assert.Equal(("VOID", 7_500L, 7_500m, 2L), await scenario.LedgerStateAsync(first.Id));

        // The ledger itself still refuses a second non-VOID claim on a source.
        var duplicate = await scenario.InsertDuplicateSourceAsync(again.Id, delivered.Source);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
    }

    [PostgreSqlContractFact]
    public async Task Adjustments_append_signed_audited_lines_and_move_the_total_exactly()
    {
        await using var scenario = await SettlementScenario.CreateAsync(fixture);
        var delivered = await scenario.AddWorkAsync("DELIVERED", "DELIVERED", InPeriod, 4_500);
        var service = CreateService();
        var created = await service.CreateAsync(scenario.Create("create"), default);

        var bonus = await service.AddAdjustmentAsync(scenario.Adjust(created.Id, 750, "Bono por puntualidad", "bonus"), default);
        var deduction = await service.AddAdjustmentAsync(scenario.Adjust(created.Id, -500, "Descuento por daño", "deduction"), default);

        Assert.Equal(5_250L, bonus.TotalCents);
        Assert.Equal(("CALCULATED", 4_750L, 3), (deduction.Status, deduction.TotalCents, deduction.Lines.Count));
        var original = Assert.Single(deduction.Lines, line => line.SourceReference == delivered.Source);
        Assert.Equal(created.Lines.Single().Id, original.Id);
        var adjustments = deduction.Lines.Where(line => line.LineType == "ADJUSTMENT").ToArray();
        Assert.Equal([-500L, 750L], adjustments.Select(line => line.AmountCents).Order());
        Assert.All(adjustments, line => Assert.Null(line.OrderId));

        // Each adjustment names the append-only audit entry that recorded it, reason included.
        foreach (var line in adjustments)
        {
            Assert.StartsWith("platform.audit_logs/", line.SourceReference, StringComparison.Ordinal);
            var audit = await scenario.AuditAsync(Guid.Parse(line.SourceReference["platform.audit_logs/".Length..]));
            Assert.Equal(("finance.settlement.adjusted", "Settlement", created.Id), (audit.Action, audit.EntityType, audit.EntityId));

            // The platform redactor may mask a GUID string it mistakes for a phone number, so the payload is
            // matched on its numeric amount; the link itself is the audit id the line names.
            Assert.Contains("\"line_id\":", audit.Payload, StringComparison.Ordinal);
            Assert.Contains(
                "\"amount_cents\": " + line.AmountCents.ToString(System.Globalization.CultureInfo.InvariantCulture),
                audit.Payload,
                StringComparison.Ordinal);
        }

        // Replay returns the stored response; a reused key with another amount adds nothing.
        Assert.Equal(Json(bonus), Json(await service.AddAdjustmentAsync(
            scenario.Adjust(created.Id, 750, "Bono por puntualidad", "bonus"), default)));
        var reused = await Assert.ThrowsAsync<SettlementConflictException>(() => service.AddAdjustmentAsync(
            scenario.Adjust(created.Id, 751, "Bono por puntualidad", "bonus"), default));
        Assert.Equal(SettlementConflictCode.IdempotencyConflict, reused.Code);
        Assert.Equal(("CALCULATED", 4_750L, 4_750m, 3L), await scenario.LedgerStateAsync(created.Id));
        Assert.Equal(2, (await scenario.AuditActionsAsync(created.Id)).Count(action => action == "finance.settlement.adjusted"));
    }

    [PostgreSqlContractFact]
    public async Task An_adjustment_that_would_leave_the_bigint_range_is_refused_without_a_line()
    {
        await using var scenario = await SettlementScenario.CreateAsync(fixture);
        var service = CreateService();
        var empty = await service.CreateAsync(scenario.Create("empty"), default);
        Assert.Equal(0, empty.TotalCents);

        var maximum = await service.AddAdjustmentAsync(scenario.Adjust(empty.Id, long.MaxValue, "Tope", "max"), default);
        Assert.Equal(long.MaxValue, maximum.TotalCents);
        var overflow = await Assert.ThrowsAsync<SettlementConflictException>(() =>
            service.AddAdjustmentAsync(scenario.Adjust(empty.Id, 1, "Excede", "overflow"), default));
        Assert.Equal(SettlementConflictCode.InvalidRequest, overflow.Code);
        Assert.Equal(("CALCULATED", long.MaxValue, (decimal)long.MaxValue, 1L), await scenario.LedgerStateAsync(empty.Id));
    }

    [PostgreSqlContractFact]
    public async Task Approval_freezes_the_settlement_and_payment_is_terminal()
    {
        await using var scenario = await SettlementScenario.CreateAsync(fixture);
        await scenario.AddWorkAsync("CLOSED", "DELIVERED", InPeriod, 4_500);
        var service = CreateService();
        var created = await service.CreateAsync(scenario.Create("create"), default);

        var approved = await service.ApproveAsync(scenario.Transition(created.Id, "approve"), default);
        Assert.Equal(("APPROVED", 4_500L), (approved.Status, approved.TotalCents));
        Assert.Equal(Json(approved), Json(await service.ApproveAsync(scenario.Transition(created.Id, "approve"), default)));

        await AssertStateConflictAsync(() => service.AddAdjustmentAsync(scenario.Adjust(created.Id, 1, "Tarde", "late"), default));
        await AssertStateConflictAsync(() => service.ApproveAsync(scenario.Transition(created.Id, "approve-again"), default));

        // The Slice 1 guard freezes the approved economics underneath the service as well.
        Assert.Equal("23514", (await scenario.UpdateTotalDirectlyAsync(created.Id, 4_501)).SqlState);

        var paid = await service.MarkPaidAsync(scenario.Transition(created.Id, "pay"), default);
        Assert.Equal(("PAID", 4_500L, 1), (paid.Status, paid.TotalCents, paid.Lines.Count));
        await AssertStateConflictAsync(() => service.MarkPaidAsync(scenario.Transition(created.Id, "pay-again"), default));
        await AssertStateConflictAsync(() => service.VoidAsync(scenario.Void(created.Id, "void-paid"), default));
        await AssertStateConflictAsync(() => service.AddAdjustmentAsync(scenario.Adjust(created.Id, 1, "Tarde", "late-paid"), default));

        Assert.Equal(("PAID", 4_500L, 4_500m, 1L), await scenario.LedgerStateAsync(created.Id));
        Assert.Equal(
            ["finance.settlement.approved", "finance.settlement.calculated", "finance.settlement.paid"],
            (await scenario.AuditActionsAsync(created.Id)).Order(StringComparer.Ordinal));
    }

    [PostgreSqlContractFact]
    public async Task Payment_requires_approval_first()
    {
        await using var scenario = await SettlementScenario.CreateAsync(fixture);
        await scenario.AddWorkAsync("DELIVERED", "DELIVERED", InPeriod, 4_500);
        var service = CreateService();
        var created = await service.CreateAsync(scenario.Create("create"), default);

        await AssertStateConflictAsync(() => service.MarkPaidAsync(scenario.Transition(created.Id, "pay-early"), default));
        Assert.Equal(("CALCULATED", 4_500L, 4_500m, 1L), await scenario.LedgerStateAsync(created.Id));
    }

    [PostgreSqlContractFact]
    public async Task Pending_cash_blocks_approval_until_it_is_reconciled()
    {
        await using var scenario = await SettlementScenario.CreateAsync(fixture);
        var cod = await scenario.AddWorkAsync("DELIVERED", "DELIVERED", InPeriod, 4_500, codExpected: 5_000);
        var service = CreateService();
        var created = await service.CreateAsync(scenario.Create("create"), default);

        // No collection at all, then a recorded but unreconciled one, then one reconciled for less than expected.
        await AssertBlockedAsync(service, scenario, created.Id, "no-cod", SettlementConflictCode.CashPending);
        await scenario.SetCodAsync(cod.OrderId, 5_000, "RECORDED");
        await AssertBlockedAsync(service, scenario, created.Id, "recorded", SettlementConflictCode.CashPending);
        await scenario.SetCodAsync(cod.OrderId, 4_000, "RECONCILED");
        await AssertBlockedAsync(service, scenario, created.Id, "reconciled-short", SettlementConflictCode.CashPending);

        await scenario.SetCodAsync(cod.OrderId, 5_000, "RECONCILED");
        var approved = await service.ApproveAsync(scenario.Transition(created.Id, "reconciled"), default);
        Assert.Equal("APPROVED", approved.Status);
    }

    [PostgreSqlContractFact]
    public async Task A_return_is_not_blocked_by_its_order_COD_expectation()
    {
        await using var scenario = await SettlementScenario.CreateAsync(fixture);
        await scenario.AddWorkAsync("RETURNED", "RETURNED", InPeriod, 3_000, codExpected: 5_000);
        var service = CreateService();
        var created = await service.CreateAsync(scenario.Create("create"), default);
        Assert.Equal("RETURN", Assert.Single(created.Lines).LineType);

        var approved = await service.ApproveAsync(scenario.Transition(created.Id, "approve"), default);
        Assert.Equal(("APPROVED", 3_000L), (approved.Status, approved.TotalCents));
    }

    [PostgreSqlContractFact]
    public async Task Only_pending_incidents_block_approval()
    {
        foreach (var (incidentStatus, blocks) in new[] { ("OPEN", true), ("INVESTIGATING", true), ("RESOLVED", false), ("REJECTED", false) })
        {
            await using var scenario = await SettlementScenario.CreateAsync(fixture);
            var work = await scenario.AddWorkAsync("DELIVERED", "DELIVERED", InPeriod, 3_000);
            await scenario.AddIncidentAsync(work.OrderId, incidentStatus);
            var service = CreateService();
            var created = await service.CreateAsync(scenario.Create("create"), default);

            if (blocks)
            {
                await AssertBlockedAsync(service, scenario, created.Id, "incident", SettlementConflictCode.IncidentPending);
            }
            else
            {
                Assert.Equal("APPROVED", (await service.ApproveAsync(scenario.Transition(created.Id, "incident"), default)).Status);
            }
        }
    }

    [PostgreSqlContractFact]
    public async Task An_open_claim_blocks_approval_until_it_is_resolved()
    {
        await using var scenario = await SettlementScenario.CreateAsync(fixture);
        var claim = await scenario.AddWorkAsync("CLAIM_OPEN", "DELIVERED", InPeriod, 4_500);
        var service = CreateService();
        var created = await service.CreateAsync(scenario.Create("create"), default);
        Assert.Equal("DELIVERY", Assert.Single(created.Lines).LineType);

        await AssertBlockedAsync(service, scenario, created.Id, "claim-open", SettlementConflictCode.ClaimPending);
        await scenario.SetOrderStatusAsync(claim.OrderId, "CLAIM_RESOLVED");
        Assert.Equal("APPROVED", (await service.ApproveAsync(scenario.Transition(created.Id, "claim-resolved"), default)).Status);
    }

    [PostgreSqlContractFact]
    public async Task Void_keeps_every_line_and_total_from_each_voidable_status()
    {
        await using var scenario = await SettlementScenario.CreateAsync(fixture);
        var service = CreateService();

        await scenario.AddWorkAsync("DELIVERED", "DELIVERED", InPeriod, 4_500);
        var calculated = await service.CreateAsync(scenario.Create("calculated"), default);
        await service.AddAdjustmentAsync(scenario.Adjust(calculated.Id, 250, "Bono", "bonus"), default);
        var voidedCalculated = await service.VoidAsync(scenario.Void(calculated.Id, "void-calculated"), default);
        Assert.Equal(("VOID", 4_750L, 2), (voidedCalculated.Status, voidedCalculated.TotalCents, voidedCalculated.Lines.Count));

        await scenario.AddWorkAsync("RETURNED", "RETURNED", InPeriod, 3_000);
        var approved = await service.CreateAsync(scenario.Create("approved"), default);
        await service.ApproveAsync(scenario.Transition(approved.Id, "approve"), default);
        var voidedApproved = await service.VoidAsync(scenario.Void(approved.Id, "void-approved"), default);
        Assert.Equal(("VOID", approved.TotalCents, approved.Lines.Count), (voidedApproved.Status, voidedApproved.TotalCents, voidedApproved.Lines.Count));

        var draft = await scenario.InsertDraftAsync();
        Assert.Equal("VOID", (await service.VoidAsync(scenario.Void(draft, "void-draft"), default)).Status);

        await AssertStateConflictAsync(() => service.VoidAsync(scenario.Void(calculated.Id, "void-twice"), default));
        await AssertStateConflictAsync(() => service.ApproveAsync(scenario.Transition(calculated.Id, "approve-void"), default));
        Assert.Equal(("VOID", 4_750L, 4_750m, 2L), await scenario.LedgerStateAsync(calculated.Id));

        var audit = await scenario.AuditAsync((await scenario.AuditIdsAsync(calculated.Id, "finance.settlement.voided")).Single());
        Assert.Contains("\"previous_status\":\"CALCULATED\"", audit.Payload.Replace(" ", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("\"reason\"", audit.Payload, StringComparison.Ordinal);
    }

    [PostgreSqlContractFact]
    public async Task Reads_and_exports_return_the_persisted_ledger_never_a_recomputation()
    {
        await using var scenario = await SettlementScenario.CreateAsync(fixture);
        var work = await scenario.AddWorkAsync("DELIVERED", "DELIVERED", InPeriod, 4_500);
        await scenario.AddWorkAsync("RETURNED", "RETURNED", InPeriod.AddHours(1), 3_000);
        var service = CreateService();
        var created = await service.CreateAsync(scenario.Create("create"), default);
        await service.AddAdjustmentAsync(scenario.Adjust(created.Id, -250, "Descuento", "deduction"), default);
        var before = await service.GetAsync(scenario.Get(created.Id), default);
        var export = await service.ExportCsvAsync(scenario.Get(created.Id), default);

        // The sources change after calculation; the persisted settlement does not.
        await scenario.SetAssignmentCostAsync(work.AssignmentId, 99_999);
        await scenario.SetOrderStatusAsync(work.OrderId, "CLAIM_OPEN");

        Assert.Equal(Json(before), Json(await service.GetAsync(scenario.Get(created.Id), default)));
        var again = await service.ExportCsvAsync(scenario.Get(created.Id), default);
        Assert.Equal(export.Content, again.Content);
        Assert.Equal($"settlement-{created.Id:D}.csv", export.FileName);

        var records = Encoding.UTF8.GetString(export.Content).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(string.Join(',', SettlementCsvWriter.Columns), records[0]);
        Assert.Equal(before.Lines.Count, records.Length - 1);
        Assert.Equal(
            before.Lines.Select(line => line.Id.ToString("D")),
            records.Skip(1).Select(record => record.Split(',')[7]));
        Assert.Equal(
            before.TotalCents,
            records.Skip(1).Sum(record => long.Parse(record.Split(',')[10], System.Globalization.CultureInfo.InvariantCulture)));
        Assert.All(records.Skip(1), record => Assert.Equal(before.TotalCents.ToString(System.Globalization.CultureInfo.InvariantCulture), record.Split(',')[6]));
        Assert.DoesNotContain("99999", records.Skip(1).Select(record => record.Split(',')[10]));
    }

    [PostgreSqlContractFact]
    public async Task An_inconsistent_persisted_ledger_fails_closed_and_is_never_repaired()
    {
        await using var scenario = await SettlementScenario.CreateAsync(fixture);
        await scenario.AddWorkAsync("DELIVERED", "DELIVERED", InPeriod, 4_500);
        var service = CreateService();
        var created = await service.CreateAsync(scenario.Create("create"), default);

        // Only a superuser bypassing every trigger can produce this; the service must refuse to rely on it.
        await scenario.CorruptTotalBypassingTriggersAsync(created.Id, 4_501);

        await Assert.ThrowsAsync<FinanceUnavailableException>(() => service.GetAsync(scenario.Get(created.Id), default));
        await Assert.ThrowsAsync<FinanceUnavailableException>(() => service.ExportCsvAsync(scenario.Get(created.Id), default));
        await Assert.ThrowsAsync<FinanceUnavailableException>(() =>
            service.ApproveAsync(scenario.Transition(created.Id, "approve-corrupt"), default));
        Assert.Equal(("CALCULATED", 4_501L, 4_500m, 1L), await scenario.LedgerStateAsync(created.Id));
        Assert.Equal(0L, await scenario.IdempotencyCountAsync(PostgreSqlSettlementService.ApproveIdempotencyScope));
    }

    [PostgreSqlContractFact]
    public async Task Capability_is_decided_before_any_settlement_or_idempotency_evidence()
    {
        await using var scenario = await SettlementScenario.CreateAsync(fixture);
        await scenario.AddWorkAsync("DELIVERED", "DELIVERED", InPeriod, 4_500);
        var service = CreateService();
        var created = await service.CreateAsync(scenario.Create("create"), default);

        foreach (var actor in new[] { scenario.DispatcherUserId, scenario.DriverUserId, scenario.ViewerUserId, scenario.AdminUserId })
        {
            // The same key FINANCE used, an existing settlement and a missing one all look the same: 403.
            await Assert.ThrowsAsync<FinanceForbiddenException>(() =>
                service.CreateAsync(scenario.Create("create") with { ActorId = actor }, default));
            foreach (var settlement in new[] { created.Id, Guid.NewGuid() })
            {
                await Assert.ThrowsAsync<FinanceForbiddenException>(() =>
                    service.GetAsync(scenario.Get(settlement) with { ActorId = actor }, default));
                await Assert.ThrowsAsync<FinanceForbiddenException>(() =>
                    service.ExportCsvAsync(scenario.Get(settlement) with { ActorId = actor }, default));
                await Assert.ThrowsAsync<FinanceForbiddenException>(() =>
                    service.AddAdjustmentAsync(scenario.Adjust(settlement, 1, "Bono", "adjust") with { ActorId = actor }, default));
                await Assert.ThrowsAsync<FinanceForbiddenException>(() =>
                    service.ApproveAsync(scenario.Transition(settlement, "approve") with { ActorId = actor, MfaSatisfied = false }, default));
                await Assert.ThrowsAsync<FinanceForbiddenException>(() =>
                    service.MarkPaidAsync(scenario.Transition(settlement, "pay") with { ActorId = actor, MfaSatisfied = false }, default));
                await Assert.ThrowsAsync<FinanceForbiddenException>(() =>
                    service.VoidAsync(scenario.Void(settlement, "void") with { ActorId = actor }, default));
            }
        }

        // D7-SETTLEMENT-MFA: FINANCE without MFA may not approve or pay, and the refusal changes nothing.
        foreach (var settlement in new[] { created.Id, Guid.NewGuid() })
        {
            await Assert.ThrowsAsync<FinanceForbiddenException>(() =>
                service.ApproveAsync(scenario.Transition(settlement, "approve-no-mfa") with { MfaSatisfied = false }, default));
            await Assert.ThrowsAsync<FinanceForbiddenException>(() =>
                service.MarkPaidAsync(scenario.Transition(settlement, "pay-no-mfa") with { MfaSatisfied = false }, default));
        }

        Assert.Equal("CALCULATED", (await service.GetAsync(scenario.Get(created.Id), default)).Status);

        // PLATFORM_ADMIN operates only with MFA, even when it also holds FINANCE.
        var withMfa = await service.GetAsync(scenario.Get(created.Id) with { ActorId = scenario.AdminUserId, MfaSatisfied = true }, default);
        Assert.Equal(created.Id, withMfa.Id);
        await scenario.GrantAsync(scenario.AdminUserId, "FINANCE");
        await Assert.ThrowsAsync<FinanceForbiddenException>(() =>
            service.GetAsync(scenario.Get(created.Id) with { ActorId = scenario.AdminUserId }, default));

        // A DISPATCHER who is also FINANCE operates as FINANCE; a suspended FINANCE membership does not.
        await scenario.GrantAsync(scenario.DispatcherUserId, "FINANCE");
        Assert.Equal(created.Id, (await service.GetAsync(scenario.Get(created.Id) with { ActorId = scenario.DispatcherUserId }, default)).Id);
        await scenario.SuspendAsync(scenario.FinanceUserId, "FINANCE");
        await Assert.ThrowsAsync<FinanceForbiddenException>(() => service.GetAsync(scenario.Get(created.Id), default));

        Assert.Equal(1L, await scenario.IdempotencyCountAsync(PostgreSqlSettlementService.CreateIdempotencyScope));
        Assert.Equal(0L, await scenario.IdempotencyCountAsync(PostgreSqlSettlementService.ApproveIdempotencyScope));
        Assert.Equal(("CALCULATED", 4_500L, 4_500m, 1L), await scenario.LedgerStateAsync(created.Id));
    }

    /// <summary>
    /// AI05-LIST-SETTLEMENTS on the runtime role under FORCE RLS: a tenant lists exactly its own settlements,
    /// even when the persisted rows of another tenant match every filter, and RLS alone already hides them.
    /// </summary>
    [PostgreSqlContractFact]
    public async Task List_returns_only_the_selected_tenant_and_rls_hides_every_other()
    {
        await using var scenario = await SettlementScenario.CreateAsync(fixture);
        await using var foreign = await SettlementScenario.CreateAsync(fixture);
        await scenario.AddWorkAsync("DELIVERED", "DELIVERED", InPeriod, 4_500);
        await foreign.AddWorkAsync("DELIVERED", "DELIVERED", InPeriod, 9_000);
        var service = CreateService();
        var own = await service.CreateAsync(scenario.Create("create"), default);
        var draft = await scenario.InsertDraftAsync();
        var foreignCreated = await CreateService().CreateAsync(foreign.Create("create"), default);

        var page = await service.ListAsync(scenario.List(), default);
        Assert.Equal(new[] { own.Id, draft }.Order(), page.Items.Select(item => item.Id).Order());
        Assert.Null(page.NextCursor);
        Assert.DoesNotContain(page.Items, item => item.Id == foreignCreated.Id);

        // Each listed settlement is exactly what getSettlement returns for it.
        Assert.Equal(Json(own), Json(page.Items.Single(item => item.Id == own.Id)));

        var foreignPage = await CreateService().ListAsync(foreign.List(), default);
        Assert.Equal([foreignCreated.Id], foreignPage.Items.Select(item => item.Id));

        // The status filter, and RLS underneath the explicit tenant predicate.
        Assert.Equal([own.Id], (await service.ListAsync(scenario.List() with { Status = "CALCULATED" }, default))
            .Items.Select(item => item.Id));
        Assert.Equal([draft], (await service.ListAsync(scenario.List() with { Status = "DRAFT" }, default))
            .Items.Select(item => item.Id));
        // Both the calculated settlement and the DRAFT header pay the settled driver; the other driver has none.
        Assert.Equal(
            new[] { own.Id, draft }.Order(),
            (await service.ListAsync(scenario.List() with { PayeeId = scenario.DriverId }, default))
                .Items.Select(item => item.Id).Order());
        Assert.Empty((await service.ListAsync(scenario.List() with { PayeeId = scenario.OtherDriverId }, default)).Items);
        Assert.Equal(0L, await scenario.CountSettlementsVisibleForAsync(foreign.OrganizationId));
        Assert.Equal(2L, await scenario.CountSettlementsVisibleForAsync(scenario.OrganizationId));
    }

    [PostgreSqlContractFact]
    public async Task List_is_refused_to_every_role_outside_the_matrix_before_any_settlement_is_read()
    {
        await using var scenario = await SettlementScenario.CreateAsync(fixture);
        await scenario.AddWorkAsync("DELIVERED", "DELIVERED", InPeriod, 4_500);
        var service = CreateService();
        var created = await service.CreateAsync(scenario.Create("create"), default);

        foreach (var actor in new[] { scenario.DispatcherUserId, scenario.DriverUserId, scenario.ViewerUserId, scenario.AdminUserId })
        {
            await Assert.ThrowsAsync<FinanceForbiddenException>(() =>
                service.ListAsync(scenario.List() with { ActorId = actor }, default));
            await Assert.ThrowsAsync<FinanceForbiddenException>(() =>
                service.ListAsync(scenario.List() with { ActorId = actor, Status = "CALCULATED" }, default));
        }

        // PLATFORM_ADMIN lists only with MFA; a suspended FINANCE membership lists nothing at all.
        Assert.Equal(
            [created.Id],
            (await service.ListAsync(scenario.List() with { ActorId = scenario.AdminUserId, MfaSatisfied = true }, default))
                .Items.Select(item => item.Id));
        await scenario.SuspendAsync(scenario.FinanceUserId, "FINANCE");
        await Assert.ThrowsAsync<FinanceForbiddenException>(() => service.ListAsync(scenario.List(), default));

        // A malformed query is refused before any transaction, whoever asks.
        var invalid = await Assert.ThrowsAsync<SettlementConflictException>(() =>
            service.ListAsync(scenario.List() with { ActorId = scenario.ViewerUserId, Status = "SETTLED" }, default));
        Assert.Equal(SettlementConflictCode.InvalidRequest, invalid.Code);
    }

    [PostgreSqlContractFact]
    public async Task List_pages_by_keyset_and_fails_closed_on_an_inconsistent_ledger()
    {
        await using var scenario = await SettlementScenario.CreateAsync(fixture);
        var drafts = new List<Guid>();
        for (var index = 0; index < SettlementListPolicy.PageSize + 2; index++)
        {
            drafts.Add(await scenario.InsertDraftAsync());
        }

        var service = CreateService();
        var first = await service.ListAsync(scenario.List(), default);
        Assert.Equal(SettlementListPolicy.PageSize, first.Items.Count);
        Assert.True(SettlementCursorCodec.TryDecode(first.NextCursor, out var cursor));
        var second = await service.ListAsync(scenario.List() with { Cursor = cursor }, default);
        Assert.Equal(2, second.Items.Count);
        Assert.Null(second.NextCursor);

        var listed = first.Items.Concat(second.Items).ToArray();
        Assert.Equal(drafts.Order(), listed.Select(item => item.Id).Order());
        Assert.Equal(
            listed.OrderByDescending(item => item.CreatedAt).ThenByDescending(item => item.Id.ToString("D"), StringComparer.Ordinal)
                .Select(item => item.Id),
            listed.Select(item => item.Id));

        // The newest settlement is on the first page, so the very first read has to fail closed.
        await scenario.CorruptTotalBypassingTriggersAsync(drafts[^1], 1);
        await Assert.ThrowsAsync<FinanceUnavailableException>(() => service.ListAsync(scenario.List(), default));
    }

    [PostgreSqlContractFact]
    public async Task Settlements_and_drivers_of_another_tenant_are_the_uniform_not_found()
    {
        await using var scenario = await SettlementScenario.CreateAsync(fixture);
        await using var foreign = await SettlementScenario.CreateAsync(fixture);
        await scenario.AddWorkAsync("DELIVERED", "DELIVERED", InPeriod, 4_500);
        await foreign.AddWorkAsync("DELIVERED", "DELIVERED", InPeriod, 9_000);
        var service = CreateService();
        var created = await service.CreateAsync(scenario.Create("create"), default);
        var foreignCreated = await CreateService().CreateAsync(foreign.Create("create"), default);
        Assert.Equal(9_000, foreignCreated.TotalCents);

        foreach (var settlement in new[] { foreignCreated.Id, Guid.NewGuid() })
        {
            await Assert.ThrowsAsync<FinanceNotFoundException>(() => service.GetAsync(scenario.Get(settlement), default));
            await Assert.ThrowsAsync<FinanceNotFoundException>(() => service.ExportCsvAsync(scenario.Get(settlement), default));
            await Assert.ThrowsAsync<FinanceNotFoundException>(() =>
                service.AddAdjustmentAsync(scenario.Adjust(settlement, 1, "Bono", $"adjust-{settlement:N}"), default));
            await Assert.ThrowsAsync<FinanceNotFoundException>(() =>
                service.ApproveAsync(scenario.Transition(settlement, $"approve-{settlement:N}"), default));
            await Assert.ThrowsAsync<FinanceNotFoundException>(() =>
                service.MarkPaidAsync(scenario.Transition(settlement, $"pay-{settlement:N}"), default));
            await Assert.ThrowsAsync<FinanceNotFoundException>(() =>
                service.VoidAsync(scenario.Void(settlement, $"void-{settlement:N}"), default));
        }

        // A foreign driver and a missing driver are the same 404, and neither leaves a settlement behind.
        foreach (var driver in new[] { foreign.DriverId, Guid.NewGuid() })
        {
            await Assert.ThrowsAsync<FinanceNotFoundException>(() =>
                service.CreateAsync(scenario.Create($"driver-{driver:N}") with { DriverId = driver }, default));
        }

        Assert.Equal(1L, await scenario.SettlementCountAsync());
        Assert.Equal(1L, await foreign.SettlementCountAsync());
        Assert.Equal(("CALCULATED", 9_000L, 9_000m, 1L), await foreign.LedgerStateAsync(foreignCreated.Id));
        Assert.Equal(4_500, created.TotalCents);
    }

    [PostgreSqlContractFact]
    public async Task Concurrent_calculations_for_one_driver_pay_every_source_exactly_once()
    {
        await using var scenario = await SettlementScenario.CreateAsync(fixture);
        var work = new List<Work>();
        for (var index = 0; index < 6; index++)
        {
            work.Add(await scenario.AddWorkAsync(
                index % 3 == 0 ? "RETURNED" : "DELIVERED",
                index % 3 == 0 ? "RETURNED" : "DELIVERED",
                InPeriod.AddMinutes(index),
                1_000 + index));
        }

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(index =>
            Task.Run(() => CreateService().CreateAsync(scenario.Create($"race-{index}"), default))));

        Assert.Equal(4L, await scenario.SettlementCountAsync());
        Assert.Equal(
            work.Select(item => item.Source).Order(StringComparer.Ordinal),
            results.SelectMany(result => result.Lines).Select(line => line.SourceReference).Order(StringComparer.Ordinal));
        var paying = Assert.Single(results, result => result.Lines.Count > 0);
        Assert.Equal(work.Sum(item => item.CostCents), paying.TotalCents);
        Assert.All(results.Where(result => result != paying), result => Assert.Equal(0, result.TotalCents));
    }

    [PostgreSqlContractFact]
    public async Task Racing_lifecycle_steps_serialize_on_the_settlement_row()
    {
        await using var scenario = await SettlementScenario.CreateAsync(fixture);
        await scenario.AddWorkAsync("DELIVERED", "DELIVERED", InPeriod, 4_500);
        var created = await CreateService().CreateAsync(scenario.Create("create"), default);

        var outcomes = await Task.WhenAll(
            Outcome(() => CreateService().ApproveAsync(scenario.Transition(created.Id, "approve-a"), default)),
            Outcome(() => CreateService().ApproveAsync(scenario.Transition(created.Id, "approve-b"), default)),
            Outcome(() => CreateService().VoidAsync(scenario.Void(created.Id, "void-c"), default)));

        // VOID is admitted from both CALCULATED and APPROVED, so the void always wins; at most one approval
        // wins, and every loser sees a state conflict, never a torn write.
        Assert.Equal("VOID", outcomes[2].Result?.Status);
        var approvals = outcomes.Take(2).ToArray();
        Assert.True(approvals.Count(outcome => outcome.Result is not null) <= 1);
        Assert.All(
            approvals.Where(outcome => outcome.Result is null),
            outcome => Assert.Equal(SettlementConflictCode.SettlementStateConflict, outcome.Conflict));
        Assert.Equal(("VOID", 4_500L, 4_500m, 1L), await scenario.LedgerStateAsync(created.Id));
        Assert.Equal(
            outcomes.Count(outcome => outcome.Result is not null),
            (await scenario.AuditActionsAsync(created.Id)).Count - 1);
    }

    [PostgreSqlContractFact]
    public async Task A_failure_before_commit_leaves_no_settlement_line_audit_or_idempotency_record()
    {
        await using var scenario = await SettlementScenario.CreateAsync(fixture);
        await scenario.AddWorkAsync("DELIVERED", "DELIVERED", InPeriod, 4_500);
        var failing = CreateService(new ThrowingFailureInjector(
            FinanceTransactionStage.BeforeCommit,
            new NpgsqlException("Simulated store failure before commit.")));

        var failure = await Assert.ThrowsAsync<FinanceUnavailableException>(() =>
            failing.CreateAsync(scenario.Create("failure"), default));
        Assert.IsType<NpgsqlException>(failure.InnerException);
        Assert.Equal(0L, await scenario.SettlementCountAsync());
        Assert.Equal(0L, await scenario.IdempotencyCountAsync(PostgreSqlSettlementService.CreateIdempotencyScope));
        Assert.Empty(await scenario.AllAuditActionsAsync());

        // The sources were never claimed, so a retry calculates them.
        var retried = await CreateService().CreateAsync(scenario.Create("failure"), default);
        Assert.Equal(4_500, retried.TotalCents);
    }

    private static async Task AssertBlockedAsync(
        PostgreSqlSettlementService service,
        SettlementScenario scenario,
        Guid settlementId,
        string key,
        SettlementConflictCode expected)
    {
        var blocked = await Assert.ThrowsAsync<SettlementConflictException>(() =>
            service.ApproveAsync(scenario.Transition(settlementId, key), default));
        Assert.Equal(expected, blocked.Code);
        Assert.Equal("CALCULATED", (await scenario.LedgerStateAsync(settlementId)).Status);
        Assert.Equal(0L, await scenario.IdempotencyCountAsync(PostgreSqlSettlementService.ApproveIdempotencyScope));
    }

    private static async Task AssertStateConflictAsync(Func<Task> operation)
    {
        var conflict = await Assert.ThrowsAsync<SettlementConflictException>(operation);
        Assert.Equal(SettlementConflictCode.SettlementStateConflict, conflict.Code);
    }

    private static async Task<(SettlementResult? Result, SettlementConflictCode? Conflict)> Outcome(
        Func<Task<SettlementResult>> operation)
    {
        try
        {
            return (await Task.Run(operation), null);
        }
        catch (SettlementConflictException conflict)
        {
            return (null, conflict.Code);
        }
    }

    private static string Json(SettlementResult result) => JsonSerializer.Serialize(result);

    private PostgreSqlSettlementService CreateService(IFinanceFailureInjector? failureInjector = null)
    {
        var state = new TenantDatabaseExecutionState();
        var financeOptions = Options.Create(new FinanceOptions { Provider = FinanceProviderKind.PostgreSql });
        var options = new DbContextOptionsBuilder<FinanceDbContext>()
            .UseNpgsql(fixture.AppDataSource, postgres => postgres.EnableRetryOnFailure())
            .AddInterceptors(
                new TenantTransactionGuardInterceptor(state),
                new TenantSaveChangesGuardInterceptor(state))
            .Options;
        var gateway = new FinanceTenantGateway(
            new TenantTransactionContext<FinanceDbContext>(new FinanceDbContext(options, state), state),
            financeOptions);
        return new(
            gateway,
            new PostgreSqlAppendOnlyAuditWriter(state),
            new AuditPayloadRedactor(),
            failureInjector ?? new NoOpFinanceFailureInjector(),
            new FixedClock(Now),
            financeOptions);
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock { public DateTimeOffset UtcNow { get; } = now; }

    private sealed class ThrowingFailureInjector(FinanceTransactionStage stage, Exception failure)
        : IFinanceFailureInjector
    {
        public Task OnStageAsync(FinanceTransactionStage current, CancellationToken cancellationToken) =>
            current == stage ? Task.FromException(failure) : Task.CompletedTask;
    }

    private sealed record Work(Guid OrderId, Guid AssignmentId, long CostCents)
    {
        public string Source => $"dispatch.assignments/{AssignmentId:D}";
    }

    private sealed record AuditRow(string Action, string EntityType, Guid EntityId, string Payload);

    /// <summary>
    /// One tenant with a FINANCE member, a PLATFORM_ADMIN, a DISPATCHER (the base scenario's user), a
    /// VIEWER, the DRIVER being settled and a second driver, plus helpers that seed the canonical rows a
    /// settlement derives from exactly as ORD-002, DSP-002, FIN-001 and INC-001 leave them.
    /// </summary>
    private sealed class SettlementScenario : IAsyncDisposable
    {
        private readonly PostgreSqlContractFixture fixture;
        private readonly SyntheticOrderScenario tenant;

        private SettlementScenario(PostgreSqlContractFixture fixture)
        {
            this.fixture = fixture;
            tenant = new SyntheticOrderScenario(fixture);
        }

        public Guid OrganizationId => tenant.OrganizationId;
        public Guid DispatcherUserId => tenant.UserId;
        public Guid FinanceUserId { get; } = Guid.NewGuid();
        public Guid AdminUserId { get; } = Guid.NewGuid();
        public Guid ViewerUserId { get; } = Guid.NewGuid();
        public Guid DriverUserId { get; } = Guid.NewGuid();
        public Guid OtherDriverUserId { get; } = Guid.NewGuid();
        public Guid DriverId { get; } = Guid.NewGuid();
        public Guid OtherDriverId { get; } = Guid.NewGuid();

        private Guid[] ExtraUsers => [FinanceUserId, AdminUserId, ViewerUserId, DriverUserId, OtherDriverUserId];

        public static async Task<SettlementScenario> CreateAsync(PostgreSqlContractFixture fixture)
        {
            var value = new SettlementScenario(fixture);
            await value.tenant.InitializeAsync("READY_FOR_PICKUP", "USED", createOrder: false);
            await value.ExecuteAsync(
                """
                INSERT INTO identity.users(id,identity_subject,status) VALUES
                  (@finance,'set001-finance-' || @finance::text,'ACTIVE'),
                  (@admin,'set001-admin-' || @admin::text,'ACTIVE'),
                  (@viewer,'set001-viewer-' || @viewer::text,'ACTIVE'),
                  (@driver_user,'set001-driver-' || @driver_user::text,'ACTIVE'),
                  (@other_driver_user,'set001-driver-' || @other_driver_user::text,'ACTIVE');
                INSERT INTO organizations.organization_memberships(id,user_id,organization_id,role,status,is_default) VALUES
                  (gen_random_uuid(),@finance,@org,'FINANCE','ACTIVE',true),
                  (gen_random_uuid(),@admin,@org,'PLATFORM_ADMIN','ACTIVE',true),
                  (gen_random_uuid(),@viewer,@org,'VIEWER','ACTIVE',true),
                  (gen_random_uuid(),@driver_user,@org,'DRIVER','ACTIVE',true),
                  (gen_random_uuid(),@other_driver_user,@org,'DRIVER','ACTIVE',true);
                INSERT INTO drivers.driver_profiles(id,user_id,org_id,home_city_id,driver_type,vehicle_type,status) VALUES
                  (@driver,@driver_user,@org,@city,'OWN','MOTORCYCLE','ACTIVE'),
                  (@other_driver,@other_driver_user,@org,@city,'EXTERNAL','CAR','ACTIVE');
                """,
                P("finance", value.FinanceUserId),
                P("admin", value.AdminUserId),
                P("viewer", value.ViewerUserId),
                P("driver_user", value.DriverUserId),
                P("other_driver_user", value.OtherDriverUserId),
                P("org", value.OrganizationId),
                P("driver", value.DriverId),
                P("other_driver", value.OtherDriverId),
                P("city", value.tenant.CityId));
            return value;
        }

        public CreateSettlementCommand Create(string key) => new(
            FinanceUserId, OrganizationId, Key(key), DriverId, PeriodFrom, PeriodTo, false, key);

        public GetSettlementQuery Get(Guid settlementId) => new(FinanceUserId, OrganizationId, settlementId, false);

        public ListSettlementsQuery List() => new(FinanceUserId, OrganizationId, null, null, null, null, false);

        /// <summary>How many settlements of <paramref name="ownerOrganizationId"/> RLS lets this tenant see.</summary>
        public async Task<long> CountSettlementsVisibleForAsync(Guid ownerOrganizationId)
        {
            await using var tx = await TenantAsync();
            await using var command = new NpgsqlCommand(
                "SELECT count(*) FROM finance.settlements WHERE owner_org_id=@owner",
                tx.Connection,
                tx.Transaction);
            command.Parameters.AddWithValue("owner", ownerOrganizationId);
            return (long)(await command.ExecuteScalarAsync())!;
        }

        public AddSettlementAdjustmentCommand Adjust(Guid settlementId, long amount, string reason, string key) => new(
            FinanceUserId, OrganizationId, Key(key), settlementId, amount, reason, false, key);

        /// <summary>
        /// An approve or pay step by FINANCE with a satisfied MFA challenge, which D7-SETTLEMENT-MFA requires of
        /// every permitted role.
        /// </summary>
        public SettlementTransitionCommand Transition(Guid settlementId, string key) => new(
            FinanceUserId, OrganizationId, Key(key), settlementId, true, key);

        public VoidSettlementCommand Void(Guid settlementId, string key) => new(
            FinanceUserId, OrganizationId, Key(key), settlementId, "Liquidación calculada con periodo incorrecto", false, key);

        private static string Key(string logicalKey) => $"set001-contract-{logicalKey}";

        /// <summary>
        /// One order with one assignment of <paramref name="driverId"/> (the settled driver by default), in
        /// <paramref name="orderStatus"/>, and — when <paramref name="outcome"/> is set — the ORD-002 status change
        /// event that recorded that outcome at <paramref name="occurredAt"/>.
        /// </summary>
        public async Task<Work> AddWorkAsync(
            string orderStatus,
            string? outcome,
            DateTimeOffset occurredAt,
            long costCents,
            string assignmentType = "OWN",
            string assignmentStatus = "ACTIVE",
            long codExpected = 0,
            Guid? driverId = null)
        {
            var order = Guid.NewGuid();
            var assignment = Guid.NewGuid();
            await ExecuteAsync(
                """
                INSERT INTO pricing.quotes(
                  id,owner_org_id,city_id,origin_location_id,destination_location_id,service_type,pricing_tier,
                  consolidated_route,subtotal_cents,discount_cents,tax_cents,total_cents,
                  minimum_total_cents_snapshot,currency,pricing_policy_version,request_snapshot_redacted,
                  package_snapshot,breakdown,input_hash,status,expires_at)
                VALUES (@quote,@org,@city,@origin,@destination,'SAME_DAY','OCCASIONAL',false,
                  12000,0,0,12000,0,'MXN','set-001-price-snapshot-v1','{}','[{"weight_grams":500}]','{}',
                  decode(repeat('03',32),'hex'),'USED',@expires);
                INSERT INTO orders.orders(
                  id,public_id,quote_id,owner_org_id,city_id,origin_location_id,destination_location_id,
                  service_type,pricing_tier,consolidated_route,payer_type,status,subtotal_cents,discount_cents,
                  tax_cents,total_cents,minimum_total_cents_snapshot,currency,pricing_policy_version,
                  package_snapshot,financial_override,cod_expected_cents,version)
                VALUES (@order,@public_id,@quote,@org,@city,@origin,@destination,'SAME_DAY','OCCASIONAL',false,
                  'SENDER',@status,12000,0,0,12000,0,'MXN','set-001-price-snapshot-v1',
                  '[{"weight_grams":500}]',NULL,@cod,2);
                INSERT INTO dispatch.assignments(
                  id,order_id,owner_org_id,driver_id,route_id,assignment_type,status,cost_cents,accepted_at,created_at)
                VALUES (@assignment,@order,@org,@driver,NULL,@assignment_type,@assignment_status,@cost,@accepted,@accepted);
                """,
                P("quote", Guid.NewGuid()),
                P("order", order),
                P("public_id", $"SET001-{order:N}"),
                P("org", OrganizationId),
                P("city", tenant.CityId),
                P("origin", tenant.OriginLocationId),
                P("destination", tenant.DestinationLocationId),
                P("status", orderStatus),
                P("cod", codExpected),
                P("assignment", assignment),
                P("driver", driverId ?? DriverId),
                P("assignment_type", assignmentType),
                P("assignment_status", assignmentStatus),
                P("cost", costCents),
                P("accepted", occurredAt.AddHours(-3)),
                P("expires", Now.AddDays(30)));
            if (outcome is not null)
            {
                await ExecuteAsync(
                    """
                    INSERT INTO orders.order_events(
                      id,order_id,owner_org_id,operator_org_id,aggregate_version,event_type,public_event_code,
                      payload,actor_id,occurred_at)
                    VALUES (gen_random_uuid(),@order,@org,NULL,2,'ORDER_STATUS_CHANGED',@outcome,
                      jsonb_build_object('previous_status',@previous,'new_status',@outcome,'reason_redacted',''),
                      NULL,@occurred);
                    """,
                    P("order", order),
                    P("org", OrganizationId),
                    P("outcome", outcome),
                    P("previous", outcome == "DELIVERED" ? "DELIVERING" : "RETURNING"),
                    P("occurred", occurredAt));
            }

            return new(order, assignment, costCents);
        }

        public Task SetCodAsync(Guid orderId, long amount, string status) => ExecuteAsync(
            """
            INSERT INTO finance.cod_transactions(
              id,order_id,owner_org_id,amount_cents,status,collected_by_driver_id,recorded_at,reconciled_at,reference)
            VALUES (gen_random_uuid(),@order,@org,@amount,@status,@driver,@now,
              CASE WHEN @status='RECONCILED' THEN @now END,'set001-cash')
            ON CONFLICT (order_id) DO UPDATE
              SET status=EXCLUDED.status,reconciled_at=EXCLUDED.reconciled_at,amount_cents=EXCLUDED.amount_cents;
            """,
            P("order", orderId),
            P("org", OrganizationId),
            P("amount", amount),
            P("status", status),
            P("driver", DriverId),
            P("now", Now));

        /// <summary>An incident with its required evidence, seeded in one transaction as INC-001 requires.</summary>
        public Task AddIncidentAsync(Guid orderId, string status)
        {
            var upload = Guid.NewGuid();
            return ExecuteInTransactionAsync(
                """
                INSERT INTO custody.proof_upload_sessions(
                  id,order_id,owner_org_id,requested_by,object_key_quarantine,
                  expected_content_type,maximum_bytes,status,expires_at)
                VALUES (@upload,@order,@org,@actor,'quarantine/' || @upload::text,'image/jpeg',1024,'READY',@later);
                INSERT INTO custody.proofs(
                  id,order_id,owner_org_id,upload_session_id,proof_type,object_key,sha256,
                  content_type,size_bytes,captured_at,created_by)
                VALUES (@proof,@order,@org,@upload,'DELIVERY_PHOTO','proofs/' || @upload::text,
                  decode(repeat('04',32),'hex'),'image/jpeg',100,@now,@actor);
                INSERT INTO incidents.incidents(
                  id,order_id,owner_org_id,incident_type,severity,status,custody_acquired,
                  description_ciphertext,pii_key_version,reason_code,next_action,occurred_at,sla_due_at,created_by,
                  resolved_at)
                VALUES (@incident,@order,@org,'FAILED_DELIVERY_ATTEMPT','MEDIUM',@status,true,
                  @ciphertext,'set001-v1','RECIPIENT_ABSENT','RESCHEDULED',@now,@later,@actor,
                  CASE WHEN @status IN ('RESOLVED','REJECTED') THEN @now END);
                INSERT INTO incidents.incident_evidence(id,incident_id,order_id,owner_org_id,proof_id,created_by)
                VALUES (gen_random_uuid(),@incident,@order,@org,@proof,@actor);
                """,
                P("upload", upload),
                P("proof", Guid.NewGuid()),
                P("incident", Guid.NewGuid()),
                P("order", orderId),
                P("org", OrganizationId),
                P("actor", DispatcherUserId),
                P("status", status),
                P("ciphertext", RandomNumberGenerator.GetBytes(48)),
                P("now", Now),
                P("later", Now.AddDays(1)));
        }

        public Task SetOrderStatusAsync(Guid orderId, string status) => ExecuteAsync(
            "UPDATE orders.orders SET status=@status WHERE id=@order;", P("status", status), P("order", orderId));

        public Task SetAssignmentCostAsync(Guid assignmentId, long cost) => ExecuteAsync(
            "UPDATE dispatch.assignments SET cost_cents=@cost WHERE id=@assignment;",
            P("cost", cost), P("assignment", assignmentId));

        public Task GrantAsync(Guid userId, string role) => ExecuteAsync(
            """
            INSERT INTO organizations.organization_memberships(id,user_id,organization_id,role,status,is_default)
            VALUES (gen_random_uuid(),@user,@org,@role,'ACTIVE',false);
            """,
            P("user", userId), P("org", OrganizationId), P("role", role));

        public Task SuspendAsync(Guid userId, string role) => ExecuteAsync(
            """
            UPDATE organizations.organization_memberships SET status='SUSPENDED'
            WHERE user_id=@user AND organization_id=@org AND role=@role;
            """,
            P("user", userId), P("org", OrganizationId), P("role", role));

        /// <summary>A DRAFT header written the way the ledger admits one, on the runtime role.</summary>
        public async Task<Guid> InsertDraftAsync()
        {
            var settlement = Guid.NewGuid();
            await using var tx = await TenantAsync();
            await using (var command = new NpgsqlCommand(
                """
                INSERT INTO finance.settlements(id,owner_org_id,payee_type,payee_id,status,total_cents,period_from,period_to)
                VALUES (@id,@org,'DRIVER',@driver,'DRAFT',0,@from,@to)
                """,
                tx.Connection,
                tx.Transaction))
            {
                command.Parameters.AddWithValue("id", settlement);
                command.Parameters.AddWithValue("org", OrganizationId);
                command.Parameters.AddWithValue("driver", DriverId);
                command.Parameters.AddWithValue("from", PeriodFrom);
                command.Parameters.AddWithValue("to", PeriodTo);
                await command.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();
            return settlement;
        }

        public async Task<PostgresException> InsertDuplicateSourceAsync(Guid settlementId, string source)
        {
            // Only a DRAFT admits economic lines, so the claim is tried on a fresh DRAFT of this tenant.
            var draft = await InsertDraftAsync();
            await using var tx = await TenantAsync();
            await using var command = new NpgsqlCommand(
                """
                INSERT INTO finance.settlement_lines(id,settlement_id,owner_org_id,order_id,line_type,amount_cents,source_reference)
                SELECT gen_random_uuid(),@draft,@org,l.order_id,'DELIVERY',0,@source
                FROM finance.settlement_lines l WHERE l.settlement_id=@settlement AND l.source_reference=@source
                """,
                tx.Connection,
                tx.Transaction);
            command.Parameters.AddWithValue("draft", draft);
            command.Parameters.AddWithValue("org", OrganizationId);
            command.Parameters.AddWithValue("source", source);
            command.Parameters.AddWithValue("settlement", settlementId);
            return await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        }

        public async Task<PostgresException> UpdateTotalDirectlyAsync(Guid settlementId, long total)
        {
            await using var tx = await TenantAsync();
            await using var command = new NpgsqlCommand(
                "UPDATE finance.settlements SET total_cents=@total WHERE id=@id",
                tx.Connection,
                tx.Transaction);
            command.Parameters.AddWithValue("total", total);
            command.Parameters.AddWithValue("id", settlementId);
            return await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        }

        public Task CorruptTotalBypassingTriggersAsync(Guid settlementId, long total) => ExecuteInTransactionAsync(
            """
            SET LOCAL session_replication_role = replica;
            UPDATE finance.settlements SET total_cents=@total WHERE id=@id;
            """,
            P("total", total),
            P("id", settlementId));

        public async Task<(string Status, long Total, decimal LineSum, long Lines)> LedgerStateAsync(Guid settlementId)
        {
            await using var command = fixture.AdminDataSource.CreateCommand(
                """
                SELECT s.status,s.total_cents,
                       (SELECT COALESCE(sum(l.amount_cents),0) FROM finance.settlement_lines l WHERE l.settlement_id=s.id),
                       (SELECT count(*) FROM finance.settlement_lines l WHERE l.settlement_id=s.id)
                FROM finance.settlements s WHERE s.id=@id
                """);
            command.Parameters.AddWithValue("id", settlementId);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return (reader.GetString(0), reader.GetInt64(1), reader.GetDecimal(2), reader.GetInt64(3));
        }

        public async Task<long> SettlementCountAsync()
        {
            await using var command = fixture.AdminDataSource.CreateCommand(
                "SELECT count(*) FROM finance.settlements WHERE owner_org_id=@org");
            command.Parameters.AddWithValue("org", OrganizationId);
            return (long)(await command.ExecuteScalarAsync())!;
        }

        public async Task<long> IdempotencyCountAsync(string scope)
        {
            await using var command = fixture.AdminDataSource.CreateCommand(
                """
                SELECT count(*) FROM platform.idempotency_keys
                WHERE owner_org_id=@org AND scope=@scope AND response_status IS NOT NULL
                """);
            command.Parameters.AddWithValue("org", OrganizationId);
            command.Parameters.AddWithValue("scope", scope);
            return (long)(await command.ExecuteScalarAsync())!;
        }

        public async Task<List<string>> AuditActionsAsync(Guid settlementId)
        {
            await using var command = fixture.AdminDataSource.CreateCommand(
                """
                SELECT action FROM platform.audit_logs
                WHERE org_id=@org AND entity_type='Settlement' AND entity_id=@id
                ORDER BY occurred_at,action
                """);
            command.Parameters.AddWithValue("org", OrganizationId);
            command.Parameters.AddWithValue("id", settlementId);
            return await ReadStringsAsync(command);
        }

        public async Task<List<string>> AllAuditActionsAsync()
        {
            await using var command = fixture.AdminDataSource.CreateCommand(
                "SELECT action FROM platform.audit_logs WHERE org_id=@org AND action LIKE 'finance.settlement.%'");
            command.Parameters.AddWithValue("org", OrganizationId);
            return await ReadStringsAsync(command);
        }

        public async Task<List<Guid>> AuditIdsAsync(Guid settlementId, string action)
        {
            await using var command = fixture.AdminDataSource.CreateCommand(
                "SELECT id FROM platform.audit_logs WHERE org_id=@org AND entity_id=@id AND action=@action");
            command.Parameters.AddWithValue("org", OrganizationId);
            command.Parameters.AddWithValue("id", settlementId);
            command.Parameters.AddWithValue("action", action);
            var ids = new List<Guid>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                ids.Add(reader.GetGuid(0));
            }

            return ids;
        }

        public async Task<AuditRow> AuditAsync(Guid auditId)
        {
            await using var command = fixture.AdminDataSource.CreateCommand(
                "SELECT action,entity_type,entity_id,payload_redacted::text FROM platform.audit_logs WHERE id=@id AND org_id=@org");
            command.Parameters.AddWithValue("id", auditId);
            command.Parameters.AddWithValue("org", OrganizationId);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync(), $"Audit entry {auditId} does not exist.");
            return new(reader.GetString(0), reader.GetString(1), reader.GetGuid(2), reader.GetString(3));
        }

        private static async Task<List<string>> ReadStringsAsync(NpgsqlCommand command)
        {
            var values = new List<string>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                values.Add(reader.GetString(0));
            }

            return values;
        }

        private Task<TenantTransaction> TenantAsync() => TenantTransaction.BeginAsync(
            fixture.AppDataSource, "paqueteria_app", FinanceUserId, [OrganizationId]);

        private Task ExecuteAsync(string sql, params NpgsqlParameter[] parameters) =>
            tenant.ExecuteAdminAsync(sql, parameters);

        /// <summary>Runs <paramref name="sql"/> as the administrator inside one explicit transaction.</summary>
        private async Task ExecuteInTransactionAsync(string sql, params NpgsqlParameter[] parameters)
        {
            await using var connection = await fixture.AdminDataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await using (var command = new NpgsqlCommand(sql, connection, transaction))
            {
                command.Parameters.AddRange(parameters);
                await command.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }

        public async ValueTask DisposeAsync()
        {
            // Settlement rows reference orders and only the migration lane may remove them.
            await using (var migrator = await TenantTransaction.BeginAsync(
                fixture.AdminDataSource, "paqueteria_migrator", FinanceUserId, [OrganizationId]))
            {
                await using var command = new NpgsqlCommand(
                    """
                    DELETE FROM finance.settlement_lines WHERE owner_org_id=@org;
                    DELETE FROM finance.settlements WHERE owner_org_id=@org;
                    """,
                    migrator.Connection,
                    migrator.Transaction);
                command.Parameters.AddWithValue("org", OrganizationId);
                await command.ExecuteNonQueryAsync();
                await migrator.CommitAsync();
            }

            await tenant.DisposeAsync();
            await ExecuteAsync("DELETE FROM identity.users WHERE id = ANY(@users);", P("users", ExtraUsers));
        }

        private static NpgsqlParameter P(string name, object value) => new(name, value);
    }
}
