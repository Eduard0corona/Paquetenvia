using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orders.Infrastructure.Persistence.Migrations;

/// <summary>
/// ORD-SERVICE-WINDOW-OPTIONAL-2026-10-02: <c>orders.orders</c> gains the optional delivery window the dispatcher
/// may give on createOrder, as two nullable <c>timestamptz</c> columns <c>service_window_from</c> and
/// <c>service_window_to</c> plus <c>orders_service_window_check</c> (both set or both null, from strictly before to).
/// NULL in both means the order has no window of its own and the zone's schedule applies, which is what every
/// existing row reads as. AI-06 already carries the same shape on fresh installations, so the lane adopts it when
/// present and refuses any other shape. Neither column has a default, so no row is rewritten; nothing is dropped.
/// </summary>
[DbContext(typeof(OrdersDbContext))]
[Migration(MigrationId)]
public sealed class AddOrderServiceWindow : Migration
{
    public const string MigrationId = "20261002000100_AddOrderServiceWindow";

    public const string CheckName = "orders_service_window_check";

    /// <summary>The check exactly as <c>pg_get_constraintdef</c> renders it.</summary>
    public const string CheckDefinition =
        "CHECK ((((service_window_from IS NULL) = (service_window_to IS NULL)) AND ((service_window_from IS NULL) OR (service_window_from < service_window_to))))";

    public const string UpSql =
        """
        DO $adoption$
        BEGIN
          IF to_regclass('orders.orders') IS NULL THEN
            RAISE EXCEPTION 'ORD-SERVICE-WINDOW-OPTIONAL requires the canonical AI-06 orders.orders table';
          END IF;

          IF (SELECT count(*) FROM information_schema.columns
              WHERE table_schema='orders' AND table_name='orders'
                AND column_name IN ('service_window_from','service_window_to')) NOT IN (0,2)
          THEN
            RAISE EXCEPTION 'orders.orders service window columns are partially present';
          END IF;

          IF NOT EXISTS (
            SELECT 1 FROM pg_class
            WHERE oid='orders.orders'::regclass AND relrowsecurity AND relforcerowsecurity
          ) THEN
            RAISE EXCEPTION 'ORD-SERVICE-WINDOW-OPTIONAL requires FORCE ROW LEVEL SECURITY on orders.orders';
          END IF;
        END
        $adoption$;

        ALTER TABLE orders.orders
          ADD COLUMN IF NOT EXISTS service_window_from timestamptz,
          ADD COLUMN IF NOT EXISTS service_window_to timestamptz;

        DO $constraint$
        BEGIN
          IF NOT EXISTS (
            SELECT 1 FROM pg_constraint
            WHERE conrelid='orders.orders'::regclass AND conname='orders_service_window_check'
          ) THEN
            ALTER TABLE orders.orders ADD CONSTRAINT orders_service_window_check
              CHECK ((service_window_from IS NULL) = (service_window_to IS NULL)
                AND (service_window_from IS NULL OR service_window_from < service_window_to));
          END IF;
        END
        $constraint$;

        DO $verify$
        BEGIN
          IF (SELECT array_agg(column_name || ':' || data_type || ':' || is_nullable || ':' || COALESCE(column_default,'')
                ORDER BY column_name)
              FROM information_schema.columns
              WHERE table_schema='orders' AND table_name='orders'
                AND column_name IN ('service_window_from','service_window_to'))
             IS DISTINCT FROM ARRAY[
               'service_window_from:timestamp with time zone:YES:',
               'service_window_to:timestamp with time zone:YES:'
             ]
          THEN
            RAISE EXCEPTION 'orders.orders service window columns differ from the ORD-SERVICE-WINDOW-OPTIONAL contract';
          END IF;

          IF (SELECT array_agg(pg_get_constraintdef(oid) || ':' || convalidated::text)
              FROM pg_constraint
              WHERE conrelid='orders.orders'::regclass AND conname='orders_service_window_check' AND contype='c')
             IS DISTINCT FROM ARRAY['CHECK ((((service_window_from IS NULL) = (service_window_to IS NULL)) AND ((service_window_from IS NULL) OR (service_window_from < service_window_to)))):true']
          THEN
            RAISE EXCEPTION 'orders.orders service window check differs from the ORD-SERVICE-WINDOW-OPTIONAL contract';
          END IF;

          IF NOT EXISTS (
            SELECT 1 FROM pg_class
            WHERE oid='orders.orders'::regclass AND relrowsecurity AND relforcerowsecurity
          ) THEN
            RAISE EXCEPTION 'ORD-SERVICE-WINDOW-OPTIONAL requires FORCE ROW LEVEL SECURITY on orders.orders';
          END IF;
        END
        $verify$;
        """;

    /// <summary>
    /// The schema is never downgraded: dropping the columns would erase delivery windows dispatchers committed to,
    /// and AI-06 carries both columns on fresh installations. The previous release reads and writes
    /// <c>orders.orders</c> with explicit column lists and both columns are nullable without a default, so an
    /// application rollback needs no schema change (orders it creates simply carry no window).
    /// </summary>
    public const string SchemaDowngradeNotSupportedSql =
        """
        DO $schema_downgrade$
        BEGIN
          RAISE EXCEPTION USING
            MESSAGE = 'ORD_SERVICE_WINDOW_DOWNGRADE_NOT_SUPPORTED',
            DETAIL = 'ORD-SERVICE-WINDOW-OPTIONAL rollback blocked: the previous release works with the nullable service window columns in place; stored windows are never dropped.',
            ERRCODE = 'P0001';
        END
        $schema_downgrade$;
        """;

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(UpSql);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(SchemaDowngradeNotSupportedSql);
}
