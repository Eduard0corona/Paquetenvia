using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pricing.Infrastructure.Persistence.Migrations;

/// <summary>
/// GATE-011-VAT-INCLUDED-2026-09-29 (owner literals: "Presentación de impuestos, IVA incluido"; "Igual para
/// todas"). The pilot presents every price with IVA included, the same for every organization, so
/// <c>security.load_master_data</c> refuses to create a tariff rule whose <c>tax_mode</c> is not
/// <c>VAT_INCLUDED</c> (<c>MDM001_TARIFF_TAX_MODE_NOT_ALLOWED</c>). This migration:
/// <list type="bullet">
/// <item>replaces the function with a body derived from the one installed by
/// <see cref="HardenMasterDataLoaderOperatorBoundary"/> by the exact, reviewed edit in
/// <see cref="FunctionEdits"/> (it must match exactly once or the migration refuses to build its SQL). The
/// refusal applies only to a rule the load would create: a stored <c>PLUS_VAT</c> or <c>EXEMPT</c> rule can
/// still be reloaded to close (<c>active_to</c>) or deactivate it, and its amount and tax mode stay immutable
/// as before. No stored rule, quote or order is rewritten: frozen snapshots are never recomputed;</item>
/// <item>keeps the AI-06 <c>tax_mode</c> vocabulary and CHECK (<c>PLUS_VAT</c>, <c>VAT_INCLUDED</c>,
/// <c>EXEMPT</c>) and adds no grant: the executor keeps the 122 column grants of the hardening step, whose
/// ACL and verification are re-applied unchanged.</item>
/// </list>
/// The rollback restores the hardening step's function, ACL and verification; rules loaded meanwhile stay.
/// </summary>
[DbContext(typeof(PricingDbContext))]
[Migration(MigrationId)]
public sealed class RequireVatIncludedTariffsInMasterDataLoader : Migration
{
    public const string MigrationId = "20260929000200_RequireVatIncludedTariffsInMasterDataLoader";

    public const string TaxModeNotAllowed = "MDM001_TARIFF_TAX_MODE_NOT_ALLOWED";

    public const string DecisionId = "GATE-011-VAT-INCLUDED-2026-09-29";

    /// <summary>The only difference from the previous loader function, as (previous text, new text).</summary>
    public static IReadOnlyList<(string Published, string Replacement)> FunctionEdits { get; } = Array.AsReadOnly(new[]
    {
        (
            "    IF v_id IS NULL THEN\n" +
            "      v_id := pg_catalog.gen_random_uuid();\n" +
            "      v_action := 'CREATE';\n" +
            "    ELSE\n" +
            "      -- A published price is never rewritten: a new price is a new rule with a later active_from.\n",
            "    IF v_id IS NULL THEN\n" +
            "      v_id := pg_catalog.gen_random_uuid();\n" +
            "      v_action := 'CREATE';\n" +
            "      -- GATE-011-VAT-INCLUDED-2026-09-29: prices are presented with IVA included, the same for every\n" +
            "      -- organization, so a new rule is VAT_INCLUDED only. A stored PLUS_VAT or EXEMPT rule may still be\n" +
            "      -- reloaded to close or deactivate it; its tax mode is never rewritten.\n" +
            "      IF v_tax_mode IS DISTINCT FROM 'VAT_INCLUDED' THEN\n" +
            "        RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'MDM001_TARIFF_TAX_MODE_NOT_ALLOWED', HINT = v_ref;\n" +
            "      END IF;\n" +
            "    ELSE\n" +
            "      -- A published price is never rewritten: a new price is a new rule with a later active_from.\n"
        ),
    });

    /// <summary>The function definition installed by the previous step (CREATE OR REPLACE ... $function$;).</summary>
    public static string PreviousFunctionSql => HardenMasterDataLoaderOperatorBoundary.FunctionSql;

    /// <summary>The function definition this migration installs.</summary>
    public static string FunctionSql { get; } = FunctionEdits.Aggregate(
        PreviousFunctionSql,
        (sql, edit) => ReplaceOnce(sql, edit.Published, edit.Replacement));

    public static string UpSql { get; } = string.Concat(
        """
        RESET ROLE;

        DO $gate011_adoption$
        BEGIN
          IF to_regprocedure('security.load_master_data(uuid,uuid,json,bytea,boolean)') IS NULL
             OR position('MDM001_DEPLOYMENT_PRINCIPAL_REFUSED' IN (SELECT prosrc FROM pg_proc
                 WHERE oid=to_regprocedure('security.load_master_data(uuid,uuid,json,bytea,boolean)'))) = 0 THEN
            RAISE EXCEPTION 'GATE-011 VAT_INCLUDED loader step requires the hardened MDM-001 loader function';
          END IF;
        END
        $gate011_adoption$;

        -- As in the published step: the ungated jsonb overload of the first published version must never stay
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

        DO $gate011_verify$
        BEGIN
          IF position('MDM001_TARIFF_TAX_MODE_NOT_ALLOWED' IN (SELECT prosrc FROM pg_proc
                 WHERE oid=to_regprocedure('security.load_master_data(uuid,uuid,json,bytea,boolean)'))) = 0
             OR position('MDM001_DEPLOYMENT_PRINCIPAL_REFUSED' IN (SELECT prosrc FROM pg_proc
                 WHERE oid=to_regprocedure('security.load_master_data(uuid,uuid,json,bytea,boolean)'))) = 0 THEN
            RAISE EXCEPTION 'GATE-011 VAT_INCLUDED loader function differs from the contract';
          END IF;
        END
        $gate011_verify$;

        SET LOCAL ROLE paqueteria_migrator;
        """);

    /// <summary>
    /// Rollback: restores the hardening step's function, ACL and verification. Tariff rules loaded meanwhile
    /// (all VAT_INCLUDED) stay; the restored function accepts any AI-06 tax mode again for new rules, while the
    /// quote engine keeps quoting only VAT_INCLUDED rules.
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
                $"GATE-011 VAT_INCLUDED loader step: an edit must match the previous function exactly once: '{published.Trim()}'.");
        }

        return string.Concat(sql.AsSpan(0, at), replacement, sql.AsSpan(at + published.Length));
    }
}
