using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pricing.Infrastructure.Persistence.Migrations;

/// <summary>
/// PRC-POLICY-VERSION-PER-ORG (owner, 2026-09-28: "Versión por organización"). Each organization
/// versions its own pricing policy on its tariff rules, in <c>pricing.tariff_rules.policy_version</c>,
/// and a quote freezes the version of the rule it selected into <c>pricing.quotes.pricing_policy_version</c>,
/// which the order copies unchanged. There is no global pricing policy version any more.
/// <para>
/// AI-06 already declares <c>policy_version text NOT NULL CHECK (policy_version ~ '^[A-Za-z0-9._-]{1,64}$')</c>
/// on fresh installations; this lane adopts that shape there and creates it on installations whose
/// baseline predates the decision.
/// </para>
/// <para>
/// Existing rows are never rewritten: nobody but the owning organization knows which version of its
/// policy an existing rule belongs to, and a synthetic label would be frozen into quotes and orders as
/// if it were a fact. So the column is required in two steps. When the table holds no rule without a
/// version (a fresh or empty installation), the column becomes <c>NOT NULL</c> at once. Otherwise the
/// column stays nullable and <c>tariff_rules_policy_version_required CHECK (policy_version IS NOT NULL)
/// NOT VALID</c> requires a version on every new or updated row, while the pre-existing rows keep NULL
/// and are never quoted (the quote engine fails closed) until their organization sets a version. The
/// second step (VALIDATE and SET NOT NULL) belongs to a later migration, once no NULL remains.
/// </para>
/// <para>
/// The rollback removes the column and its constraints, but refuses while any rule carries a version:
/// that would discard each organization's policy history.
/// </para>
/// </summary>
[DbContext(typeof(PricingDbContext))]
[Migration(MigrationId)]
public sealed class VersionPricingPolicyPerOrganization : Migration
{
    public const string MigrationId = "20260928000100_VersionPricingPolicyPerOrganization";

    public const string FormatConstraint = "tariff_rules_policy_version_check";

    public const string RequiredConstraint = "tariff_rules_policy_version_required";

    public const string DowngradeBlocked = "PRC_POLICY_VERSION_DOWNGRADE_BLOCKED";

    /// <summary>The format CHECK exactly as <c>pg_get_constraintdef</c> renders it.</summary>
    public const string FormatConstraintDefinition = "CHECK ((policy_version ~ '^[A-Za-z0-9._-]{1,64}$'::text))";

    /// <summary>The transitional NOT VALID CHECK exactly as <c>pg_get_constraintdef</c> renders it.</summary>
    public const string RequiredConstraintDefinition = "CHECK ((policy_version IS NOT NULL)) NOT VALID";

