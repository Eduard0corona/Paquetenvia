using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orders.Infrastructure.Persistence.Migrations;

/// <summary>
/// TRK-002-AUTO-LINK: public tracking links are derived from the order, a generation and a key version, so
/// <c>orders.public_tracking_tokens</c> gains <c>generation</c> (starting at 1; a revoked generation is never
/// derived again) and <c>key_version</c> (the Key Vault key version; NULL marks a random token issued before this
/// decision), plus two partial unique indexes over derived rows: one row per generation and at most one live link
/// per order. AI-06 already carries the same shape on fresh installations, so the lane adopts it when present and
/// refuses any other shape. Existing rows keep their values: they read as generation 1 without a key version, and
/// neither index covers them. Nothing is dropped and no row is rewritten.
/// </summary>
[DbContext(typeof(OrdersDbContext))]
[Migration(MigrationId)]
public sealed class AddTrackingLinkGenerations : Migration
{
    public const string MigrationId = "20260929000100_AddTrackingLinkGenerations";

    /// <summary>Both indexes this step owns, exactly as <c>pg_get_indexdef</c> renders them.</summary>
    public static readonly IReadOnlyList<(string Name, string Definition)> Indexes =
    [
        ("orders.tracking_tokens_order_generation_uq",
            "CREATE UNIQUE INDEX tracking_tokens_order_generation_uq ON orders.public_tracking_tokens USING btree (order_id, generation) WHERE (key_version IS NOT NULL)"),
        ("orders.tracking_tokens_one_active_derived_uq",
            "CREATE UNIQUE INDEX tracking_tokens_one_active_derived_uq ON orders.public_tracking_tokens USING btree (order_id) WHERE ((key_version IS NOT NULL) AND (revoked_at IS NULL))"),
    ];

