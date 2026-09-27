namespace Paqueteria.Infrastructure.Database.Baseline;

public static class CanonicalBaselineContract
{
    public const string BaselineVersion = "v0.6";
    public const string ManifestRelativePath = "database/migrations/v0.6-baseline.json";
    public const string SchemaRelativePath = "docs/normative/v0.6/database/AI-06_SCHEMA.sql";
    public const string RolesRelativePath = "docs/normative/v0.6/database/AI-18_DATABASE_ROLE_MODEL.sql";
    public const string SchemaSha256 = "1b7729b8901aadd362bf35cd144349ae25471c933967af901b62591ab795091c";
    public const string RolesSha256 = "e8dcd7ab4030dab79baf216166fe02181ae2b8fe667c500d41b115590a9fa3ac";
    public const string DeploymentCredential = "privileged-deployment";
    public const long AdvisoryLockKey = 5_783_000_321_440_564_562L;
}
