using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dispatch.Infrastructure.Persistence.Migrations;

/// <summary>
/// DP-INDEXES-ORDERS-DISPATCH-2026-10-20 (project owner, G3: "Ahora, en un PR (Recommended)"). Two read indexes the
/// AI-06 baseline declares for fresh installations, for installations provisioned before it:
/// <list type="bullet">
/// <item><c>dispatch.assignments_driver_open_idx</c>: a driver's ACCEPTED or ACTIVE assignments, the predicate
/// listMyStops, listOrganizationDrivers and the assignment picker use, without the driver's finished history
/// (<c>assignments_driver_idx</c> stays: settlements read every status).</item>
/// <item><c>dispatch.assignments_order_driver_idx</c>: one order's assignment to one driver in any status, the
/// completed_today probe of listMyStops.</item>
/// </list>
/// The lane runs inside the migrator's transaction (EF Core, or the E-002 bridge transaction), so the indexes are built
/// with a plain <c>CREATE INDEX</c>, never <c>CONCURRENTLY</c>. An index AI-06 already created is adopted; one with the
/// same name and another shape is refused, never altered. No table, column, constraint, policy, grant, function or row
/// is added, changed or removed.
/// </summary>
/// <remarks>
/// Rollback drops exactly these two indexes. No code depends on them: the previous release reads the same rows through
/// the previous plans, only slower.
/// </remarks>
[DbContext(typeof(DispatchDbContext))]
[Migration(MigrationId)]
public sealed class AddAssignmentReadIndexes : Migration
{
    public const string MigrationId = "20261020000100_AddAssignmentReadIndexes";
    public const string DecisionId = "DP-INDEXES-ORDERS-DISPATCH-2026-10-20";

    /// <summary>
    /// Every index this step owns: its qualified name, the statement AI-06 declares (byte for byte) and the definition
    /// <c>pg_get_indexdef</c> renders for it.
    /// </summary>
    public static IReadOnlyList<(string Name, string Statement, string Definition)> Indexes { get; } =
    [
        ("dispatch.assignments_driver_open_idx",
            "CREATE INDEX assignments_driver_open_idx ON dispatch.assignments(driver_id) WHERE status IN ('ACCEPTED','ACTIVE');",
            "CREATE INDEX assignments_driver_open_idx ON dispatch.assignments USING btree (driver_id) WHERE (status = ANY (ARRAY['ACCEPTED'::text, 'ACTIVE'::text]))"),
        ("dispatch.assignments_order_driver_idx",
            "CREATE INDEX assignments_order_driver_idx ON dispatch.assignments(order_id,driver_id);",
            "CREATE INDEX assignments_order_driver_idx ON dispatch.assignments USING btree (order_id, driver_id)"),
    ];

    public const string UpSql =
        """
        DO $adoption$
        BEGIN
          IF to_regclass('dispatch.assignments') IS NULL THEN
            RAISE EXCEPTION 'DP-INDEXES-ORDERS-DISPATCH requires the canonical AI-06 dispatch.assignments table';
          END IF;

          IF EXISTS (
            SELECT 1
            FROM (VALUES
              ('assignments_driver_open_idx',
               'CREATE INDEX assignments_driver_open_idx ON dispatch.assignments USING btree (driver_id) WHERE (status = ANY (ARRAY[''ACCEPTED''::text, ''ACTIVE''::text]))'),
              ('assignments_order_driver_idx',
               'CREATE INDEX assignments_order_driver_idx ON dispatch.assignments USING btree (order_id, driver_id)')
            ) expected(name, definition)
            JOIN pg_class c ON c.relname = expected.name
            JOIN pg_namespace n ON n.oid = c.relnamespace AND n.nspname = 'dispatch'
            WHERE pg_get_indexdef(c.oid) IS DISTINCT FROM expected.definition
          ) THEN
            RAISE EXCEPTION 'DP-INDEXES-ORDERS-DISPATCH found a dispatch index of another shape; it is never altered';
          END IF;
        END
        $adoption$;

        CREATE INDEX IF NOT EXISTS assignments_driver_open_idx ON dispatch.assignments(driver_id) WHERE status IN ('ACCEPTED','ACTIVE');
        CREATE INDEX IF NOT EXISTS assignments_order_driver_idx ON dispatch.assignments(order_id,driver_id);

        DO $verify$
        BEGIN
          IF (SELECT count(*)
              FROM (VALUES
                ('dispatch.assignments_driver_open_idx',
                 'CREATE INDEX assignments_driver_open_idx ON dispatch.assignments USING btree (driver_id) WHERE (status = ANY (ARRAY[''ACCEPTED''::text, ''ACTIVE''::text]))'),
                ('dispatch.assignments_order_driver_idx',
                 'CREATE INDEX assignments_order_driver_idx ON dispatch.assignments USING btree (order_id, driver_id)')
              ) expected(name, definition)
              JOIN pg_index i ON i.indexrelid = to_regclass(expected.name)
              WHERE i.indisvalid AND i.indisready
                AND pg_get_indexdef(i.indexrelid) = expected.definition
                AND pg_get_userbyid((SELECT relowner FROM pg_class WHERE oid = i.indexrelid)) = 'paqueteria_migrator') <> 2
          THEN
            RAISE EXCEPTION 'DP-INDEXES-ORDERS-DISPATCH dispatch index set differs from the canonical AI-06 contract';
          END IF;
        END
        $verify$;
        """;

    /// <summary>Rollback: exactly the two indexes of this step; every row stays.</summary>
    public const string DownSql =
        """
        DROP INDEX IF EXISTS dispatch.assignments_driver_open_idx;
        DROP INDEX IF EXISTS dispatch.assignments_order_driver_idx;
        """;

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(UpSql);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(DownSql);
}
