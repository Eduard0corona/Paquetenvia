using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Pricing.Infrastructure.Persistence.Migrations;

[DbContext(typeof(PricingDbContext))]
[Migration(MigrationId)]
public sealed class AdoptCanonicalPricingBaseline : Migration
{
    public const string MigrationId = "20260722_AdoptCanonicalPricingBaseline";

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(
        """
        DO $adoption$
        DECLARE
          quote_checks text;
        BEGIN
          IF to_regclass('pricing.tariff_rules') IS NULL OR to_regclass('pricing.quotes') IS NULL THEN
            RAISE EXCEPTION 'PRC-001 adoption requires canonical pricing tables from AI-06';
          END IF;

          IF NOT EXISTS (
            SELECT 1 FROM information_schema.columns
            WHERE table_schema='pricing' AND table_name='tariff_rules' AND column_name='amount_cents' AND data_type='bigint'
          ) OR NOT EXISTS (
            SELECT 1 FROM information_schema.columns
            WHERE table_schema='pricing' AND table_name='quotes' AND column_name='subtotal_cents' AND data_type='bigint'
          ) OR NOT EXISTS (
            SELECT 1 FROM information_schema.columns
            WHERE table_schema='pricing' AND table_name='quotes' AND column_name='discount_cents' AND data_type='bigint'
          ) OR NOT EXISTS (
            SELECT 1 FROM information_schema.columns
            WHERE table_schema='pricing' AND table_name='quotes' AND column_name='tax_cents' AND data_type='bigint'
          ) OR NOT EXISTS (
            SELECT 1 FROM information_schema.columns
            WHERE table_schema='pricing' AND table_name='quotes' AND column_name='total_cents' AND data_type='bigint'
          ) OR NOT EXISTS (
            SELECT 1 FROM information_schema.columns
            WHERE table_schema='pricing' AND table_name='quotes' AND column_name='minimum_total_cents_snapshot' AND data_type='bigint'
          ) THEN
            RAISE EXCEPTION 'canonical pricing money columns must be bigint';
          END IF;

          IF NOT EXISTS (
            SELECT 1 FROM information_schema.columns
            WHERE table_schema='pricing' AND table_name='quotes' AND column_name='rule_ids' AND udt_name='_uuid'
          ) OR NOT EXISTS (
            SELECT 1 FROM information_schema.columns
            WHERE table_schema='pricing' AND table_name='quotes' AND column_name IN ('request_snapshot_redacted','package_snapshot','breakdown')
              AND data_type='jsonb' GROUP BY table_schema,table_name HAVING count(*)=3
          ) OR NOT EXISTS (
            SELECT 1 FROM information_schema.columns
            WHERE table_schema='pricing' AND table_name='quotes' AND column_name='input_hash' AND data_type='bytea'
          ) THEN
            RAISE EXCEPTION 'canonical pricing snapshot, UUID array or hash columns do not match AI-06';
          END IF;

          IF to_regclass('pricing.quotes_owner_expiry_idx') IS NULL THEN
            RAISE EXCEPTION 'canonical quote expiry index is missing';
          END IF;

          SELECT string_agg(pg_get_constraintdef(oid), ' ') INTO quote_checks
          FROM pg_constraint WHERE conrelid='pricing.quotes'::regclass AND contype='c';
          IF quote_checks IS NULL
             OR position('total_cents = ((subtotal_cents - discount_cents) + tax_cents)' in quote_checks)=0
             OR position('minimum_total_cents_snapshot' in quote_checks)=0
             OR position('financial_override' in quote_checks)=0 THEN
            RAISE EXCEPTION 'canonical quote coherence constraints are missing';
          END IF;
        END
        $adoption$;
        """);

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Adoption is intentionally non-destructive. Canonical AI-06 objects are never dropped here.
    }
}
