using System.Reflection;
using Paqueteria.ArchitectureTests.Architecture;
using Paqueteria.Infrastructure.Database;

namespace Paqueteria.ArchitectureTests;

public sealed class DatabaseSchemaCatalogTests
{
    private static readonly string[] ExpectedModules =
    [
        "Identity", "Organizations", "Clients", "Locations", "Pricing", "Orders", "Dispatch", "Drivers",
        "Routes", "Custody", "Incidents", "Finance", "Allies", "Notifications", "Reporting",
    ];

    private static readonly string[] ExpectedSchemas =
    [
        "identity", "organizations", "clients", "locations", "pricing", "orders", "dispatch", "drivers",
        "routes", "custody", "incidents", "finance", "allies", "notifications", "reporting",
    ];

    [Fact]
    public void Normative_modules_and_schemas_are_bijective_immutable_and_conventional()
    {
        Assert.Equal(ExpectedModules, DatabaseSchemaCatalog.Modules.Select(item => item.Module));
        Assert.Equal(ExpectedSchemas, DatabaseSchemaCatalog.Modules.Select(item => item.Schema));
        Assert.Equal(ExpectedModules.Length, DatabaseSchemaCatalog.Modules.Select(item => item.Module).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(ExpectedSchemas.Length, DatabaseSchemaCatalog.Modules.Select(item => item.Schema).Distinct(StringComparer.Ordinal).Count());
        Assert.All(DatabaseSchemaCatalog.Modules, item => Assert.True(IsLowerSnakeCase(item.Schema), item.Schema));
        Assert.Equal(["platform", "security", "extensions"], DatabaseSchemaCatalog.SharedSchemas);
        Assert.DoesNotContain(DatabaseSchemaCatalog.SharedSchemas, schema => ExpectedSchemas.Contains(schema, StringComparer.Ordinal));
        Assert.Equal("public", DatabaseSchemaCatalog.ExternalPostGisSchema);
    }

    [Fact]
    public void Ai06_creates_exactly_the_cataloged_application_schemas_and_keeps_postgis_public()
    {
        var sql = File.ReadAllText(TestRepository.GetPath("docs/normative/v0.6/database/AI-06_SCHEMA.sql"));
        var created = ParseCreatedSchemas(sql);

        Assert.Equal(DatabaseSchemaCatalog.ApplicationSchemas.Order(StringComparer.Ordinal), created.Order(StringComparer.Ordinal));
        Assert.Contains("CREATE EXTENSION IF NOT EXISTS postgis;", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE EXTENSION IF NOT EXISTS postgis WITH SCHEMA", sql, StringComparison.Ordinal);
        Assert.Contains("CREATE EXTENSION IF NOT EXISTS pgcrypto WITH SCHEMA extensions;", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Ai18_covers_every_cataloged_schema_and_module_default_privilege_kind()
    {
        var sql = File.ReadAllText(TestRepository.GetPath("docs/normative/v0.6/database/AI-18_DATABASE_ROLE_MODEL.sql"));
        var literals = ParseSqlStringLiterals(sql).ToHashSet(StringComparer.Ordinal);

        Assert.All(DatabaseSchemaCatalog.ApplicationSchemas, schema => Assert.Contains(schema, literals));
        Assert.Contains("ALTER DEFAULT PRIVILEGES FOR ROLE paqueteria_migrator IN SCHEMA %I GRANT SELECT,INSERT,UPDATE,DELETE ON TABLES TO paqueteria_app,paqueteria_worker", sql, StringComparison.Ordinal);
        Assert.Contains("ALTER DEFAULT PRIVILEGES FOR ROLE paqueteria_migrator IN SCHEMA %I GRANT USAGE,SELECT ON SEQUENCES TO paqueteria_app,paqueteria_worker", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Catalog_is_framework_host_and_concrete_module_independent()
    {
        var references = typeof(DatabaseSchemaCatalog).Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();
        Assert.DoesNotContain(references, name => name is "Paqueteria.Api" or "Paqueteria.Worker");
        Assert.DoesNotContain(references, name => name.StartsWith("Identity.", StringComparison.Ordinal) ||
            name.StartsWith("Orders.", StringComparison.Ordinal) ||
            name.StartsWith("Pricing.", StringComparison.Ordinal));
        Assert.DoesNotContain(typeof(DatabaseSchemaCatalog).GetCustomAttributesData(), attribute =>
            attribute.AttributeType.FullName?.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Module_sources_do_not_introduce_uncataloged_schema_names()
    {
        var allowed = DatabaseSchemaCatalog.ApplicationSchemas
            .Append(DatabaseSchemaCatalog.ExternalPostGisSchema)
            .ToHashSet(StringComparer.Ordinal);
        var violations = new List<string>();
        foreach (var file in Directory.GetFiles(TestRepository.GetPath("src/Modules"), "*.cs", SearchOption.AllDirectories))
        {
            foreach (var line in File.ReadLines(file))
            {
                foreach (var candidate in ParseDeclaredSchemas(line))
                {
                    if (!allowed.Contains(candidate))
                    {
                        violations.Add($"{Path.GetRelativePath(TestRepository.Root, file)}: {candidate}");
                    }
                }
            }
        }

        Assert.Empty(violations);
    }

    private static IEnumerable<string> ParseDeclaredSchemas(string line)
    {
        const string defaultMarker = "HasDefaultSchema(\"";
        var defaultStart = line.IndexOf(defaultMarker, StringComparison.Ordinal);
        if (defaultStart >= 0)
        {
            var valueStart = defaultStart + defaultMarker.Length;
            var valueEnd = line.IndexOf('"', valueStart);
            if (valueEnd > valueStart)
            {
                yield return line[valueStart..valueEnd];
            }
        }

        const string tableMarker = "ToTable(\"";
        var tableStart = line.IndexOf(tableMarker, StringComparison.Ordinal);
        if (tableStart < 0)
        {
            yield break;
        }

        var separator = line.IndexOf("\", \"", tableStart + tableMarker.Length, StringComparison.Ordinal);
        if (separator < 0)
        {
            yield break;
        }

        var schemaStart = separator + 4;
        var schemaEnd = line.IndexOf('"', schemaStart);
        if (schemaEnd > schemaStart)
        {
            yield return line[schemaStart..schemaEnd];
        }
    }

    private static IReadOnlyList<string> ParseCreatedSchemas(string sql) =>
        sql.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("CREATE SCHEMA IF NOT EXISTS ", StringComparison.Ordinal))
            .Select(line => line["CREATE SCHEMA IF NOT EXISTS ".Length..].TrimEnd(';', '\r'))
            .ToArray();

    private static IEnumerable<string> ParseSqlStringLiterals(string sql)
    {
        for (var index = 0; index < sql.Length; index++)
        {
            if (sql[index] != '\'')
            {
                continue;
            }

            var value = new System.Text.StringBuilder();
            for (index++; index < sql.Length; index++)
            {
                if (sql[index] == '\'' && index + 1 < sql.Length && sql[index + 1] == '\'')
                {
                    value.Append('\'');
                    index++;
                    continue;
                }

                if (sql[index] == '\'')
                {
                    break;
                }

                value.Append(sql[index]);
            }

            yield return value.ToString();
        }
    }

    private static bool IsLowerSnakeCase(string value) =>
        value.Length != 0 &&
        char.IsLower(value[0]) &&
        value.All(character => char.IsLower(character) || char.IsDigit(character) || character == '_');
}
