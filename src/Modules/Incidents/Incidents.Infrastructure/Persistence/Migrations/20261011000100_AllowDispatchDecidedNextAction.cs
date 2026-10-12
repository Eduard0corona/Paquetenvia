using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Incidents.Infrastructure.Persistence.Migrations;

/// <summary>
/// INC-FAILED-ATTEMPT-DISPATCH-DECIDES-2026-10-11 (owner, 2026-10-11: "Despacho decide"). A driver
/// reports a failed attempt with a reason and evidence only, and dispatch decides afterwards whether
/// the order is rescheduled, retried today or returned. The incident of such a report records no next
/// action: <c>incidents.incidents.next_action</c> becomes nullable, and NULL means "dispatch decides".
/// <para>
/// The column belongs to the INC-001 module migration, not to the frozen AI-06 bundle, so the change
/// lives in this lane. Nothing else changes: <c>incidents_next_action_check</c> still admits only
/// <c>RESCHEDULED</c> and <c>RETURNING</c> when a value is recorded, and
/// <c>incidents_returning_requires_custody_check</c> still ties <c>RETURNING</c> to custody (both
/// accept NULL, as every CHECK does). No row is read or rewritten, and no grant, policy, trigger or
/// RLS setting is touched.
/// </para>
/// <para>
/// The rollback restores <c>NOT NULL</c> only while no incident leaves the decision to dispatch. Mapping
/// those incidents to a next action would invent a decision nobody took (and <c>RETURNING</c> could
/// even contradict custody), so the rollback refuses instead with
/// <see cref="DowngradeBlocked"/>. The probe is <c>SET NOT NULL</c> itself: its validation reads every
/// row regardless of row level security, so a tenant's incident the migrator cannot see still blocks it.
/// </para>
/// </summary>
[DbContext(typeof(IncidentsDbContext))]
[Migration(MigrationId)]
public sealed class AllowDispatchDecidedNextAction : Migration
{
    public const string MigrationId = "20261011000100_AllowDispatchDecidedNextAction";

    public const string DecisionId = "INC-FAILED-ATTEMPT-DISPATCH-DECIDES-2026-10-11";

    public const string DowngradeBlocked = "INC_DISPATCH_DECISION_DOWNGRADE_BLOCKED";

    public const string UpSql =
        """
        DO $dispatch_decides$
        BEGIN
          IF to_regclass('incidents.incidents') IS NULL THEN
            RAISE EXCEPTION 'INC-FAILED-ATTEMPT-DISPATCH-DECIDES-2026-10-11 requires the INC-001 incidents table';
          END IF;

          IF NOT EXISTS (
            SELECT 1 FROM information_schema.columns
            WHERE table_schema='incidents' AND table_name='incidents'
              AND column_name='next_action' AND data_type='text') THEN
            RAISE EXCEPTION 'INC-FAILED-ATTEMPT-DISPATCH-DECIDES-2026-10-11 requires the INC-001 next_action column';
          END IF;

          -- NULL: the driver reported the attempt and dispatch decides its successor.
          ALTER TABLE incidents.incidents ALTER COLUMN next_action DROP NOT NULL;

          -- Verification: the column is nullable and both INC-001 checks still guard a recorded value.
          IF NOT EXISTS (
            SELECT 1 FROM information_schema.columns
            WHERE table_schema='incidents' AND table_name='incidents'
              AND column_name='next_action' AND data_type='text' AND is_nullable='YES') THEN
            RAISE EXCEPTION 'incidents.incidents.next_action is not nullable';
          END IF;

          IF (SELECT count(*) FROM pg_constraint
              WHERE conrelid='incidents.incidents'::regclass AND contype='c'
                AND conname IN (
                  'incidents_next_action_check',
                  'incidents_returning_requires_custody_check')) <> 2 THEN
            RAISE EXCEPTION 'the INC-001 next action checks are missing';
          END IF;
        END
        $dispatch_decides$;
        """;

    public const string DownSql =
        """
        DO $dispatch_decides_downgrade$
        BEGIN
          IF to_regclass('incidents.incidents') IS NULL THEN
            RAISE EXCEPTION 'INC-FAILED-ATTEMPT-DISPATCH-DECIDES-2026-10-11 rollback requires the INC-001 incidents table';
          END IF;

          -- SET NOT NULL validates every row regardless of row level security, so it is its own probe:
          -- it succeeds only while no incident leaves the decision to dispatch.
          BEGIN
            ALTER TABLE incidents.incidents ALTER COLUMN next_action SET NOT NULL;
          EXCEPTION WHEN not_null_violation THEN
            RAISE EXCEPTION USING
              MESSAGE = 'INC_DISPATCH_DECISION_DOWNGRADE_BLOCKED',
              DETAIL = 'Incidents leave the next action of their failed attempt to dispatch; restoring NOT NULL would invent a decision nobody took.',
              ERRCODE = 'P0001';
          END;
        END
        $dispatch_decides_downgrade$;
        """;

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(UpSql);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(DownSql);
}
