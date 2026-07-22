using System.Collections.ObjectModel;

namespace Paqueteria.Infrastructure.Database;

public sealed record DatabaseModuleSchema(string Module, string Schema);

public static class DatabaseSchemaCatalog
{
    private static readonly ReadOnlyCollection<DatabaseModuleSchema> ModulesValue = Array.AsReadOnly(
    new DatabaseModuleSchema[]
    {
        new("Identity", "identity"),
        new("Organizations", "organizations"),
        new("Clients", "clients"),
        new("Locations", "locations"),
        new("Pricing", "pricing"),
        new("Orders", "orders"),
        new("Dispatch", "dispatch"),
        new("Drivers", "drivers"),
        new("Routes", "routes"),
        new("Custody", "custody"),
        new("Incidents", "incidents"),
        new("Finance", "finance"),
        new("Allies", "allies"),
        new("Notifications", "notifications"),
        new("Reporting", "reporting"),
    });

    private static readonly ReadOnlyCollection<string> SharedSchemasValue = Array.AsReadOnly(
    new[]
    {
        "platform",
        "security",
        "extensions",
    });

    private static readonly ReadOnlyCollection<string> ApplicationSchemasValue = Array.AsReadOnly(
        ModulesValue.Select(item => item.Schema).Concat(SharedSchemasValue).ToArray());

    public const string ExternalPostGisSchema = "public";

    public static IReadOnlyList<DatabaseModuleSchema> Modules => ModulesValue;

    public static IReadOnlyList<string> SharedSchemas => SharedSchemasValue;

    public static IReadOnlyList<string> ApplicationSchemas => ApplicationSchemasValue;
}