    public const string UpSql =
        """
        DO $prc_policy_version$
        DECLARE
          v_type text;
          v_nullable text;
          v_definition text;
        BEGIN
          IF to_regclass('pricing.tariff_rules') IS NULL OR to_regclass('pricing.quotes') IS NULL THEN
            RAISE EXCEPTION 'PRC-POLICY-VERSION-PER-ORG requires the canonical AI-06 pricing tables';
          END IF;

          IF NOT EXISTS (
            SELECT 1 FROM pg_class
            WHERE oid='pricing.tariff_rules'::regclass AND relrowsecurity AND relforcerowsecurity)
             OR NOT EXISTS (
            SELECT 1 FROM pg_policies
            WHERE schemaname='pricing' AND tablename='tariff_rules' AND policyname='tariff_rules_tenant') THEN
            RAISE EXCEPTION 'PRC-POLICY-VERSION-PER-ORG requires the canonical tariff_rules FORCE RLS tenant policy';
          END IF;

          -- The frozen copy the quote engine writes and the order copies.
          IF NOT EXISTS (
            SELECT 1 FROM information_schema.columns
            WHERE table_schema='pricing' AND table_name='quotes' AND column_name='pricing_policy_version'
              AND data_type='text' AND is_nullable='NO') THEN
            RAISE EXCEPTION 'pricing.quotes.pricing_policy_version must be text NOT NULL as in AI-06';
          END IF;

          SELECT data_type INTO v_type
          FROM information_schema.columns
          WHERE table_schema='pricing' AND table_name='tariff_rules' AND column_name='policy_version';
          IF v_type IS NULL THEN
            ALTER TABLE pricing.tariff_rules ADD COLUMN policy_version text;
          ELSIF v_type <> 'text' THEN
            RAISE EXCEPTION 'pricing.tariff_rules.policy_version does not match the canonical AI-06 contract';
          END IF;

          -- Format: validated against every existing row (a NULL satisfies it).
          SELECT pg_get_constraintdef(oid) INTO v_definition
          FROM pg_constraint
          WHERE conrelid='pricing.tariff_rules'::regclass AND conname='tariff_rules_policy_version_check';
          IF v_definition IS NULL THEN
            ALTER TABLE pricing.tariff_rules ADD CONSTRAINT tariff_rules_policy_version_check
              CHECK (policy_version ~ '^[A-Za-z0-9._-]{1,64}$');
          ELSIF v_definition IS DISTINCT FROM 'CHECK ((policy_version ~ ''^[A-Za-z0-9._-]{1,64}$''::text))' THEN
            RAISE EXCEPTION 'tariff_rules_policy_version_check does not match the canonical AI-06 contract';
          END IF;

          SELECT is_nullable INTO v_nullable
          FROM information_schema.columns
          WHERE table_schema='pricing' AND table_name='tariff_rules' AND column_name='policy_version';
          IF v_nullable = 'YES' THEN
            -- SET NOT NULL checks every row, whatever row level security would show this role, so
            -- it succeeds exactly when no rule lacks a version. No row is read or rewritten here.
            BEGIN
              ALTER TABLE pricing.tariff_rules ALTER COLUMN policy_version SET NOT NULL;
            EXCEPTION WHEN not_null_violation THEN
              SELECT pg_get_constraintdef(oid) INTO v_definition
              FROM pg_constraint
              WHERE conrelid='pricing.tariff_rules'::regclass AND conname='tariff_rules_policy_version_required';
              IF v_definition IS NULL THEN
                ALTER TABLE pricing.tariff_rules ADD CONSTRAINT tariff_rules_policy_version_required
                  CHECK (policy_version IS NOT NULL) NOT VALID;
              ELSIF v_definition NOT IN (
                'CHECK ((policy_version IS NOT NULL)) NOT VALID',
                'CHECK ((policy_version IS NOT NULL))') THEN
                RAISE EXCEPTION 'tariff_rules_policy_version_required does not match PRC-POLICY-VERSION-PER-ORG';
              END IF;
              RAISE NOTICE 'PRC-POLICY-VERSION-PER-ORG: existing tariff rules have no policy_version; they are not quotable until their organization sets one, and every new or updated rule must carry one.';
            END;
          END IF;

          SELECT is_nullable INTO v_nullable
          FROM information_schema.columns
          WHERE table_schema='pricing' AND table_name='tariff_rules' AND column_name='policy_version';
          IF v_nullable = 'NO' THEN
            -- Every rule carries a version: NOT NULL supersedes the transitional guard.
            ALTER TABLE pricing.tariff_rules DROP CONSTRAINT IF EXISTS tariff_rules_policy_version_required;
          ELSIF NOT EXISTS (
            SELECT 1 FROM pg_constraint
            WHERE conrelid='pricing.tariff_rules'::regclass AND conname='tariff_rules_policy_version_required'
              AND contype='c') THEN
            RAISE EXCEPTION 'pricing.tariff_rules.policy_version is neither NOT NULL nor guarded for new rows';
          END IF;
        END
        $prc_policy_version$;
        """;

    public const string DownSql =
        """
        DO $prc_policy_version_downgrade$
        BEGIN
          IF to_regclass('pricing.tariff_rules') IS NULL THEN
            RAISE EXCEPTION 'PRC-POLICY-VERSION-PER-ORG rollback requires the canonical AI-06 tariff_rules table';
          END IF;

          IF EXISTS (
            SELECT 1 FROM information_schema.columns
            WHERE table_schema='pricing' AND table_name='tariff_rules' AND column_name='policy_version') THEN
            -- Probe every row regardless of row level security: the probe only validates when no rule
            -- carries a version, so the rollback never discards an organization's policy history.
            ALTER TABLE pricing.tariff_rules DROP CONSTRAINT IF EXISTS tariff_rules_prc_policy_downgrade_probe;
            BEGIN
              ALTER TABLE pricing.tariff_rules ADD CONSTRAINT tariff_rules_prc_policy_downgrade_probe
                CHECK (policy_version IS NULL);
            EXCEPTION WHEN check_violation THEN
              RAISE EXCEPTION USING
                MESSAGE = 'PRC_POLICY_VERSION_DOWNGRADE_BLOCKED',
                DETAIL = 'Tariff rules carry pricing policy versions; rolling PRC-POLICY-VERSION-PER-ORG back would discard them.',
                ERRCODE = 'P0001';
            END;
            ALTER TABLE pricing.tariff_rules DROP CONSTRAINT tariff_rules_prc_policy_downgrade_probe;
            ALTER TABLE pricing.tariff_rules DROP CONSTRAINT IF EXISTS tariff_rules_policy_version_required;
            ALTER TABLE pricing.tariff_rules DROP CONSTRAINT IF EXISTS tariff_rules_policy_version_check;
            ALTER TABLE pricing.tariff_rules DROP COLUMN policy_version;
          END IF;
        END
        $prc_policy_version_downgrade$;
        """;

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(UpSql);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(DownSql);
}
