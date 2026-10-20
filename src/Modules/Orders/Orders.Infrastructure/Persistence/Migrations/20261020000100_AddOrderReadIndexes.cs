using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orders.Infrastructure.Persistence.Migrations;

/// <summary>
/// DP-INDEXES-ORDERS-DISPATCH-2026-10-20 (project owner, G3: "Ahora, en un PR (Recommended)"). Two read indexes the
/// AI-06 baseline declares for fresh installations, for installations provisioned before it:
/// <list type="bullet">
/// <item><c>orders.order_events_actor_status_time_idx</c>: one actor's ORDER_STATUS_CHANGED events in a time range, so
/// the driver's completed_today count (listMyStops) reads one day of the driver's own changes.</item>
/// <item><c>orders.orders_price_review_idx</c>: the orders the operations dashboard's price-review filter selects
/// (not finished, below the minimum snapshot or with an authorized financial override), newest change first. Its
/// predicate is the reader's text, so the planner can prove it.</item>
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
[DbContext(typeof(OrdersDbContext))]
[Migration(MigrationId)]
public sealed class AddOrderReadIndexes : Migration
{
    public const string MigrationId = "20261020000100_AddOrderReadIndexes";
    public const string DecisionId = "DP-INDEXES-ORDERS-DISPATCH-2026-10-20";

    /// <summary>
    /// Every index this step owns: its qualified name, the statement AI-06 declares (byte for byte) and the definition
    /// <c>pg_get_indexdef</c> renders for it.
    /// </summary>
    public static IReadOnlyList<(string Name, string Statement, string Definition)> Indexes { get; } =
    [
        ("orders.order_events_actor_status_time_idx",
            "CREATE INDEX order_events_actor_status_time_idx ON orders.order_events(actor_id,occurred_at) WHERE actor_id IS NOT NULL AND event_type='ORDER_STATUS_CHANGED';",
            "CREATE INDEX order_events_actor_status_time_idx ON orders.order_events USING btree (actor_id, occurred_at) WHERE ((actor_id IS NOT NULL) AND (event_type = 'ORDER_STATUS_CHANGED'::text))"),
        ("orders.orders_price_review_idx",
            "CREATE INDEX orders_price_review_idx ON orders.orders(updated_at DESC,id DESC) WHERE status NOT IN ('DELIVERED','RETURNED','CLAIM_RESOLVED','CLOSED','CANCELLED') AND (COALESCE(total_cents < minimum_total_cents_snapshot,false) OR COALESCE(financial_override ?& ARRAY['actor_id','reason','valid_until'],false));",
            "CREATE INDEX orders_price_review_idx ON orders.orders USING btree (updated_at DESC, id DESC) WHERE ((status <> ALL (ARRAY['DELIVERED'::text, 'RETURNED'::text, 'CLAIM_RESOLVED'::text, 'CLOSED'::text, 'CANCELLED'::text])) AND (COALESCE((total_cents < minimum_total_cents_snapshot), false) OR COALESCE((financial_override ?& ARRAY['actor_id'::text, 'reason'::text, 'valid_until'::text]), false)))"),
    ];

    public const string UpSql =
        """
        DO $adoption$
        BEGIN
          IF to_regclass('orders.order_events') IS NULL OR to_regclass('orders.orders') IS NULL THEN
            RAISE EXCEPTION 'DP-INDEXES-ORDERS-DISPATCH requires the canonical AI-06 orders tables';
          END IF;

          IF EXISTS (
            SELECT 1
            FROM (VALUES
              ('order_events_actor_status_time_idx',
               'CREATE INDEX order_events_actor_status_time_idx ON orders.order_events USING btree (actor_id, occurred_at) WHERE ((actor_id IS NOT NULL) AND (event_type = ''ORDER_STATUS_CHANGED''::text))'),
              ('orders_price_review_idx',
               'CREATE INDEX orders_price_review_idx ON orders.orders USING btree (updated_at DESC, id DESC) WHERE ((status <> ALL (ARRAY[''DELIVERED''::text, ''RETURNED''::text, ''CLAIM_RESOLVED''::text, ''CLOSED''::text, ''CANCELLED''::text])) AND (COALESCE((total_cents < minimum_total_cents_snapshot), false) OR COALESCE((financial_override ?& ARRAY[''actor_id''::text, ''reason''::text, ''valid_until''::text]), false)))')
            ) expected(name, definition)
            JOIN pg_class c ON c.relname = expected.name
            JOIN pg_namespace n ON n.oid = c.relnamespace AND n.nspname = 'orders'
            WHERE pg_get_indexdef(c.oid) IS DISTINCT FROM expected.definition
          ) THEN
            RAISE EXCEPTION 'DP-INDEXES-ORDERS-DISPATCH found an orders index of another shape; it is never altered';
          END IF;
        END
        $adoption$;

        CREATE INDEX IF NOT EXISTS order_events_actor_status_time_idx ON orders.order_events(actor_id,occurred_at) WHERE actor_id IS NOT NULL AND event_type='ORDER_STATUS_CHANGED';
        CREATE INDEX IF NOT EXISTS orders_price_review_idx ON orders.orders(updated_at DESC,id DESC) WHERE status NOT IN ('DELIVERED','RETURNED','CLAIM_RESOLVED','CLOSED','CANCELLED') AND (COALESCE(total_cents < minimum_total_cents_snapshot,false) OR COALESCE(financial_override ?& ARRAY['actor_id','reason','valid_until'],false));

        DO $verify$
        BEGIN
          IF (SELECT count(*)
              FROM (VALUES
                ('orders.order_events_actor_status_time_idx',
                 'CREATE INDEX order_events_actor_status_time_idx ON orders.order_events USING btree (actor_id, occurred_at) WHERE ((actor_id IS NOT NULL) AND (event_type = ''ORDER_STATUS_CHANGED''::text))'),
                ('orders.orders_price_review_idx',
                 'CREATE INDEX orders_price_review_idx ON orders.orders USING btree (updated_at DESC, id DESC) WHERE ((status <> ALL (ARRAY[''DELIVERED''::text, ''RETURNED''::text, ''CLAIM_RESOLVED''::text, ''CLOSED''::text, ''CANCELLED''::text])) AND (COALESCE((total_cents < minimum_total_cents_snapshot), false) OR COALESCE((financial_override ?& ARRAY[''actor_id''::text, ''reason''::text, ''valid_until''::text]), false)))')
              ) expected(name, definition)
              JOIN pg_index i ON i.indexrelid = to_regclass(expected.name)
              WHERE i.indisvalid AND i.indisready
                AND pg_get_indexdef(i.indexrelid) = expected.definition
                AND pg_get_userbyid((SELECT relowner FROM pg_class WHERE oid = i.indexrelid)) = 'paqueteria_migrator') <> 2
          THEN
            RAISE EXCEPTION 'DP-INDEXES-ORDERS-DISPATCH orders index set differs from the canonical AI-06 contract';
          END IF;
        END
        $verify$;
        """;

    /// <summary>Rollback: exactly the two indexes of this step; every row stays.</summary>
    public const string DownSql =
        """
        DROP INDEX IF EXISTS orders.order_events_actor_status_time_idx;
        DROP INDEX IF EXISTS orders.orders_price_review_idx;
        """;

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(UpSql);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(DownSql);
}
