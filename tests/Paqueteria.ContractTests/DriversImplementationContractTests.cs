using Drivers.Domain;
using Drivers.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Paqueteria.Infrastructure.Tenancy;
using Paqueteria.ContractTests.Support;

namespace Paqueteria.ContractTests;

public sealed class DriversImplementationContractTests
{
    [Fact]
    public void Ef_model_maps_exactly_the_three_DSP001_tables_and_canonical_columns()
    {
        var options = new DbContextOptionsBuilder<DriversDbContext>()
            .UseNpgsql("Host=localhost;Database=model;Username=model;Password=model")
            .Options;
        using var context = new DriversDbContext(options, new TenantDatabaseExecutionState());
        var model = context.Model;

        Assert.Equal(
            ["driver_documents", "driver_profiles", "driver_service_areas"],
            model.GetEntityTypes().Select(entity => entity.GetTableName()).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(model.GetEntityTypes(), entity =>
            string.Equals(entity.GetTableName(), "driver_positions", StringComparison.Ordinal));

        AssertColumns<DriverProfile>(model,
            "created_at", "driver_type", "home_city_id", "id", "org_id", "status", "user_id", "vehicle_type");
        AssertColumns<DriverServiceArea>(model,
            "driver_id", "org_id", "service_area_id", "status");
        AssertColumns<DriverDocument>(model,
            "created_at", "document_type", "driver_id", "expires_at", "id", "object_key", "org_id", "sha256", "status");
    }

    [Fact]
    public void Adoption_migration_is_single_non_destructive_and_scoped()
    {
        var migrationDirectory = Path.Combine(
            RepositoryPaths.Root,
            "src", "Modules", "Drivers", "Drivers.Infrastructure", "Persistence", "Migrations");
        var adoptionFiles = Directory.GetFiles(migrationDirectory, "*AdoptCanonicalDriversBaseline.cs");
        var source = File.ReadAllText(Assert.Single(adoptionFiles));

        Assert.Contains("20260723_AdoptCanonicalDriversBaseline", source, StringComparison.Ordinal);
        Assert.Contains("drivers.driver_profiles", source, StringComparison.Ordinal);
        Assert.Contains("drivers.driver_service_areas", source, StringComparison.Ordinal);
        Assert.Contains("drivers.driver_documents", source, StringComparison.Ordinal);
        Assert.DoesNotContain("driver_positions", source, StringComparison.Ordinal);
        foreach (var destructive in new[] { "CreateTable", "DropTable", "DropColumn", "AlterColumn", "DELETE FROM", "TRUNCATE" })
        {
            Assert.DoesNotContain(destructive, source, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Drivers_adds_no_HTTP_operation_to_the_normative_or_implemented_surface()
    {
        var endpointsDirectory = Path.Combine(
            RepositoryPaths.Root,
            "src", "Modules", "Drivers", "Drivers.Endpoints");
        var implementation = string.Join('\n',
            Directory.GetFiles(endpointsDirectory, "*.cs", SearchOption.AllDirectories).Select(File.ReadAllText));

        Assert.DoesNotContain("MapGet(", implementation, StringComparison.Ordinal);
        Assert.DoesNotContain("MapPost(", implementation, StringComparison.Ordinal);
        Assert.DoesNotContain("MapPut(", implementation, StringComparison.Ordinal);
        Assert.DoesNotContain("MapDelete(", implementation, StringComparison.Ordinal);
    }

    private static void AssertColumns<TEntity>(
        Microsoft.EntityFrameworkCore.Metadata.IModel model,
        params string[] expected)
    {
        var entity = model.FindEntityType(typeof(TEntity));
        Assert.NotNull(entity);
        var table = Microsoft.EntityFrameworkCore.Metadata.StoreObjectIdentifier.Table(
            entity.GetTableName()!,
            entity.GetSchema());
        var columns = entity.GetProperties()
            .Select(property => property.GetColumnName(table))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expected.Order(StringComparer.Ordinal), columns);
    }
}
