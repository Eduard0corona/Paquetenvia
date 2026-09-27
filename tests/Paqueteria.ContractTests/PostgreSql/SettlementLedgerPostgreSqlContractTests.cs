using System.Data;
using Npgsql;
using NpgsqlTypes;
using Paqueteria.ContractTests.PostgreSql.Fixtures;

namespace Paqueteria.ContractTests.PostgreSql;

/// <summary>
/// SET-001 Slice 1: the physical guarantees underneath the settlement vertical. Every write goes
/// through a real runtime role inside a tenant transaction and under FORCE ROW LEVEL SECURITY, so
/// what holds here is what the database enforces on its own, without any settlement application code.
/// </summary>
[Collection(PostgreSqlContractCollection.Name)]
[Trait("Category", "PostgreSqlContract")]
public sealed class SettlementLedgerPostgreSqlContractTests(PostgreSqlContractFixture fixture)
{
    private static readonly string[] Statuses = ["DRAFT", "CALCULATED", "APPROVED", "PAID", "VOID"];
    private static readonly DateOnly PeriodFrom = new(2026, 9, 14);
    private static readonly DateOnly PeriodTo = new(2026, 9, 20);

    [PostgreSqlContractFact]
    public async Task Ledger_construction_reconciles_at_commit_through_the_whole_lifecycle()
    {
        await using var ledger = await LedgerScenario.CreateAsync(fixture);
        var settlement = Guid.NewGuid();

        // One transaction builds the ledger the way SET-001 will: an empty DRAFT header, its lines, the
        // total and the calculation. The reconciliation is deferred, so nothing fails before commit.
        await using (var tx = await ledger.TenantAsync())
        {
            await ledger.CreateDraftAsync(tx, settlement);
            await ledger.AddLineAsync(tx, settlement, "DELIVERY", 12_000, Assignment());
            await ledger.AddLineAsync(tx, settlement, "RETURN", 3_000, Assignment());
            await ledger.AddLineAsync(tx, settlement, "DELIVERY", 0, Assignment());
            await ledger.AddLineAsync(tx, settlement, "ADJUSTMENT", -500, AuditEntry());
            await SetAsync(tx, settlement, "DRAFT", 14_500);
            await SetAsync(tx, settlement, "CALCULATED", 14_500);
            await tx.CommitAsync();
        }

        // A later adjustment moves the CALCULATED total; approval freezes it and payment keeps it.
        await using (var tx = await ledger.TenantAsync())
        {
            await ledger.AddLineAsync(tx, settlement, "ADJUSTMENT", 750, AuditEntry());
            await SetAsync(tx, settlement, "CALCULATED", 15_250);
            await tx.CommitAsync();
        }

        foreach (var status in new[] { "APPROVED", "PAID" })
        {
            await using var tx = await ledger.TenantAsync();
            await SetAsync(tx, settlement, status, 15_250);
            await tx.CommitAsync();
        }

        Assert.Equal(("PAID", 15_250L, 15_250m, 5L), await ledger.StateAsync(settlement));
    }

    [PostgreSqlContractFact]
    public async Task Commit_is_refused_whenever_a_total_differs_from_its_line_sum()
    {
        await using var ledger = await LedgerScenario.CreateAsync(fixture);

        var cases = new (string Name, (string Type, long Amount)[] Lines, long? Total, bool Reconciles)[]
        {
            ("lines without a total", [("DELIVERY", 1_000)], null, false),
            ("a total one cent short", [("DELIVERY", 1_000)], 999, false),
            ("a total without lines", [], 1, false),
            ("an adjustment counted without its sign", [("DELIVERY", 1_000), ("ADJUSTMENT", -1_000)], 2_000, false),
            ("a signed adjustment counted exactly", [("DELIVERY", 1_000), ("ADJUSTMENT", -1_000)], 0, true),
            ("an empty settlement at zero", [], 0, true),
        };

        foreach (var (name, lines, total, reconciles) in cases)
        {
            var settlement = Guid.NewGuid();
            await using var tx = await ledger.TenantAsync();
            await ledger.CreateDraftAsync(tx, settlement);
            foreach (var (type, amount) in lines)
            {
                await ledger.AddLineAsync(tx, settlement, type, amount, type == "ADJUSTMENT" ? AuditEntry() : Assignment());
            }

            if (total is { } value)
            {
                await SetAsync(tx, settlement, "DRAFT", value);
            }

            if (reconciles)
            {
                await tx.CommitAsync();
                Assert.Equal(1L, await ledger.CountAsync(settlement));
            }
            else
            {
                var error = await RejectedAsync(() => tx.CommitAsync(), "23514");
                Assert.Contains("does not equal the sum", error.MessageText, StringComparison.Ordinal);
                Assert.True(0L == await ledger.CountAsync(settlement), $"{name} was persisted.");
            }
        }

        // The invariant is re-evaluated whenever either side moves after the ledger was built.
        var calculated = await ledger.CommittedAsync("CALCULATED", ("DELIVERY", 500));
        await using (var tx = await ledger.TenantAsync())
        {
            await SetAsync(tx, calculated, "CALCULATED", 600);
            await RejectedAsync(() => tx.CommitAsync(), "23514");
        }

        await using (var tx = await ledger.TenantAsync())
        {
            await ledger.AddLineAsync(tx, calculated, "ADJUSTMENT", 100, AuditEntry());
            await RejectedAsync(() => tx.CommitAsync(), "23514");
        }

        await using (var tx = await ledger.TenantAsync())
        {
            await ledger.AddLineAsync(tx, calculated, "ADJUSTMENT", 100, AuditEntry());
            await SetAsync(tx, calculated, "CALCULATED", 600);
            await tx.CommitAsync();
        }

        Assert.Equal(("CALCULATED", 600L, 600m, 2L), await ledger.StateAsync(calculated));
    }

