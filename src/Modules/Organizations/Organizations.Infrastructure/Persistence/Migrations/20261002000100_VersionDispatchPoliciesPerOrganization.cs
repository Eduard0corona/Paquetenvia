using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Organizations.Infrastructure.Persistence.Migrations;

/// <summary>
/// POLICY-VERSIONS-PER-ORG-2026-10-02 (owner, 2026-10-02: "Por empresa, piloto-2026-10-v1"). Each
/// organization carries the version of its own assignment policy and of its own driver eligibility
/// policy, in <c>organizations.organizations.assignment_policy_version</c> and
/// <c>organizations.organizations.driver_eligibility_policy_version</c>. There is no global
/// <c>Dispatch:AssignmentPolicyVersion</c> or <c>Drivers:Eligibility:PolicyVersion</c> setting any more.
/// <para>
/// Both columns are <c>text NOT NULL DEFAULT 'piloto-2026-10-v1'</c> with the same format CHECK as the
/// pricing policy version (<c>^[A-Za-z0-9._-]{1,64}$</c>). The default is the owner's literal starting
/// version: every existing organization gets it when the column is added (no row is read or rewritten;
/// PostgreSQL stores the default once), and every organization created later, by any path, starts there.
/// </para>
/// <para>
/// AI-06 already declares both columns on fresh installations; this lane adopts them there and creates them
/// on installations whose baseline predates the decision, refusing any shape that differs.
/// </para>
/// <para>
/// The rollback removes both columns and their checks, but refuses while any organization carries a version
/// other than the starting one: that would discard the organization's policy history.
/// </para>
/// </summary>
[DbContext(typeof(OrganizationsDbContext))]
[Migration(MigrationId)]
public sealed class VersionDispatchPoliciesPerOrganization : Migration
{
    public const string MigrationId = "20261002000100_VersionDispatchPoliciesPerOrganization";

    /// <summary>The owner's starting version for every organization (POLICY-VERSIONS-PER-ORG-2026-10-02).</summary>
    public const string InitialPolicyVersion = "piloto-2026-10-v1";

    public const string AssignmentColumn = "assignment_policy_version";

    public const string EligibilityColumn = "driver_eligibility_policy_version";

    public const string AssignmentFormatConstraint = "organizations_assignment_policy_version_check";

    public const string EligibilityFormatConstraint = "organizations_driver_eligibility_policy_version_check";

    public const string DowngradeBlocked = "POLICY_VERSIONS_PER_ORG_DOWNGRADE_BLOCKED";

    /// <summary>The assignment format CHECK exactly as <c>pg_get_constraintdef</c> renders it.</summary>
    public const string AssignmentFormatConstraintDefinition =
        "CHECK ((assignment_policy_version ~ '^[A-Za-z0-9._-]{1,64}$'::text))";

    /// <summary>The eligibility format CHECK exactly as <c>pg_get_constraintdef</c> renders it.</summary>
    public const string EligibilityFormatConstraintDefinition =
        "CHECK ((driver_eligibility_policy_version ~ '^[A-Za-z0-9._-]{1,64}$'::text))";

    /// <summary>The column default exactly as <c>information_schema.columns.column_default</c> renders it.</summary>
    public const string ColumnDefault = "'piloto-2026-10-v1'::text";

