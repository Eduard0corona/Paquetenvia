using Drivers.Application.Locations;
using Drivers.Domain;
using Drivers.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Paqueteria.Infrastructure.Tenancy;
using Paqueteria.ContractTests.Support;
using YamlDotNet.RepresentationModel;

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
    public void Drivers_implements_exactly_the_normative_location_and_voice_HTTP_operations()
    {
        var endpointsDirectory = Path.Combine(
            RepositoryPaths.Root,
            "src", "Modules", "Drivers", "Drivers.Endpoints");
        var implementation = string.Join('\n',
            Directory.GetFiles(endpointsDirectory, "*.cs", SearchOption.AllDirectories)
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                               !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .Select(File.ReadAllText));

        // DRV-001 location ingestion: one batched POST of 1..20 positions.
        Assert.Contains(
            "MapPost(\"/api/v1/driver/me/location-updates\"",
            implementation,
            StringComparison.Ordinal);
        Assert.Contains(".WithName(\"publishDriverLocation\")", implementation, StringComparison.Ordinal);
        Assert.Contains("Count: >= 1 and <= 20", implementation, StringComparison.Ordinal);

        // VOICE-001-MASKED-CALLS-2026-10-11: the driver's own phone, the recipient call and the two signed webhooks.
        Assert.Contains("public const string Path = \"/api/v1/driver/me/phone\";", implementation, StringComparison.Ordinal);
        Assert.Contains(
            "public const string Path = \"/api/v1/driver/me/stops/{orderId}/recipient-call\";",
            implementation,
            StringComparison.Ordinal);
        Assert.Contains("endpoints.MapPost(VoiceWebhookPaths.CallStatus,", implementation, StringComparison.Ordinal);
        Assert.Contains("endpoints.MapPost(VoiceWebhookPaths.Inbound,", implementation, StringComparison.Ordinal);

        // Exactly these routes and operation ids, nothing else.
        Assert.Equal(2, Count(implementation, "endpoints.MapGet("));
        Assert.Equal(4, Count(implementation, "endpoints.MapPost("));
        Assert.Equal(1, Count(implementation, "endpoints.MapPut("));
        Assert.Equal(1, Count(implementation, "endpoints.MapDelete("));
        Assert.DoesNotContain("MapPatch(", implementation, StringComparison.Ordinal);
        Assert.DoesNotContain("MapMethods(", implementation, StringComparison.Ordinal);
        Assert.DoesNotContain("MapGroup(", implementation, StringComparison.Ordinal);
        Assert.Equal(
            [
                "answerTwilioInboundCall",
                "getMyDriverPhone",
                "getRecipientCallAvailability",
                "publishDriverLocation",
                "receiveTwilioCallStatus",
                "registerMyDriverPhone",
                "removeMyDriverPhone",
                "requestRecipientCall",
            ],
            System.Text.RegularExpressions.Regex.Matches(implementation, "\\.WithName\\(\"([A-Za-z]+)\"\\)")
                .Select(match => match.Groups[1].Value)
                .Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Driver_location_AI05_operation_has_the_exact_request_response_and_status_contract()
    {
        var root = YamlNodes.LoadMapping(
            RepositoryPaths.Normative("contracts", "AI-05_OPENAPI.yaml"));
        var operation = root.Mapping("paths")
            .Mapping("/driver/me/location-updates")
            .Mapping("post");
        Assert.Equal("publishDriverLocation", operation.Scalar("operationId"));

        var responses = operation.Mapping("responses");
        Assert.Equal(
            ["202", "401", "403", "404", "409", "429", "503"],
            responses.Children.Keys
                .Cast<YamlScalarNode>()
                .Select(value => value.Value!)
                .Order(StringComparer.Ordinal));
        Assert.Equal(
            "#/components/responses/DriverLocationInvalidRequest",
            responses.Mapping("409").Scalar("$ref"));
        Assert.Equal(
            "#/components/responses/TooManyRequests",
            responses.Mapping("429").Scalar("$ref"));
        Assert.Equal(
            "#/components/responses/ServiceUnavailable",
            responses.Mapping("503").Scalar("$ref"));

        var schemas = root.Mapping("components").Mapping("schemas");
        var batch = schemas.Mapping("DriverLocationBatchRequest");
        Assert.Contains("positions", Required(batch));
        var positions = batch.Mapping("properties").Mapping("positions");
        Assert.Equal("array", positions.Scalar("type"));
        Assert.Equal("1", positions.Scalar("minItems"));
        Assert.Equal("20", positions.Scalar("maxItems"));
        Assert.Equal(
            "#/components/schemas/DriverLocationPoint",
            positions.Mapping("items").Scalar("$ref"));

        var point = schemas.Mapping("DriverLocationPoint");
        Assert.Contains("client_event_id", Required(point));
        var requestId = point.Mapping("properties").Mapping("client_event_id");
        Assert.Equal("string", requestId.Scalar("type"));
        Assert.Equal("uuid", requestId.Scalar("format"));

        var result = schemas.Mapping("DriverLocationResult");
        Assert.Contains("client_event_id", Required(result));
        Assert.Contains("duplicate", Required(result));
        var responseId = result.Mapping("properties").Mapping("client_event_id");
        Assert.Equal("string", responseId.Scalar("type"));
        Assert.Equal("uuid", responseId.Scalar("format"));
        var positionIdTypes = result.Mapping("properties").Mapping("position_id")
            .Sequence("type").Children.Cast<YamlScalarNode>().Select(value => value.Value);
        Assert.Equal(["string", "null"], positionIdTypes);
        var status = result.Mapping("properties").Mapping("status");
        Assert.Equal(
            ["ACCEPTED", "DUPLICATE", "REJECTED"],
            status.Sequence("enum").Children.Cast<YamlScalarNode>().Select(value => value.Value));
        var errorTypes = result.Mapping("properties").Mapping("error_code")
            .Sequence("type").Children.Cast<YamlScalarNode>().Select(value => value.Value);
        Assert.Equal(["string", "null"], errorTypes);

        var invalidProblem = schemas.Mapping("DriverLocationInvalidRequestProblem");
        Assert.Contains("code", Required(invalidProblem));
        Assert.Equal(
            "INVALID_REQUEST",
            invalidProblem.Mapping("properties").Mapping("code").Scalar("const"));
        Assert.Equal(
            "409",
            invalidProblem.Mapping("properties").Mapping("status").Scalar("const"));
    }

    [Fact]
    public void Driver_location_identifier_types_and_boundary_mapping_cannot_silently_substitute_values()
    {
        Assert.Equal(
            typeof(Guid),
            typeof(DriverLocationPointInput).GetProperty(nameof(DriverLocationPointInput.ClientEventId))!.PropertyType);
        Assert.Equal(
            typeof(Guid),
            typeof(DriverLocationItemResult).GetProperty(nameof(DriverLocationItemResult.ClientEventId))!.PropertyType);

        var endpointSource = File.ReadAllText(Path.Combine(
            RepositoryPaths.Root,
            "src", "Modules", "Drivers", "Drivers.Endpoints", "DriverLocationEndpoints.cs"));
        Assert.Contains("Guid.TryParseExact(", endpointSource, StringComparison.Ordinal);
        Assert.Contains("\"D\"", endpointSource, StringComparison.Ordinal);
        Assert.Contains("if (!TryToInput(", endpointSource, StringComparison.Ordinal);
        Assert.DoesNotContain("item.ClientEventId ?? Guid.Empty", endpointSource, StringComparison.Ordinal);

        var applicationSource = File.ReadAllText(Path.Combine(
            RepositoryPaths.Root,
            "src", "Modules", "Drivers", "Drivers.Application", "Locations",
            "DriverLocationContracts.cs"));
        Assert.DoesNotContain("string ClientEventId", applicationSource, StringComparison.Ordinal);
        Assert.DoesNotContain("string? ClientEventId", applicationSource, StringComparison.Ordinal);

        var domainSource = File.ReadAllText(Path.Combine(
            RepositoryPaths.Root,
            "src", "Modules", "Drivers", "Drivers.Domain", "Location",
            "DriverLocationPolicy.cs"));
        Assert.DoesNotContain("System.Text.Json", domainSource, StringComparison.Ordinal);
        Assert.DoesNotContain("JsonPropertyName", domainSource, StringComparison.Ordinal);
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

    private static string[] Required(YamlMappingNode schema) =>
        schema.Sequence("required").Children
            .Cast<YamlScalarNode>()
            .Select(value => value.Value!)
            .ToArray();

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