    [PostgreSqlContractFact]
    public async Task Line_sums_keep_checked_bigint_semantics()
    {
        await using var ledger = await LedgerScenario.CreateAsync(fixture);
        var settlement = await ledger.CommittedAsync("DRAFT", ("DELIVERY", long.MaxValue));

        // One more cent would wrap a bigint sum to long.MinValue. The ledger sums in numeric, so neither
        // the stale total nor the wrapped one reconciles.
        foreach (var total in new[] { long.MaxValue, long.MinValue })
        {
            await using var tx = await ledger.TenantAsync();
            await ledger.AddLineAsync(tx, settlement, "DELIVERY", 1, Assignment());
            await SetAsync(tx, settlement, "DRAFT", total);
            var error = await RejectedAsync(() => tx.CommitAsync(), "23514");
            Assert.Contains("9223372036854775808", error.MessageText, StringComparison.Ordinal);
        }

        // The total itself is a checked bigint.
        await using (var tx = await ledger.TenantAsync())
        {
            await RejectedAsync(
                () => ExecuteAsync(tx, "UPDATE finance.settlements SET total_cents=total_cents+1 WHERE id=@id",
                    P("id", settlement)),
                "22003");
        }

        Assert.Equal(("DRAFT", long.MaxValue, (decimal)long.MaxValue, 1L), await ledger.StateAsync(settlement));
    }

    [PostgreSqlContractFact]
    public async Task Settlement_lines_are_append_only_for_runtime_roles()
    {
        await using var ledger = await LedgerScenario.CreateAsync(fixture);
        var settlement = await ledger.CommittedAsync("DRAFT", ("DELIVERY", 1_000));

        foreach (var begin in new Func<Task<TenantTransaction>>[] { ledger.TenantAsync, ledger.WorkerAsync })
        {
            foreach (var statement in new[]
            {
                "UPDATE finance.settlement_lines SET amount_cents=amount_cents+1 WHERE settlement_id=@id",
                "UPDATE finance.settlement_lines SET source_reference='dispatch.assignments/other' WHERE settlement_id=@id",
                "DELETE FROM finance.settlement_lines WHERE settlement_id=@id",
            })
            {
                await using var tx = await begin();
                var error = await RejectedAsync(() => ExecuteAsync(tx, statement, P("id", settlement)), "42501");
                Assert.Contains("finance.settlement_lines is append-only", error.MessageText, StringComparison.Ordinal);
            }
        }

        // The rejection is the canonical platform guard, whose one exemption is the migration lane: it
        // can still evolve the table in a controlled way, which SET-001 does not take away.
        Assert.Equal("platform.reject_runtime_mutation", await AdminScalarAsync<string>("""
            SELECT tgfoid::regproc::text FROM pg_trigger
            WHERE tgrelid='finance.settlement_lines'::regclass AND tgname='settlement_lines_append_only'
            """));
        await using (var migrator = await ledger.MigratorAsync())
        {
            Assert.Equal(1, await ExecuteAsync(migrator,
                "UPDATE finance.settlement_lines SET source_reference=source_reference WHERE settlement_id=@id",
                P("id", settlement)));
            await ExecuteAsync(migrator, "SET CONSTRAINTS ALL IMMEDIATE");
            await migrator.RollbackAsync();
        }

        Assert.Equal(("DRAFT", 1_000L, 1_000m, 1L), await ledger.StateAsync(settlement));
    }

