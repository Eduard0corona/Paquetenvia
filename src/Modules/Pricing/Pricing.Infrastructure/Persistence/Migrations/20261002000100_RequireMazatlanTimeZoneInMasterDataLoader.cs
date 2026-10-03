using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pricing.Infrastructure.Persistence.Migrations;

/// <summary>
/// MDM-001-TZ-MAZATLAN-ONLY-2026-10-02 (owner literal: "Solo America/Mazatlan"). In the pilot every city carries
/// the <c>America/Mazatlan</c> time zone, so <c>security.load_master_data</c> refuses a city entry with any other
/// zone of the Mexican allowlist (<c>MDM001_CITY_TIMEZONE_NOT_IN_PILOT</c>, also in dry run, before anything is
/// written). This migration:
/// <list type="bullet">
/// <item>replaces the function with a body derived from the one installed by
/// <see cref="RequireVatIncludedTariffsInMasterDataLoader"/> by the exact, reviewed edit in
/// <see cref="FunctionEdits"/> (it must match exactly once or the migration refuses to build its SQL). A zone
/// outside the Mexican allowlist keeps failing first with <c>MDM001_CITY_TIMEZONE_NOT_ALLOWED</c>;</item>
/// <item>rewrites no stored row: an existing city keeps its zone and stays referenceable by service areas,
/// zones, tariffs and drivers, which name a city by its natural key and never carry a zone;</item>
/// <item>adds no grant, table or role: the executor keeps the 122 column grants of the hardening step, whose
/// ACL and verification are re-applied unchanged.</item>
/// </list>
/// The rollback restores the VAT_INCLUDED step's function, ACL and verification; cities loaded meanwhile stay.
/// </summary>
[DbContext(typeof(PricingDbContext))]
[Migration(MigrationId)]
public sealed class RequireMazatlanTimeZoneInMasterDataLoader : Migration
{
    public const string MigrationId = "20261002000100_RequireMazatlanTimeZoneInMasterDataLoader";

    public const string TimeZoneNotInPilot = "MDM001_CITY_TIMEZONE_NOT_IN_PILOT";

    public const string PilotTimeZone = "America/Mazatlan";

    public const string DecisionId = "MDM-001-TZ-MAZATLAN-ONLY-2026-10-02";

    /// <summary>The only difference from the previous loader function, as (previous text, new text).</summary>
    public static IReadOnlyList<(string Published, string Replacement)> FunctionEdits { get; } = Array.AsReadOnly(new[]
    {
        (
            "      RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'MDM001_CITY_TIMEZONE_NOT_ALLOWED', HINT = v_ref;\n" +
            "    END IF;\n" +
            "    v_key := v_country || '|' || v_state || '|' || v_name;\n",
            "      RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'MDM001_CITY_TIMEZONE_NOT_ALLOWED', HINT = v_ref;\n" +
            "    END IF;\n" +
            "    -- MDM-001-TZ-MAZATLAN-ONLY-2026-10-02: the pilot accepts only America/Mazatlan for a city entry.\n" +
            "    -- A stored city is never rewritten and stays referenceable by its natural key.\n" +
            "    IF v_timezone IS DISTINCT FROM 'America/Mazatlan' THEN\n" +
            "      RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'MDM001_CITY_TIMEZONE_NOT_IN_PILOT', HINT = v_ref;\n" +
            "    END IF;\n" +
            "    v_key := v_country || '|' || v_state || '|' || v_name;\n"
        ),
    });

    /// <summary>The function definition installed by the previous step (CREATE OR REPLACE ... $function$;).</summary>
    public static string PreviousFunctionSql => RequireVatIncludedTariffsInMasterDataLoader.FunctionSql;

    /// <summary>The function definition this migration installs.</summary>
    public static string FunctionSql { get; } = FunctionEdits.Aggregate(
        PreviousFunctionSql,
        (sql, edit) => ReplaceOnce(sql, edit.Published, edit.Replacement));