    public const string UpSql =
        """
        DO $adoption$
        DECLARE
          token_columns text[];
        BEGIN
          IF to_regclass('orders.public_tracking_tokens') IS NULL THEN
            RAISE EXCEPTION 'TRK-002-AUTO-LINK requires the canonical AI-06 orders.public_tracking_tokens table';
          END IF;

          SELECT array_agg(column_name || ':' || data_type || ':' || is_nullable ORDER BY column_name)
          INTO token_columns
          FROM information_schema.columns
          WHERE table_schema='orders' AND table_name='public_tracking_tokens'
            AND column_name IN ('created_at','expires_at','id','order_id','owner_org_id','revoked_at','token_hash');
          IF token_columns IS DISTINCT FROM ARRAY[
            'created_at:timestamp with time zone:NO',
            'expires_at:timestamp with time zone:NO',
            'id:uuid:NO',
            'order_id:uuid:NO',
            'owner_org_id:uuid:NO',
            'revoked_at:timestamp with time zone:YES',
            'token_hash:bytea:NO'
          ] THEN
            RAISE EXCEPTION 'orders.public_tracking_tokens columns do not match the canonical AI-06 contract';
          END IF;

          IF NOT EXISTS (
            SELECT 1 FROM pg_class
            WHERE oid='orders.public_tracking_tokens'::regclass AND relrowsecurity AND relforcerowsecurity
          ) THEN
            RAISE EXCEPTION 'TRK-002-AUTO-LINK requires FORCE ROW LEVEL SECURITY on orders.public_tracking_tokens';
          END IF;
        END
        $adoption$;

        ALTER TABLE orders.public_tracking_tokens
          ADD COLUMN IF NOT EXISTS generation integer NOT NULL DEFAULT 1
            CONSTRAINT public_tracking_tokens_generation_check CHECK (generation >= 1),
          ADD COLUMN IF NOT EXISTS key_version integer
            CONSTRAINT public_tracking_tokens_key_version_check
            CHECK (key_version IS NULL OR key_version BETWEEN 1 AND 32767);

        CREATE UNIQUE INDEX IF NOT EXISTS tracking_tokens_order_generation_uq
          ON orders.public_tracking_tokens(order_id,generation) WHERE key_version IS NOT NULL;
        CREATE UNIQUE INDEX IF NOT EXISTS tracking_tokens_one_active_derived_uq
          ON orders.public_tracking_tokens(order_id) WHERE key_version IS NOT NULL AND revoked_at IS NULL;

        DO $verify$
        BEGIN
          IF (SELECT array_agg(column_name || ':' || data_type || ':' || is_nullable || ':' || COALESCE(column_default,'')
                ORDER BY column_name)
              FROM information_schema.columns
              WHERE table_schema='orders' AND table_name='public_tracking_tokens'
                AND column_name IN ('generation','key_version'))
             IS DISTINCT FROM ARRAY['generation:integer:NO:1','key_version:integer:YES:']
          THEN
            RAISE EXCEPTION 'orders.public_tracking_tokens generation columns differ from the TRK-002-AUTO-LINK contract';
          END IF;

          IF (SELECT array_agg(conname || '=' || pg_get_constraintdef(oid) ORDER BY conname)
              FROM pg_constraint
              WHERE conrelid='orders.public_tracking_tokens'::regclass
                AND conname IN ('public_tracking_tokens_generation_check','public_tracking_tokens_key_version_check'))
             IS DISTINCT FROM ARRAY[
               'public_tracking_tokens_generation_check=CHECK ((generation >= 1))',
               'public_tracking_tokens_key_version_check=CHECK (((key_version IS NULL) OR ((key_version >= 1) AND (key_version <= 32767))))'
             ]
          THEN
            RAISE EXCEPTION 'orders.public_tracking_tokens generation checks differ from the TRK-002-AUTO-LINK contract';
          END IF;

          IF (SELECT count(*)
              FROM (VALUES
                ('orders.tracking_tokens_order_generation_uq',
                 'CREATE UNIQUE INDEX tracking_tokens_order_generation_uq ON orders.public_tracking_tokens USING btree (order_id, generation) WHERE (key_version IS NOT NULL)'),
                ('orders.tracking_tokens_one_active_derived_uq',
                 'CREATE UNIQUE INDEX tracking_tokens_one_active_derived_uq ON orders.public_tracking_tokens USING btree (order_id) WHERE ((key_version IS NOT NULL) AND (revoked_at IS NULL))')
              ) expected(name, definition)
              JOIN pg_index i ON i.indexrelid = to_regclass(expected.name)
              WHERE i.indisvalid AND i.indisready AND i.indisunique
                AND pg_get_indexdef(i.indexrelid) = expected.definition) <> 2
          THEN
            RAISE EXCEPTION 'orders.public_tracking_tokens generation indexes differ from the TRK-002-AUTO-LINK contract';
          END IF;

          IF NOT EXISTS (
            SELECT 1 FROM pg_class
            WHERE oid='orders.public_tracking_tokens'::regclass AND relrowsecurity AND relforcerowsecurity
          ) THEN
            RAISE EXCEPTION 'TRK-002-AUTO-LINK requires FORCE ROW LEVEL SECURITY on orders.public_tracking_tokens';
          END IF;
        END
        $verify$;
        """;

    /// <summary>
    /// The schema is never downgraded: the generation of every derived link is what keeps a revoked link from
    /// being derived again, and AI-06 carries both columns on fresh installations. The previous release reads and
    /// writes the table unchanged (both columns have defaults), so an application rollback needs no schema change.
    /// </summary>
    public const string SchemaDowngradeNotSupportedSql =
        """
        DO $schema_downgrade$
        BEGIN
          RAISE EXCEPTION USING
            MESSAGE = 'TRK002_GENERATION_DOWNGRADE_NOT_SUPPORTED',
            DETAIL = 'TRK-002-AUTO-LINK rollback blocked: the previous release works with generation and key_version in place; generations are never removed.',
            ERRCODE = 'P0001';
        END
        $schema_downgrade$;
        """;

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(UpSql);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(SchemaDowngradeNotSupportedSql);
}