    [PostgreSqlContractFact]
    public async Task Settlement_headers_are_born_empty_and_never_deleted_or_rescoped_at_runtime()
    {
        await using var ledger = await LedgerScenario.CreateAsync(fixture);
        var settlement = await ledger.CommittedAsync("DRAFT");

        foreach (var begin in new Func<Task<TenantTransaction>>[] { ledger.TenantAsync, ledger.WorkerAsync })
        {
            await using var tx = await begin();
            var error = await RejectedAsync(
                () => ExecuteAsync(tx, "DELETE FROM finance.settlements WHERE id=@id", P("id", settlement)),
                "42501");
            Assert.Contains("finance.settlements is append-only", error.MessageText, StringComparison.Ordinal);
        }

        foreach (var assignment in new[]
        {
            "id=gen_random_uuid()",
            "owner_org_id=@foreign",
            "payee_type='ALLY'",
            "payee_id=gen_random_uuid()",
            "period_from=period_from-1",
            "period_to=period_to+1",
            "created_at=created_at-interval '1 day'",
        })
        {
            // A session acting for both organizations reaches the guard for the owner change too.
            await using var tx = await ledger.BothTenantsAsync();
            var parameters = assignment.Contains("@foreign", StringComparison.Ordinal)
                ? new[] { P("id", settlement), P("foreign", ledger.ForeignOrganizationId) }
                : new[] { P("id", settlement) };
            var error = await RejectedAsync(
                () => ExecuteAsync(tx, $"UPDATE finance.settlements SET {assignment} WHERE id=@id", parameters),
                "23514");
            Assert.Contains("identity and scope are immutable", error.MessageText, StringComparison.Ordinal);
        }

        foreach (var (status, total) in new[]
        {
            ("CALCULATED", 0L), ("APPROVED", 0L), ("PAID", 0L), ("VOID", 0L), ("DRAFT", 1L), ("DRAFT", -1L),
        })
        {
            await using var tx = await ledger.TenantAsync();
            var error = await RejectedAsync(
                () => ExecuteAsync(tx, """
                    INSERT INTO finance.settlements(owner_org_id,payee_type,payee_id,status,total_cents,period_from,period_to)
                    VALUES (@org,'DRIVER',@payee,@status,@total,@from,@to)
                    """,
                    P("org", ledger.OrganizationId), P("payee", ledger.DriverId), P("status", status),
                    P("total", total), P("from", PeriodFrom), P("to", PeriodTo)),
                "23514");
            Assert.Contains("created as DRAFT with total_cents 0", error.MessageText, StringComparison.Ordinal);
        }

        Assert.Equal(("DRAFT", 0L, 0m, 0L), await ledger.StateAsync(settlement));
    }

    [PostgreSqlContractFact]
    public async Task Settlement_status_follows_the_closed_lifecycle_and_PAID_and_VOID_are_terminal()
    {
        await using var ledger = await LedgerScenario.CreateAsync(fixture);
        var origins = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var status in Statuses)
        {
            origins[status] = await ledger.CommittedAsync(status);
        }

        var allowed = new List<string>();
        foreach (var from in Statuses)
        {
            foreach (var to in Statuses)
            {
                await using var tx = await ledger.TenantAsync();
                try
                {
                    await ExecuteAsync(tx, "UPDATE finance.settlements SET status=@status WHERE id=@id",
                        P("status", to), P("id", origins[from]));
                    await ExecuteAsync(tx, "SET CONSTRAINTS ALL IMMEDIATE");
                    allowed.Add($"{from}>{to}");
                }
                catch (PostgresException error) when (
                    error.SqlState == "23514" && error.MessageText.Contains("is not allowed", StringComparison.Ordinal))
                {
                }
            }
        }

        // Exactly the SET-001 lifecycle. APPROVED -> APPROVED is not needed by ledger construction, so
        // it is refused like every other move that is not listed.
        Assert.Equal(
            [
                "DRAFT>DRAFT", "DRAFT>CALCULATED", "DRAFT>VOID",
                "CALCULATED>CALCULATED", "CALCULATED>APPROVED", "CALCULATED>VOID",
                "APPROVED>PAID", "APPROVED>VOID",
                "PAID>PAID",
                "VOID>VOID",
            ],
            allowed);

