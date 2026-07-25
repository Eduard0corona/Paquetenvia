using Drivers.Domain;
using Drivers.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Paqueteria.Infrastructure.Tenancy;
using Paqueteria.ContractTests.Support;

namespace Paqueteria.ContractTests;

public sealed class DriversImplementationContractTests
{
    [Fact]
    public void Ef_model_maps_DSP001_tables_and_canonical_driver_positions()
    {
        var options = new DbContextOptionsBuilder<DriversDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=model;Username=model;Password=model",
                postgres => postgres.UseNetTopologySuite())
            .Options;
        using var context = new DriversDbContext(options, new TenantDatabaseExecutionState());
        var model = context.Model;

        Assert.Equal(
            ["driver_documents", "driver_positions", "driver_profiles", "driver_service_areas"],
            model.GetEntityTypes().Select(entity => entity.GetTableName()).Order(StringComparer.Ordinal));

        AssertColumns<DriverProfile>(model,
            "created_at", "driver_type", "home_city_id", "id", "org_id", "status", "user_id", "vehicle_type");
        AssertColumns<DriverServiceArea>(model,
            "driver_id", "org_id", "service_area_id", "status");
        AssertColumns<DriverDocument>(model,
            "created_at", "document_type", "driver_id", "expires_at", "id", "object_key", "org_id", "sha256", "status");
        var position = Assert.Single(model.GetEntityTypes(), entity =>
            string.Equals(entity.GetTableName(), "driver_positions", StringComparison.Ordinal));
        Assert.Equal(
            ["accuracy_m", "captured_at", "city_id", "client_event_id", "driver_id", "heading_degrees",
                "id", "org_id", "point", "publish_realtime", "received_at", "speed_mps"],
            position.GetProperties().Select(property => property.GetColumnName()).Order(StringComparer.Ordinal));
        Assert.Equal("geometry(Point,4326)", position.FindProperty("Point")!.GetColumnType());
        Assert.Equal("numeric(8,2)", position.FindProperty("AccuracyMeters")!.GetColumnType());
        Assert.Equal("numeric(6,2)", position.FindProperty("HeadingDegrees")!.GetColumnType());
    }

    [Fact]
    public void Driver_positions_adoption_migration_is_non_destructive_and_scoped()
    {
        var migrationDirectory = Path.Combine(
            RepositoryPaths.Root,
            "src", "Modules", "Drivers", "Drivers.Infrastructure", "Persistence", "Migrations");
        var adoptionFiles = Directory.GetFiles(migrationDirectory, "*AdoptCanonicalDriverPositions.cs");
        var source = File.ReadAllText(Assert.Single(adoptionFiles));

        Assert.Contains("20260725000156_AdoptCanonicalDriverPositions", source, StringComparison.Ordinal);
        Assert.Contains("drivers.driver_positions", source, StringComparison.Ordinal);
        Assert.Contains("Find_SRID", source, StringComparison.Ordinal);
        Assert.Contains("relforcerowsecurity", source, StringComparison.Ordinal);
        foreach (var destructive in new[] { "CreateTable", "DropTable", "DropColumn", "AlterColumn", "DELETE FROM", "TRUNCATE" })
        {
            Assert.DoesNotContain(destructive, source, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Drivers_implements_exactly_the_normative_location_HTTP_operation()
    {
        var endpointsDirectory = Path.Combine(
            RepositoryPaths.Root,
            "src", "Modules", "Drivers", "Drivers.Endpoints");
        var implementation = string.Join('\n',
            Directory.GetFiles(endpointsDirectory, "*.cs", SearchOption.AllDirectories).Select(File.ReadAllText));

        Assert.DoesNotContain("MapGet(", implementation, StringComparison.Ordinal);
        Assert.Equal(1, Count(implementation, "endpoints.MapPost("));
        Assert.Contains(
            "MapPost(\"/api/v1/driver/me/location-updates\"",
            implementation,
            StringComparison.Ordinal);
        Assert.Contains(".WithName(\"publishDriverLocation\")", implementation, StringComparison.Ordinal);
        Assert.Contains("Count: >= 1 and <= 20", implementation, StringComparison.Ordinal);
        Assert.DoesNotContain("MapPut(", implementation, StringComparison.Ordinal);
        Assert.DoesNotContain("MapDelete(", implementation, StringComparison.Ordinal);
    }

    [Fact]
    public void Location_ingestion_uses_the_dedicated_insert_only_outbox_without_realtime()
    {
        var infrastructureDirectory = Path.Combine(
            RepositoryPaths.Root,
            "src", "Modules", "Drivers", "Drivers.Infrastructure", "Locations");
        var source = string.Join('\n',
            Directory.GetFiles(infrastructureDirectory, "*.cs", SearchOption.AllDirectories)
                .Select(File.ReadAllText));
        Assert.Contains("INSERT INTO platform.location_outbox_events", source, StringComparison.Ordinal);
        Assert.Contains("drivers.location-updated", source, StringComparison.Ordinal);
        Assert.Contains("driver-location-updated-v1", source, StringComparison.Ordinal);
        Assert.Contains("pg_advisory_xact_lock", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RETURNING", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SELECT * FROM platform.location_outbox_events", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("IRealtimePublisher", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IHubContext", source, StringComparison.Ordinal);
        Assert.DoesNotContain("heading_degrees\", pending", source, StringComparison.Ordinal);
        Assert.DoesNotContain("speed_mps\", pending", source, StringComparison.Ordinal);

        var modelSource = File.ReadAllText(RepositoryPaths.Root +
            "/src/Modules/Drivers/Drivers.Infrastructure/Persistence/DriversDbContext.cs");
        Assert.DoesNotContain("location_outbox_events", modelSource, StringComparison.Ordinal);
    }

    private static int Count(string source, string value)
    {
        var count = 0;
        var start = 0;
        while ((start = source.IndexOf(value, start, StringComparison.Ordinal)) >= 0)
        {
            count++;
            start += value.Length;
        }

        return count;
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
