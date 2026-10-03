namespace Paqueteria.Infrastructure.Database.Baseline;

public static class CanonicalBaselineContract
{
    public const string BaselineVersion = "v0.6";
    public const string ManifestRelativePath = "database/migrations/v0.6-baseline.json";
    public const string SchemaRelativePath = "docs/normative/v0.6/database/AI-06_SCHEMA.sql";
    public const string RolesRelativePath = "docs/normative/v0.6/database/AI-18_DATABASE_ROLE_MODEL.sql";
    public const string SchemaSha256 = "f069b455894eb825f252f59c1a50373cd6a0d13d50d6b4493c53024f0d4b7ba7";
    public const string RolesSha256 = "e13dc633db9a96565245f0f80917f006e5b7aaec01d549e1d01942a06ce4e759";
    public const string DeploymentCredential = "privileged-deployment";
    public const long AdvisoryLockKey = 5_783_000_321_440_564_562L;
}