        foreach (var status in Statuses)
        {
            Assert.Equal((status, 0L, 0m, 0L), await ledger.StateAsync(origins[status]));
        }
    }

    [PostgreSqlContractFact]
    public async Task Settlement_total_is_frozen_once_it_leaves_draft_and_calculated()
    {
        await using var ledger = await LedgerScenario.CreateAsync(fixture);
        var draft = await ledger.CommittedAsync("DRAFT", ("DELIVERY", 100));
        var calculated = await ledger.CommittedAsync("CALCULATED", ("DELIVERY", 100));
        var approved = await ledger.CommittedAsync("APPROVED", ("DELIVERY", 100));
        var paid = await ledger.CommittedAsync("PAID", ("DELIVERY", 100));
        var voided = await ledger.CommittedAsync("VOID", ("DELIVERY", 100));

        foreach (var (settlement, to) in new[]
        {
            (calculated, "APPROVED"), (approved, "PAID"), (approved, "VOID"),
            (paid, "PAID"), (voided, "VOID"), (draft, "VOID"), (calculated, "VOID"),
        })
        {
            await using var tx = await ledger.TenantAsync();
            var error = await RejectedAsync(() => SetAsync(tx, settlement, to, 150), "23514");
            Assert.Contains("total is frozen", error.MessageText, StringComparison.Ordinal);
        }

        // Inside DRAFT and CALCULATED the total still moves, because ledger construction needs it to.
        await using (var tx = await ledger.TenantAsync())
        {
            await ledger.AddLineAsync(tx, draft, "DELIVERY", 50, Assignment());
            await SetAsync(tx, draft, "CALCULATED", 150);
            await ledger.AddLineAsync(tx, calculated, "ADJUSTMENT", 50, AuditEntry());
            await SetAsync(tx, calculated, "CALCULATED", 150);
            await tx.CommitAsync();
        }

        Assert.Equal(("CALCULATED", 150L, 150m, 2L), await ledger.StateAsync(draft));
        Assert.Equal(("CALCULATED", 150L, 150m, 2L), await ledger.StateAsync(calculated));
        Assert.Equal(("APPROVED", 100L, 100m, 1L), await ledger.StateAsync(approved));
        Assert.Equal(("PAID", 100L, 100m, 1L), await ledger.StateAsync(paid));
        Assert.Equal(("VOID", 100L, 100m, 1L), await ledger.StateAsync(voided));
    }

    [PostgreSqlContractFact]
    public async Task Line_admission_enforces_parent_tenant_and_line_type_rules()
    {
        await using var ledger = await LedgerScenario.CreateAsync(fixture);
        var parents = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var status in Statuses)
        {
            parents[status] = await ledger.CommittedAsync(status);
        }

        async Task<PostgresException> RefusedAsync(
            Func<TenantTransaction, Task> insert,
            string sqlState,
            string message)
        {
            await using var tx = await ledger.TenantAsync();
            var error = await RejectedAsync(() => insert(tx), sqlState);
            Assert.Contains(message, error.MessageText, StringComparison.Ordinal);
            return error;
        }

        await RefusedAsync(
            tx => ledger.AddLineAsync(tx, Guid.NewGuid(), "DELIVERY", 1, Assignment()),
            "23503", "must belong to an existing settlement");

        // Tenant coherence: even a session allowed to act for both organizations cannot file one
        // tenant's line under the other tenant's settlement.
        await using (var tx = await ledger.BothTenantsAsync())
        {
            var error = await RejectedAsync(
                () => InsertLineAsync(tx, parents["DRAFT"], ledger.ForeignOrganizationId, "DELIVERY", 1,
                    Assignment(), ledger.ForeignOrderId),
                "23514");
            Assert.Contains("owner organization of its settlement", error.MessageText, StringComparison.Ordinal);
        }

        await RefusedAsync(
            tx => ledger.AddLineAsync(tx, parents["DRAFT"], "DELIVERY", 1, "  "),
            "23514", "must name its source");

        // DELIVERY and RETURN: only while DRAFT, always tied to their order, never negative.
        foreach (var type in new[] { "DELIVERY", "RETURN" })
        {
            foreach (var status in Statuses.Where(status => status != "DRAFT"))
            {
                await RefusedAsync(
                    tx => ledger.AddLineAsync(tx, parents[status], type, 1, Assignment()),
                    "23514", "only admitted while the settlement is DRAFT");
            }

            await RefusedAsync(
                tx => InsertLineAsync(tx, parents["DRAFT"], ledger.OrganizationId, type, 1, Assignment(), null),
                "23514", "must reference their order");
            await RefusedAsync(
                tx => ledger.AddLineAsync(tx, parents["DRAFT"], type, -1, Assignment()),
                "23514", "cannot be negative");
        }

        // ADJUSTMENT: only while DRAFT or CALCULATED, never zero, never tied to an order. Slice 2 narrows
        // manual adjustments to CALCULATED in the application; physically both states admit them.
        foreach (var status in new[] { "APPROVED", "PAID", "VOID" })
        {
            await RefusedAsync(
                tx => ledger.AddLineAsync(tx, parents[status], "ADJUSTMENT", 1, AuditEntry()),
                "23514", "only admitted while the settlement is DRAFT or CALCULATED");
        }

        await RefusedAsync(
            tx => ledger.AddLineAsync(tx, parents["CALCULATED"], "ADJUSTMENT", 0, AuditEntry()),
            "23514", "must move the total");
        await RefusedAsync(
            tx => InsertLineAsync(tx, parents["CALCULATED"], ledger.OrganizationId, "ADJUSTMENT", 1,
                AuditEntry(), ledger.OrderId),
            "23514", "cannot reference an order");

        // ROUTE_BASE, BONUS, WAITING and COD remain AI-06 vocabulary without a SET-001 source, so the
        // ledger refuses them outright instead of giving them invented semantics.
        foreach (var type in new[] { "ROUTE_BASE", "BONUS", "WAITING", "COD" })
        {
            await RefusedAsync(
                tx => ledger.AddLineAsync(tx, parents["DRAFT"], type, 1, Assignment()),
                "23514", "is reserved and not admitted by the SET-001 ledger");
        }

        // The admitted shapes, checked through commit-time reconciliation and then rolled back.
        await using (var tx = await ledger.TenantAsync())
        {
            await ledger.AddLineAsync(tx, parents["DRAFT"], "DELIVERY", 0, Assignment());
            await ledger.AddLineAsync(tx, parents["DRAFT"], "RETURN", 40, Assignment());
            await ledger.AddLineAsync(tx, parents["DRAFT"], "ADJUSTMENT", -15, AuditEntry());
            await ledger.AddLineAsync(tx, parents["CALCULATED"], "ADJUSTMENT", 15, AuditEntry());
            await SetAsync(tx, parents["DRAFT"], "DRAFT", 25);
            await SetAsync(tx, parents["CALCULATED"], "CALCULATED", 15);
            await ExecuteAsync(tx, "SET CONSTRAINTS ALL IMMEDIATE");
            await tx.RollbackAsync();
        }

        foreach (var status in Statuses)
        {
            Assert.Equal((status, 0L, 0m, 0L), await ledger.StateAsync(parents[status]));
        }
    }

    [PostgreSqlContractFact]
    public async Task An_economic_source_is_paid_by_at_most_one_settlement_that_is_not_void()
    {
        await using var ledger = await LedgerScenario.CreateAsync(fixture);
        var source = Assignment();
        var claimed = Guid.NewGuid();
        await using (var tx = await ledger.TenantAsync())
        {
            await ledger.CreateDraftAsync(tx, claimed);
            await ledger.AddLineAsync(tx, claimed, "DELIVERY", 1_000, source);
            await SetAsync(tx, claimed, "DRAFT", 1_000);
            await tx.CommitAsync();
        }

        // Within one settlement the canonical UNIQUE(settlement_id, source_reference) answers, whatever
        // the line type: the reference names the economic source, not the kind of line.
        foreach (var type in new[] { "DELIVERY", "RETURN" })
        {
            await using var tx = await ledger.TenantAsync();
            var error = await RejectedAsync(() => ledger.AddLineAsync(tx, claimed, type, 1, source), "23505");
            Assert.Equal("settlement_lines_settlement_id_source_reference_key", error.ConstraintName);
        }

        // Across settlements the ledger answers, at every live stage up to PAID.
        var other = await ledger.CommittedAsync("DRAFT");
        foreach (var stage in new[] { "DRAFT", "CALCULATED", "APPROVED", "PAID" })
        {
            if (stage != "DRAFT")
            {
                await using var advance = await ledger.TenantAsync();
                await SetAsync(advance, claimed, stage, 1_000);
                await advance.CommitAsync();
            }

            foreach (var type in new[] { "DELIVERY", "RETURN" })
            {
                await using var tx = await ledger.TenantAsync();
                var error = await RejectedAsync(() => ledger.AddLineAsync(tx, other, type, 1, source), "23505");
                Assert.Contains("already settled by a settlement that is not VOID", error.MessageText, StringComparison.Ordinal);
            }
        }

        // VOID releases the claim, and only VOID does.
        var released = Assignment();
        var abandoned = Guid.NewGuid();
        await using (var tx = await ledger.TenantAsync())
        {
            await ledger.CreateDraftAsync(tx, abandoned);
            await ledger.AddLineAsync(tx, abandoned, "DELIVERY", 700, released);
            await SetAsync(tx, abandoned, "DRAFT", 700);
            await tx.CommitAsync();
        }

        await using (var tx = await ledger.TenantAsync())
        {
            await RejectedAsync(() => ledger.AddLineAsync(tx, other, "DELIVERY", 700, released), "23505");
        }

        await using (var tx = await ledger.TenantAsync())
        {
            await SetAsync(tx, abandoned, "VOID", 700);
            await tx.CommitAsync();
        }

        await using (var tx = await ledger.TenantAsync())
        {
            await ledger.AddLineAsync(tx, other, "DELIVERY", 700, released);
            await SetAsync(tx, other, "DRAFT", 700);
            await tx.CommitAsync();
        }

        // Adjustments name their own audit entries: they neither claim nor collide with a source.
        var entry = AuditEntry();
        var first = await ledger.CommittedAsync("CALCULATED");
        var second = await ledger.CommittedAsync("CALCULATED");
        await using (var tx = await ledger.TenantAsync())
        {
            await ledger.AddLineAsync(tx, first, "ADJUSTMENT", 25, entry);
            await ledger.AddLineAsync(tx, second, "ADJUSTMENT", -25, entry);
            await ledger.AddLineAsync(tx, other, "DELIVERY", 10, entry);
            await SetAsync(tx, first, "CALCULATED", 25);
            await SetAsync(tx, second, "CALCULATED", -25);
            await SetAsync(tx, other, "DRAFT", 710);
            await tx.CommitAsync();
        }

        // The economic identity is per tenant, so another organization settles its own source of the
        // same name and learns nothing about this tenant's ledger.
        var foreign = Guid.NewGuid();
        await using (var tx = await ledger.ForeignAsync())
        {
            await ledger.CreateDraftAsync(tx, foreign, ledger.ForeignOrganizationId);
            await InsertLineAsync(tx, foreign, ledger.ForeignOrganizationId, "DELIVERY", 1_000, source,
                ledger.ForeignOrderId);
            await SetAsync(tx, foreign, "DRAFT", 1_000);
            await tx.CommitAsync();
        }

        Assert.Equal(("PAID", 1_000L, 1_000m, 1L), await ledger.StateAsync(claimed));
        Assert.Equal(("VOID", 700L, 700m, 1L), await ledger.StateAsync(abandoned));
        Assert.Equal(("DRAFT", 710L, 710m, 2L), await ledger.StateAsync(other));
    }

    [PostgreSqlContractFact]
    public async Task Concurrent_claims_of_one_economic_source_are_serialized()
    {
        await using var ledger = await LedgerScenario.CreateAsync(fixture);
        var first = await ledger.CommittedAsync("DRAFT");
        var second = await ledger.CommittedAsync("DRAFT");

        // A claim that commits wins: the contender waits for it instead of reading a ledger that does not
        // show it yet, and is then refused.
        var committed = Assignment();
        await using (var winner = await ledger.TenantAsync())
        await using (var loser = await ledger.TenantAsync())
        {
            await ledger.AddLineAsync(winner, first, "DELIVERY", 500, committed);
            await SetAsync(winner, first, "DRAFT", 500);

            var contender = ledger.AddLineAsync(loser, second, "DELIVERY", 500, committed);
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            Assert.False(contender.IsCompleted);

            await winner.CommitAsync();
            var error = await Assert.ThrowsAsync<PostgresException>(() => contender.WaitAsync(TimeSpan.FromSeconds(30)));
            Assert.Equal("23505", error.SqlState);
        }

        // A claim that rolls back leaves nothing behind: the contender then takes the source.
        var abandoned = Assignment();
        await using (var winner = await ledger.TenantAsync())
        await using (var loser = await ledger.TenantAsync())
        {
            await ledger.AddLineAsync(winner, first, "DELIVERY", 300, abandoned);

            var contender = ledger.AddLineAsync(loser, second, "DELIVERY", 300, abandoned);
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            Assert.False(contender.IsCompleted);

            await winner.RollbackAsync();
            await contender.WaitAsync(TimeSpan.FromSeconds(30));
            await SetAsync(loser, second, "DRAFT", 300);
            await loser.CommitAsync();
        }

        Assert.Equal(("DRAFT", 500L, 500m, 1L), await ledger.StateAsync(first));
        Assert.Equal(("DRAFT", 300L, 300m, 1L), await ledger.StateAsync(second));
    }

    [PostgreSqlContractFact]
    public async Task Economic_lines_refuse_snapshot_isolation_that_could_miss_a_committed_claim()
    {
        await using var ledger = await LedgerScenario.CreateAsync(fixture);
        var settlement = await ledger.CommittedAsync("DRAFT");

        foreach (var isolation in new[] { IsolationLevel.RepeatableRead, IsolationLevel.Serializable })
        {
            foreach (var type in new[] { "DELIVERY", "RETURN" })
            {
                await using var tx = await ledger.TenantAsync(isolation);
                var error = await RejectedAsync(() => ledger.AddLineAsync(tx, settlement, type, 1, Assignment()), "0A000");
                Assert.Contains("READ COMMITTED", error.MessageText, StringComparison.Ordinal);
            }
        }

        // Adjustments claim no source, so they do not depend on the snapshot.
        await using (var tx = await ledger.TenantAsync(IsolationLevel.RepeatableRead))
        {
            await ledger.AddLineAsync(tx, settlement, "ADJUSTMENT", 5, AuditEntry());
            await SetAsync(tx, settlement, "DRAFT", 5);
            await tx.CommitAsync();
        }

        Assert.Equal(("DRAFT", 5L, 5m, 1L), await ledger.StateAsync(settlement));
    }

    [PostgreSqlContractFact]
    public async Task Row_level_security_isolates_settlement_ledgers_between_tenants()
    {
        await using var ledger = await LedgerScenario.CreateAsync(fixture);
        Assert.Equal(2L, await AdminScalarAsync<long>("""
            SELECT count(*) FROM pg_class
            WHERE oid IN ('finance.settlements'::regclass,'finance.settlement_lines'::regclass)
              AND relrowsecurity AND relforcerowsecurity
            """));
        Assert.Equal("settlement_lines_tenant,settlements_tenant", await AdminScalarAsync<string>("""
            SELECT string_agg(policyname,',' ORDER BY policyname) FROM pg_policies
            WHERE schemaname='finance' AND tablename IN ('settlements','settlement_lines')
            """));

        var settlement = await ledger.CommittedAsync("DRAFT", ("DELIVERY", 1_000));

        // The other tenant can neither see nor touch the ledger.
        await using (var tx = await ledger.ForeignAsync())
        {
            Assert.Equal(0L, await ScalarAsync<long>(tx,
                "SELECT count(*) FROM finance.settlements WHERE id=@id", P("id", settlement)));
            Assert.Equal(0L, await ScalarAsync<long>(tx,
                "SELECT count(*) FROM finance.settlement_lines WHERE settlement_id=@id", P("id", settlement)));
            Assert.Equal(0, await ExecuteAsync(tx,
                "UPDATE finance.settlements SET status='VOID' WHERE id=@id", P("id", settlement)));
        }

        // Writing into it fails closed: a header for a foreign owner fails the policy check, and a line
        // under a settlement the session cannot see has no parent at all.
        await using (var tx = await ledger.ForeignAsync())
        {
            await RejectedAsync(() => ledger.CreateDraftAsync(tx, Guid.NewGuid(), ledger.OrganizationId), "42501");
        }

        foreach (var owner in new[] { ledger.OrganizationId, ledger.ForeignOrganizationId })
        {
            await using var tx = await ledger.ForeignAsync();
            await RejectedAsync(
                () => InsertLineAsync(tx, settlement, owner, "DELIVERY", 1, Assignment(), ledger.ForeignOrderId),
                "23503");
        }

        // FORCE ROW LEVEL SECURITY binds the owner as well: the migration role sees no settlement
        // without a tenant context.
        await using (var migrator = await TenantTransaction.BeginAsync(
            fixture.AdminDataSource, "paqueteria_migrator", Guid.NewGuid(), []))
        {
            Assert.Equal(0L, await ScalarAsync<long>(migrator,
                "SELECT count(*) FROM finance.settlements WHERE id=@id", P("id", settlement)));
        }

        Assert.Equal(("DRAFT", 1_000L, 1_000m, 1L), await ledger.StateAsync(settlement));
    }

    private async Task<T> AdminScalarAsync<T>(string sql)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(sql);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static string Assignment() => $"dispatch.assignments/{Guid.NewGuid()}";

    private static string AuditEntry() => $"platform.audit_logs/{Guid.NewGuid()}";

    private static Task SetAsync(TenantTransaction tx, Guid settlement, string status, long total) =>
        ExecuteAsync(tx, "UPDATE finance.settlements SET status=@status,total_cents=@total WHERE id=@id",
            P("status", status), P("total", total), P("id", settlement));

    private static Task InsertLineAsync(
        TenantTransaction tx,
        Guid settlement,
        Guid owner,
        string lineType,
        long amount,
        string source,
        Guid? order) =>
        ExecuteAsync(tx, """
            INSERT INTO finance.settlement_lines(settlement_id,owner_org_id,order_id,line_type,amount_cents,source_reference)
            VALUES (@settlement,@owner,@order,@line_type,@amount,@source)
            """,
            P("settlement", settlement), P("owner", owner),
            new NpgsqlParameter("order", NpgsqlDbType.Uuid) { Value = order is { } value ? value : DBNull.Value },
            P("line_type", lineType), P("amount", amount), P("source", source));

    private static async Task<int> ExecuteAsync(TenantTransaction tx, string sql, params NpgsqlParameter[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, tx.Connection, tx.Transaction);
        command.Parameters.AddRange(parameters);
        return await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(TenantTransaction tx, string sql, params NpgsqlParameter[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, tx.Connection, tx.Transaction);
        command.Parameters.AddRange(parameters);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<PostgresException> RejectedAsync(Func<Task> action, string sqlState)
    {
        var error = await Assert.ThrowsAsync<PostgresException>(action);
        Assert.Equal(sqlState, error.SqlState);
        return error;
    }

    private static NpgsqlParameter P(string name, object value) => new(name, value);

    /// <summary>
    /// Two tenants, each with a real order, and a DRIVER payee. Ledger rows are removed through the
    /// migration lane on disposal, lines first, before the synthetic orders and organizations go.
    /// </summary>
    private sealed class LedgerScenario : IAsyncDisposable
    {
        private readonly PostgreSqlContractFixture fixture;
        private readonly SyntheticOrderScenario tenant;
        private readonly SyntheticOrderScenario foreign;

        private LedgerScenario(PostgreSqlContractFixture fixture)
        {
            this.fixture = fixture;
            tenant = new SyntheticOrderScenario(fixture);
            foreign = new SyntheticOrderScenario(fixture);
        }

        public Guid OrganizationId => tenant.OrganizationId;
        public Guid ForeignOrganizationId => foreign.OrganizationId;
        public Guid OrderId => tenant.OrderId;
        public Guid ForeignOrderId => foreign.OrderId;
        public Guid DriverId { get; } = Guid.NewGuid();

        public static async Task<LedgerScenario> CreateAsync(PostgreSqlContractFixture fixture)
        {
            var value = new LedgerScenario(fixture);
            await value.tenant.InitializeAsync();
            await value.foreign.InitializeAsync();
            return value;
        }

        public Task<TenantTransaction> TenantAsync() => TenantAsync(IsolationLevel.Unspecified);

        public Task<TenantTransaction> TenantAsync(IsolationLevel isolation) =>
            TenantTransaction.BeginAsync(
                fixture.AppDataSource, "paqueteria_app", tenant.UserId, [tenant.OrganizationId],
                isolationLevel: isolation);

        public Task<TenantTransaction> WorkerAsync() =>
            TenantTransaction.BeginAsync(
                fixture.WorkerDataSource, "paqueteria_worker", tenant.UserId, [tenant.OrganizationId]);

        public Task<TenantTransaction> ForeignAsync() =>
            TenantTransaction.BeginAsync(
                fixture.AppDataSource, "paqueteria_app", foreign.UserId, [foreign.OrganizationId]);

        public Task<TenantTransaction> BothTenantsAsync() =>
            TenantTransaction.BeginAsync(
                fixture.AppDataSource, "paqueteria_app", tenant.UserId,
                [tenant.OrganizationId, foreign.OrganizationId]);

        public Task<TenantTransaction> MigratorAsync() =>
            TenantTransaction.BeginAsync(
                fixture.AdminDataSource, "paqueteria_migrator", tenant.UserId,
                [tenant.OrganizationId, foreign.OrganizationId]);

        public Task CreateDraftAsync(TenantTransaction tx, Guid settlement, Guid? owner = null) =>
            ExecuteAsync(tx, """
                INSERT INTO finance.settlements(id,owner_org_id,payee_type,payee_id,status,total_cents,period_from,period_to)
                VALUES (@id,@owner,'DRIVER',@payee,'DRAFT',0,@from,@to)
                """,
                P("id", settlement), P("owner", owner ?? OrganizationId), P("payee", DriverId),
                P("from", PeriodFrom), P("to", PeriodTo));

        /// <summary>A line of this tenant; order-bearing types reference the tenant's order.</summary>
        public Task AddLineAsync(TenantTransaction tx, Guid settlement, string lineType, long amount, string source) =>
            InsertLineAsync(tx, settlement, OrganizationId, lineType, amount, source,
                lineType == "ADJUSTMENT" ? null : OrderId);

        /// <summary>A committed settlement of this tenant, brought to <paramref name="status"/> legally.</summary>
        public async Task<Guid> CommittedAsync(string status, params (string LineType, long Amount)[] lines)
        {
            var settlement = Guid.NewGuid();
            var total = lines.Sum(line => line.Amount);
            await using var tx = await TenantAsync();
            await CreateDraftAsync(tx, settlement);
            foreach (var (lineType, amount) in lines)
            {
                await AddLineAsync(tx, settlement, lineType, amount,
                    lineType == "ADJUSTMENT" ? AuditEntry() : Assignment());
            }

            await SetAsync(tx, settlement, "DRAFT", total);
            string[] path = status switch
            {
                "DRAFT" => [],
                "CALCULATED" => ["CALCULATED"],
                "APPROVED" => ["CALCULATED", "APPROVED"],
                "PAID" => ["CALCULATED", "APPROVED", "PAID"],
                "VOID" => ["VOID"],
                _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
            };
            foreach (var step in path)
            {
                await SetAsync(tx, settlement, step, total);
            }

            await tx.CommitAsync();
            return settlement;
        }

        public async Task<long> CountAsync(Guid settlement)
        {
            await using var tx = await TenantAsync();
            return await ScalarAsync<long>(tx, "SELECT count(*) FROM finance.settlements WHERE id=@id",
                P("id", settlement));
        }

        public async Task<(string Status, long Total, decimal LineSum, long Lines)> StateAsync(Guid settlement)
        {
            await using var tx = await TenantAsync();
            await using var command = new NpgsqlCommand("""
                SELECT s.status,s.total_cents,
                       (SELECT COALESCE(sum(l.amount_cents),0) FROM finance.settlement_lines l WHERE l.settlement_id=s.id),
                       (SELECT count(*) FROM finance.settlement_lines l WHERE l.settlement_id=s.id)
                FROM finance.settlements s WHERE s.id=@id
                """, tx.Connection, tx.Transaction);
            command.Parameters.AddWithValue("id", settlement);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return (reader.GetString(0), reader.GetInt64(1), reader.GetDecimal(2), reader.GetInt64(3));
        }

        public async ValueTask DisposeAsync()
        {
            await using (var migrator = await MigratorAsync())
            {
                await ExecuteAsync(migrator, """
                    DELETE FROM finance.settlement_lines WHERE owner_org_id=ANY(@orgs);
                    DELETE FROM finance.settlements WHERE owner_org_id=ANY(@orgs);
                    """,
                    P("orgs", new[] { tenant.OrganizationId, foreign.OrganizationId }));
                await migrator.CommitAsync();
            }

            await foreign.DisposeAsync();
            await tenant.DisposeAsync();
        }
    }
}