    public static string UpSql { get; } = string.Concat(
        """
        RESET ROLE;

        DO $mdm001_tz_adoption$
        BEGIN
          IF to_regprocedure('security.load_master_data(uuid,uuid,json,bytea,boolean)') IS NULL
             OR position('MDM001_TARIFF_TAX_MODE_NOT_ALLOWED' IN (SELECT prosrc FROM pg_proc
                 WHERE oid=to_regprocedure('security.load_master_data(uuid,uuid,json,bytea,boolean)'))) = 0
             OR position('MDM001_DEPLOYMENT_PRINCIPAL_REFUSED' IN (SELECT prosrc FROM pg_proc
                 WHERE oid=to_regprocedure('security.load_master_data(uuid,uuid,json,bytea,boolean)'))) = 0 THEN
            RAISE EXCEPTION 'MDM-001 America/Mazatlan loader step requires the GATE-011 VAT_INCLUDED loader function';
          END IF;
        END
        $mdm001_tz_adoption$;

        -- As in the previous steps: the ungated jsonb overload of the first published version must never stay
        -- callable, and the verification below requires the executor to own exactly one function.

        """,
        StoreTariffPolicyVersionInMasterDataLoader.LegacyOverloadDrop,
        "\n\n",
        FunctionSql,
        "\n",
        StoreTariffPolicyVersionInMasterDataLoader.FunctionAclSql,
        "\n",
        HardenMasterDataLoaderOperatorBoundary.VerifySql,
        """

        DO $mdm001_tz_verify$
        BEGIN
          IF position('MDM001_CITY_TIMEZONE_NOT_IN_PILOT' IN (SELECT prosrc FROM pg_proc
                 WHERE oid=to_regprocedure('security.load_master_data(uuid,uuid,json,bytea,boolean)'))) = 0
             OR position('MDM001_TARIFF_TAX_MODE_NOT_ALLOWED' IN (SELECT prosrc FROM pg_proc
                 WHERE oid=to_regprocedure('security.load_master_data(uuid,uuid,json,bytea,boolean)'))) = 0
             OR position('MDM001_DEPLOYMENT_PRINCIPAL_REFUSED' IN (SELECT prosrc FROM pg_proc
                 WHERE oid=to_regprocedure('security.load_master_data(uuid,uuid,json,bytea,boolean)'))) = 0 THEN
            RAISE EXCEPTION 'MDM-001 America/Mazatlan loader function differs from the contract';
          END IF;
        END
        $mdm001_tz_verify$;

        SET LOCAL ROLE paqueteria_migrator;
        """);

    /// <summary>
    /// Rollback: restores the VAT_INCLUDED step's function, ACL and verification. Cities loaded meanwhile (all
    /// America/Mazatlan) stay; the restored function accepts any zone of the Mexican allowlist again.
    /// </summary>
    public static string DownSql { get; } = string.Concat(
        """
        RESET ROLE;

        """,
        StoreTariffPolicyVersionInMasterDataLoader.LegacyOverloadDrop,
        "\n\n",
        PreviousFunctionSql,
        "\n",
        StoreTariffPolicyVersionInMasterDataLoader.FunctionAclSql,
        "\n",
        HardenMasterDataLoaderOperatorBoundary.VerifySql,
        """

        SET LOCAL ROLE paqueteria_migrator;
        """);

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(UpSql);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(DownSql);

    private static string ReplaceOnce(string sql, string published, string replacement)
    {
        var at = sql.IndexOf(published, StringComparison.Ordinal);
        if (at < 0 || sql.IndexOf(published, at + published.Length, StringComparison.Ordinal) >= 0)
        {
            throw new InvalidOperationException(
                $"MDM-001 America/Mazatlan loader step: an edit must match the previous function exactly once: '{published.Trim()}'.");
        }

        return string.Concat(sql.AsSpan(0, at), replacement, sql.AsSpan(at + published.Length));
    }
}