    public const string UpSql =
        """
        DO $policy_versions_per_org$
        DECLARE
          v_column text;
          v_type text;
          v_nullable text;
          v_default text;
          v_definition text;
        BEGIN
          IF to_regclass('organizations.organizations') IS NULL THEN
            RAISE EXCEPTION 'POLICY-VERSIONS-PER-ORG requires the canonical AI-06 organizations table';
          END IF;

          IF NOT EXISTS (
            SELECT 1 FROM pg_class
            WHERE oid='organizations.organizations'::regclass AND relrowsecurity AND relforcerowsecurity)
             OR NOT EXISTS (
            SELECT 1 FROM pg_policies
            WHERE schemaname='organizations' AND tablename='organizations' AND policyname='organizations_tenant') THEN
            RAISE EXCEPTION 'POLICY-VERSIONS-PER-ORG requires the canonical organizations FORCE RLS tenant policy';
          END IF;

          FOREACH v_column IN ARRAY ARRAY['assignment_policy_version','driver_eligibility_policy_version'] LOOP
            SELECT data_type, is_nullable, column_default INTO v_type, v_nullable, v_default
            FROM information_schema.columns
            WHERE table_schema='organizations' AND table_name='organizations' AND column_name=v_column;
            IF v_type IS NULL THEN
              -- NOT NULL with a constant default: every existing organization starts at the owner's version
              -- without any row being read or rewritten.
              EXECUTE format(
                'ALTER TABLE organizations.organizations ADD COLUMN %I text NOT NULL DEFAULT %L',
                v_column, 'piloto-2026-10-v1');
            ELSIF v_type <> 'text' OR v_nullable <> 'NO'
               OR v_default IS DISTINCT FROM '''piloto-2026-10-v1''::text' THEN
              RAISE EXCEPTION 'organizations.organizations.% does not match the canonical AI-06 contract', v_column;
            END IF;

            SELECT pg_get_constraintdef(oid) INTO v_definition
            FROM pg_constraint
            WHERE conrelid='organizations.organizations'::regclass AND conname='organizations_' || v_column || '_check';
            IF v_definition IS NULL THEN
              EXECUTE format(
                'ALTER TABLE organizations.organizations ADD CONSTRAINT %I CHECK (%I ~ %L)',
                'organizations_' || v_column || '_check', v_column, '^[A-Za-z0-9._-]{1,64}$');
            ELSIF v_definition IS DISTINCT FROM
              format('CHECK ((%s ~ %L::text))', v_column, '^[A-Za-z0-9._-]{1,64}$') THEN
              RAISE EXCEPTION 'organizations_%_check does not match the canonical AI-06 contract', v_column;
            END IF;
          END LOOP;
        END
        $policy_versions_per_org$;
        """;

    public const string DownSql =
        """
        DO $policy_versions_per_org_downgrade$
        BEGIN
          IF to_regclass('organizations.organizations') IS NULL THEN
            RAISE EXCEPTION 'POLICY-VERSIONS-PER-ORG rollback requires the canonical AI-06 organizations table';
          END IF;

          -- Probe every row regardless of row level security: the probe only validates when every organization
          -- still carries the starting version, so the rollback never discards an organization's policy history.
          ALTER TABLE organizations.organizations DROP CONSTRAINT IF EXISTS organizations_policy_versions_downgrade_probe;
          BEGIN
            IF EXISTS (
              SELECT 1 FROM information_schema.columns
              WHERE table_schema='organizations' AND table_name='organizations'
                AND column_name='assignment_policy_version') THEN
              ALTER TABLE organizations.organizations ADD CONSTRAINT organizations_policy_versions_downgrade_probe
                CHECK (assignment_policy_version = 'piloto-2026-10-v1');
              ALTER TABLE organizations.organizations DROP CONSTRAINT organizations_policy_versions_downgrade_probe;
            END IF;
            IF EXISTS (
              SELECT 1 FROM information_schema.columns
              WHERE table_schema='organizations' AND table_name='organizations'
                AND column_name='driver_eligibility_policy_version') THEN
              ALTER TABLE organizations.organizations ADD CONSTRAINT organizations_policy_versions_downgrade_probe
                CHECK (driver_eligibility_policy_version = 'piloto-2026-10-v1');
              ALTER TABLE organizations.organizations DROP CONSTRAINT organizations_policy_versions_downgrade_probe;
            END IF;
          EXCEPTION WHEN check_violation THEN
            RAISE EXCEPTION USING
              MESSAGE = 'POLICY_VERSIONS_PER_ORG_DOWNGRADE_BLOCKED',
              DETAIL = 'Organizations carry assignment or driver eligibility policy versions other than the starting one; rolling POLICY-VERSIONS-PER-ORG back would discard them.',
              ERRCODE = 'P0001';
          END;

          ALTER TABLE organizations.organizations DROP CONSTRAINT IF EXISTS organizations_assignment_policy_version_check;
          ALTER TABLE organizations.organizations DROP CONSTRAINT IF EXISTS organizations_driver_eligibility_policy_version_check;
          ALTER TABLE organizations.organizations DROP COLUMN IF EXISTS assignment_policy_version;
          ALTER TABLE organizations.organizations DROP COLUMN IF EXISTS driver_eligibility_policy_version;
        END
        $policy_versions_per_org_downgrade$;
        """;

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(UpSql);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(DownSql);
}
