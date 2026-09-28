namespace Paqueteria.Infrastructure.Database.Baseline;

public static class CanonicalBaselineContract
{
    public const string BaselineVersion = "v0.6";
    public const string ManifestRelativePath = "database/migrations/v0.6-baseline.json";
    public const string SchemaRelativePath = "docs/normative/v0.6/database/AI-06_SCHEMA.sql";
    public const string RolesRelativePath = "docs/normative/v0.6/database/AI-18_DATABASE_ROLE_MODEL.sql";
    public const string SchemaSha256 = "35f1c6e839b2efbe441bfc6e5acd7a3a291534822a1de7067dddd5f7530f88bc";
    public const string RolesSha256 = "060d248312365769471ffb95637ac6d787271a0445957c2538d660dee3d9b655";
    public const string DeploymentCredential = "privileged-deployment";
    public const long AdvisoryLockKey = 5_783_000_321_440_564_562L;
}
