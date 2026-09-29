namespace Paqueteria.Infrastructure.Database.Baseline;

public static class CanonicalBaselineContract
{
    public const string BaselineVersion = "v0.6";
    public const string ManifestRelativePath = "database/migrations/v0.6-baseline.json";
    public const string SchemaRelativePath = "docs/normative/v0.6/database/AI-06_SCHEMA.sql";
    public const string RolesRelativePath = "docs/normative/v0.6/database/AI-18_DATABASE_ROLE_MODEL.sql";
    public const string SchemaSha256 = "4e441d1165c17611abe317811278eb2932c27758685d768629855ab78de39da4";
    public const string RolesSha256 = "53c486c12178a470aa35764be2b117105f3ac077f384f7f3d0b7cca01a2b810f";
    public const string DeploymentCredential = "privileged-deployment";
    public const long AdvisoryLockKey = 5_783_000_321_440_564_562L;
}
