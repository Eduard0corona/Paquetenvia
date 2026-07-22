using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Identity.Infrastructure.Persistence.Migrations;

[DbContext(typeof(IdentityDbContext))]
[Migration(MigrationId)]
public sealed class AdoptCanonicalIdentityBaseline : Migration
{
    public const string MigrationId = "20260722_AdoptCanonicalIdentityBaseline";

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(
        """
        DO $adoption$
        BEGIN
          IF to_regclass('identity.users') IS NULL THEN
            RAISE EXCEPTION 'TEN-001 adoption requires identity.users from AI-06';
          END IF;
          IF NOT EXISTS (
            SELECT 1 FROM information_schema.columns
            WHERE table_schema='identity' AND table_name='users' AND column_name='id' AND data_type='uuid'
          ) OR NOT EXISTS (
            SELECT 1 FROM information_schema.columns
            WHERE table_schema='identity' AND table_name='users' AND column_name='identity_subject' AND data_type='text' AND is_nullable='NO'
          ) OR NOT EXISTS (
            SELECT 1 FROM information_schema.columns
            WHERE table_schema='identity' AND table_name='users' AND column_name='email_ciphertext' AND data_type='bytea'
          ) OR NOT EXISTS (
            SELECT 1 FROM information_schema.columns
            WHERE table_schema='identity' AND table_name='users' AND column_name='status' AND data_type='text' AND is_nullable='NO'
          ) OR NOT EXISTS (
            SELECT 1 FROM information_schema.columns
            WHERE table_schema='identity' AND table_name='users' AND column_name='created_at' AND data_type='timestamp with time zone' AND is_nullable='NO'
          ) THEN
            RAISE EXCEPTION 'identity.users does not match the canonical AI-06 shape';
          END IF;
        END
        $adoption$;
        """);

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Adoption is intentionally non-destructive. Canonical AI-06 objects are never dropped here.
    }
}
