using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Orders.Infrastructure.Persistence.Migrations;

[DbContext(typeof(OrdersDbContext))]
[Migration(MigrationId)]
public sealed class AdoptCanonicalOrdersBaseline : Migration
{
    public const string MigrationId = "20260722_AdoptCanonicalOrdersBaseline";

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(
        """
        DO $adoption$
        DECLARE
          order_checks text;
          acceptance_checks text;
        BEGIN
          IF to_regclass('orders.orders') IS NULL
             OR to_regclass('orders.package_items') IS NULL
             OR to_regclass('orders.order_events') IS NULL
             OR to_regclass('orders.order_acceptances') IS NULL THEN
            RAISE EXCEPTION 'ORD-001 adoption requires canonical orders tables from AI-06';
          END IF;

          IF NOT EXISTS (
            SELECT 1 FROM pg_constraint
            WHERE conrelid='orders.orders'::regclass AND contype='u'
              AND pg_get_constraintdef(oid)='UNIQUE (quote_id)'
          ) OR NOT EXISTS (
            SELECT 1 FROM pg_constraint
            WHERE conrelid='orders.orders'::regclass AND contype='u'
              AND pg_get_constraintdef(oid)='UNIQUE (public_id)'
          ) THEN
            RAISE EXCEPTION 'canonical order quote/public uniqueness constraints are missing';
          END IF;

          IF (
            SELECT count(*) FROM information_schema.columns
            WHERE table_schema='orders' AND table_name='orders'
              AND column_name IN (
                'subtotal_cents','discount_cents','tax_cents','total_cents',
                'minimum_total_cents_snapshot','cod_expected_cents')
              AND data_type='bigint'
          ) <> 6 THEN
            RAISE EXCEPTION 'canonical order monetary columns must be bigint';
          END IF;

          IF NOT EXISTS (
            SELECT 1 FROM information_schema.columns
            WHERE table_schema='orders' AND table_name='orders'
              AND column_name='package_snapshot' AND data_type='jsonb'
          ) OR NOT EXISTS (
            SELECT 1 FROM information_schema.columns
            WHERE table_schema='orders' AND table_name='package_items'
              AND column_name='dimensions_mm' AND data_type='jsonb'
          ) OR NOT EXISTS (
            SELECT 1 FROM information_schema.columns
            WHERE table_schema='orders' AND table_name='order_events'
              AND column_name='payload' AND data_type='jsonb'
          ) OR NOT EXISTS (
            SELECT 1 FROM information_schema.columns
            WHERE table_schema='orders' AND table_name='order_acceptances'
              AND column_name='evidence_hash' AND data_type='bytea'
          ) THEN
            RAISE EXCEPTION 'canonical orders JSONB or evidence hash columns do not match AI-06';
          END IF;

          SELECT string_agg(pg_get_constraintdef(oid), ' ') INTO order_checks
          FROM pg_constraint WHERE conrelid='orders.orders'::regclass AND contype='c';
          IF order_checks IS NULL
             OR position('total_cents = ((subtotal_cents - discount_cents) + tax_cents)' in order_checks)=0
             OR position('minimum_total_cents_snapshot' in order_checks)=0
             OR position('financial_override' in order_checks)=0 THEN
            RAISE EXCEPTION 'canonical order coherence constraints are missing';
          END IF;

          SELECT string_agg(pg_get_constraintdef(oid), ' ') INTO acceptance_checks
          FROM pg_constraint WHERE conrelid='orders.order_acceptances'::regclass AND contype='c';
          IF acceptance_checks IS NULL
             OR position('octet_length(evidence_hash) = 32' in acceptance_checks)=0
             OR position('order-acceptance-v1' in acceptance_checks)=0
             OR position('acceptance_channel' in acceptance_checks)=0 THEN
            RAISE EXCEPTION 'canonical order acceptance constraints are missing';
          END IF;

          IF to_regclass('orders.orders_owner_status_idx') IS NULL
             OR to_regclass('orders.orders_operator_status_idx') IS NULL
             OR to_regclass('orders.orders_city_status_idx') IS NULL
             OR to_regclass('orders.package_items_order_idx') IS NULL
             OR to_regclass('orders.order_events_tenant_order_time_idx') IS NULL
             OR to_regclass('orders.order_acceptances_tenant_time_idx') IS NULL THEN
            RAISE EXCEPTION 'canonical orders indexes are missing';
          END IF;
        END
        $adoption$;
        """);

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Adoption is intentionally non-destructive. Canonical AI-06 objects are never dropped here.
    }
}
