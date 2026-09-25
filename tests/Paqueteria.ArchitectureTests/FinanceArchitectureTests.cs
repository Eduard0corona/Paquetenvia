using System.Reflection;
using System.Text.RegularExpressions;
using Finance.Infrastructure.Persistence;
using Finance.Infrastructure.Persistence.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Paqueteria.ArchitectureTests.Architecture;
using Paqueteria.Infrastructure.Database;

namespace Paqueteria.ArchitectureTests;

public sealed class FinanceArchitectureTests
{
    [Fact]
    public void Finance_has_exactly_the_four_canonical_layers()
    {
        Assert.Equal(
            ["Finance.Domain", "Finance.Application", "Finance.Infrastructure", "Finance.Endpoints"],
            SolutionCatalog.Finance.Components.Select(component => component.Name));
    }

    [Fact]
    public void Domain_and_application_are_free_of_technical_storage_dependencies()
    {
        var forbidden = new[] { "Npgsql", "EntityFrameworkCore", "AspNetCore" };
        var references = SolutionCatalog.Finance.Domain.Assembly.GetReferencedAssemblies()
            .Concat(SolutionCatalog.Finance.Application.Assembly.GetReferencedAssemblies())
            .Select(reference => reference.Name ?? string.Empty);

        Assert.DoesNotContain(references, reference =>
            forbidden.Any(value => reference.Contains(value, StringComparison.OrdinalIgnoreCase)));
        Assert.Empty(ProjectMetadataReader.Read(SolutionCatalog.Finance.Domain).PackageReferences);
        Assert.Empty(ProjectMetadataReader.Read(SolutionCatalog.Finance.Domain).FrameworkReferences);
    }

    [Fact]
    public void Finance_only_depends_on_organizations_across_module_boundaries()
    {
        // FIN-001 reads orders, dispatch and routes through SQL against the canonical schema rather than
        // by referencing those modules, so unit economics cannot drift into their business logic.
        Assert.Equal(
            ["Organizations"],
            SolutionCatalog.Finance.AllowedCrossModuleDependencies.Order(StringComparer.Ordinal));

        foreach (var component in SolutionCatalog.Finance.Components)
        {
            var referenced = ProjectMetadataReader.Read(component).ProjectReferencePaths
                .Where(SolutionCatalog.ByProjectPath.ContainsKey)
                .Select(path => SolutionCatalog.ByProjectPath[path])
                .Where(project => project.ModuleName is not null and not "Finance")
                .Select(project => project.ModuleName!)
                .Distinct(StringComparer.Ordinal);
            Assert.All(referenced, module => Assert.Equal("Organizations", module));
        }
    }

    [Fact]
    public void Finance_owns_only_the_SET001_settlement_ledger_migration()
    {
        // finance.cod_transactions ships in AI-06, so FIN-001 adds no migration of its own. The one
        // Finance lane is SET-001's (OA-2): it guards the canonical settlement tables and nothing else.
        const string directory = "src/Modules/Finance/Finance.Infrastructure/Persistence/Migrations";
        var root = TestRepository.GetPath(directory);
        Assert.Equal(
            ["20260925000100_EnforceSettlementLedgerIntegrity.cs"],
            Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
                .Order(StringComparer.Ordinal));

        var migration = Assert.Single(
            SolutionCatalog.Finance.Infrastructure.Assembly.GetTypes(),
            type => typeof(Migration).IsAssignableFrom(type) && !type.IsAbstract);
        Assert.Equal(typeof(EnforceSettlementLedgerIntegrity), migration);
        Assert.Equal("20260925000100_EnforceSettlementLedgerIntegrity", EnforceSettlementLedgerIntegrity.MigrationId);
        Assert.Equal(EnforceSettlementLedgerIntegrity.MigrationId, migration.GetCustomAttribute<MigrationAttribute>()?.Id);
        Assert.Equal(typeof(FinanceDbContext), migration.GetCustomAttribute<DbContextAttribute>()?.ContextType);

        // Every application-schema object the lane names is a settlement table, one of its own guard
        // functions or indexes, or the shared append-only guard it reuses: never a FIN-001 table.
        var schemas = string.Join('|', DatabaseSchemaCatalog.ApplicationSchemas);
        var referenced = Regex.Matches(
                EnforceSettlementLedgerIntegrity.LedgerSql,
                $@"\b(?:{schemas})\.[a-z_]+\b")
            .Select(match => match.Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
        Assert.Equal(
            [
                "finance.guard_settlement_line_insert",
                "finance.guard_settlement_mutation",
                "finance.require_settlement_reconciliation",
                "finance.settlement_lines",
                "finance.settlement_lines_owner_source_idx",
                "finance.settlements",
                "finance.settlements_owner_payee_period_idx",
                "platform.reject_runtime_mutation",
            ],
            referenced);

        // Additive only: no table is created, altered, dropped or emptied and no row is written.
        foreach (var forbidden in new[]
        {
            "CREATE TABLE", "ALTER TABLE", "DROP TABLE", "TRUNCATE", "INSERT INTO", "DELETE FROM",
            "UPDATE finance", "DROP TRIGGER", "DROP FUNCTION", "DROP INDEX", "DISABLE",
        })
        {
            Assert.DoesNotContain(forbidden, EnforceSettlementLedgerIntegrity.LedgerSql, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Money_never_leaves_the_finance_module_as_binary_floating_point()
    {
        var offenders = SolutionCatalog.Finance.Components
            .SelectMany(component => component.Assembly.GetTypes())
            .Where(type => type.IsPublic)
            .SelectMany(type => type.GetProperties().Select(property => (type, property)))
            .Where(pair =>
            {
                var propertyType = Nullable.GetUnderlyingType(pair.property.PropertyType)
                    ?? pair.property.PropertyType;
                return propertyType == typeof(float) ||
                    propertyType == typeof(double) ||
                    propertyType == typeof(decimal);
            })
            .Select(pair => $"{pair.type.FullName}.{pair.property.Name}")
            .ToArray();

        Assert.Empty(offenders);
    }
}
