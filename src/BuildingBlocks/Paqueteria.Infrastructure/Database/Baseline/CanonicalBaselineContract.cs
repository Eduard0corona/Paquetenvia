namespace Paqueteria.Infrastructure.Database.Baseline;

public static class CanonicalBaselineContract
{
    public const string BaselineVersion = "v0.6";
    public const string ManifestRelativePath = "database/migrations/v0.6-baseline.json";
    public const string SchemaRelativePath = "docs/normative/v0.6/database/AI-06_SCHEMA.sql";
    public const string RolesRelativePath = "docs/normative/v0.6/database/AI-18_DATABASE_ROLE_MODEL.sql";
    public const string SchemaSha256 = "39c24d416309b5ec40279c0a58d04a71e39e096fe550fcc88ea62faae6b7eba7";
    public const string RolesSha256 = "b1fb898d8e4980640c27bdb8f68a8bb844f8b9bc0a37399b637e7b65e81fd8ef";
    public const string DeploymentCredential = "privileged-deployment";
    public const long AdvisoryLockKey = 5_783_000_321_440_564_562L;
}
