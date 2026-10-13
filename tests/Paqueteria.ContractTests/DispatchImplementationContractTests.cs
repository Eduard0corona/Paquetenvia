using System.Reflection;
using System.Text.Json.Serialization;
using Dispatch.Domain;
using Dispatch.Endpoints;
using Dispatch.Infrastructure.Assignments;
using Dispatch.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Paqueteria.ContractTests.Support;
using Paqueteria.Infrastructure.Tenancy;
using YamlDotNet.RepresentationModel;

namespace Paqueteria.ContractTests;

public sealed class DispatchImplementationContractTests
{
    [Fact]
    public void Implementation_exposes_the_two_DSP002_and_three_EXT001_routes()
    {
        var path = Path.Combine(
            RepositoryPaths.Root,
            "src", "Modules", "Dispatch", "Dispatch.Endpoints", "DispatchEndpoints.cs");
        var source = File.ReadAllText(path);

        Assert.Equal(3, Count(source, "endpoints.MapPost("));
        Assert.Equal(4, Count(source, "endpoints.MapGet("));
        Assert.Contains("MapGet(\"/api/v1/orders/{orderId}/assignable-drivers\"", source, StringComparison.Ordinal);
        Assert.Contains(".WithName(\"listAssignableDrivers\")", source, StringComparison.Ordinal);
        Assert.Contains("MapGet(\"/api/v1/drivers\"", source, StringComparison.Ordinal);
        Assert.Contains(".WithName(\"listOrganizationDrivers\")", source, StringComparison.Ordinal);
        Assert.Contains("MapPost(\"/api/v1/orders/{orderId}/assignments\"", source, StringComparison.Ordinal);
        Assert.Contains("MapGet(\"/api/v1/driver/me/stops\"", source, StringComparison.Ordinal);
        Assert.Contains("MapPost(\"/api/v1/external-offers\"", source, StringComparison.Ordinal);
        Assert.Contains("MapPost(\"/api/v1/external-offers/{offerId}/accept\"", source, StringComparison.Ordinal);
        Assert.Contains("MapGet(\"/api/v1/driver/me/external-offers\"", source, StringComparison.Ordinal);
        Assert.Contains(".WithName(\"assignDriver\")", source, StringComparison.Ordinal);
        Assert.Contains(".WithName(\"listMyStops\")", source, StringComparison.Ordinal);
        Assert.Contains(".WithName(\"createExternalOffer\")", source, StringComparison.Ordinal);
        Assert.Contains(".WithName(\"acceptExternalOffer\")", source, StringComparison.Ordinal);
        Assert.Contains(".WithName(\"listMyEligibleExternalOffers\")", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MapPut(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MapPatch(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MapDelete(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Request_assignment_and_stop_DTOs_match_AI05_and_expose_no_PII()
    {
        AssertJsonProperties<CreateAssignmentRequest>(
            "assignment_type", "cost_cents", "driver_id", "route_id");
        AssertJsonProperties<AssignmentResponse>("cost", "driver_id", "id", "order_id", "route_id", "status");
        AssertJsonProperties<MoneyResponse>("amount_cents", "currency");
        AssertJsonProperties<DriverStopListResponse>("completed_today", "stops");
        AssertJsonProperties<DriverStopResponse>(
            "address_summary",
            "aggregate_version",
            "cod",
            "contact_full_name",
            "contact_short_name",
            "details_available",
            "navigation",
            "order_id",
            "order_public_id",
            "service_window",
            "status",
            "stop_type",
            "zone_name");
        AssertJsonProperties<DriverStopServiceWindowResponse>("from", "to");
        AssertJsonProperties<DriverStopCodResponse>("expected", "recorded");
        AssertJsonProperties<DriverStopNavigationResponse>("address_text", "latitude", "longitude", "references");
        AssertJsonProperties<CreateExternalOfferRequest>(
            "commission_cents", "eligible_constraints", "expires_at", "order_id");
        AssertJsonProperties<ExternalOfferConstraintsRequest>(
            "requires_cod", "service_area_ids", "vehicle_types");
        AssertJsonProperties<ExternalOfferResponse>(
            "accepted_at", "accepted_by_driver_id", "commission", "expires_at", "id",
            "order_id", "status", "version");
        AssertJsonProperties<ExternalOfferPageResponse>("items", "next_cursor");
        AssertJsonProperties<AssignableDriverResponse>(
            "active_assignment_count", "driver_id", "driver_reference", "eligible", "ineligibility_reasons",
            "vehicle_type");
        AssertJsonProperties<AssignableDriverPageResponse>("items", "next_cursor");
        Assert.DoesNotContain(
            typeof(AssignableDriverResponse).GetProperties().Select(property => property.Name),
            name => name.Contains("Name", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Phone", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Email", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Document", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Location", StringComparison.OrdinalIgnoreCase));

        // UI-PHASE3-DRIVERS-2026-10-11: the dispatcher's driver list carries the time of the latest position, never
        // the position, and no personal data.
        AssertJsonProperties<OrganizationDriverResponse>(
            "active_stop_count", "driver_id", "driver_reference", "last_position_at", "status", "vehicle_type");
        AssertJsonProperties<OrganizationDriverPageResponse>("items", "next_cursor");
        Assert.DoesNotContain(
            typeof(OrganizationDriverResponse).GetProperties().Select(property => property.Name),
            name => name.Contains("Name", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Phone", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Email", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Document", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Location", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Lat", StringComparison.Ordinal) ||
                name.Contains("Lng", StringComparison.Ordinal) ||
                name.Contains("Point", StringComparison.Ordinal) ||
                name.Contains("Accuracy", StringComparison.Ordinal));

        // UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11: the only contact data are the contact name of a PICKUP stop and
        // the short name ("M. López") of the other stops; never a phone, a cost, an internal driver or assignment id,
        // a ciphertext or a key version.
        var stopNames = new[]
            {
                typeof(DriverStopListResponse),
                typeof(DriverStopResponse),
                typeof(DriverStopServiceWindowResponse),
                typeof(DriverStopCodResponse),
                typeof(DriverStopNavigationResponse),
            }
            .SelectMany(type => type.GetProperties())
            .Select(property => property.Name)
            .ToArray();
        Assert.DoesNotContain(stopNames, name =>
            name.Contains("Phone", StringComparison.OrdinalIgnoreCase) ||
            (name.Contains("Contact", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(name, nameof(DriverStopResponse.ContactShortName), StringComparison.Ordinal) &&
                !string.Equals(name, nameof(DriverStopResponse.ContactFullName), StringComparison.Ordinal)) ||
            name.Contains("Cost", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("DriverId", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("AssignmentId", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Ciphertext", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("KeyVersion", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AI05_assignment_and_driver_stop_shapes_are_implemented_structurally()
    {
        var root = YamlNodes.LoadMapping(
            RepositoryPaths.Normative("contracts", "AI-05_OPENAPI.yaml"));
        var assignmentOperation = root.Mapping("paths")
            .Mapping("/orders/{orderId}/assignments")
            .Mapping("post");
        var responses = assignmentOperation.Mapping("responses");
        var schemas = root.Mapping("components").Mapping("schemas");
        var request = schemas.Mapping("CreateAssignmentRequest");
        var assignment = schemas.Mapping("Assignment");
        var money = schemas.Mapping("Money");
        var stop = schemas.Mapping("DriverStop");
        var conflict = schemas.Mapping("DispatchAssignmentConflictProblem");

        Assert.Equal(
            ["201", "401", "403", "404", "409", "503"],
            responses.Children.Keys.Cast<YamlScalarNode>().Select(value => value.Value));
        Assert.Equal(
            "#/components/responses/ServiceUnavailable",
            responses.Mapping("503").Scalar("$ref"));
        Assert.Equal(
            "#/components/responses/UniformNotFound",
            responses.Mapping("404").Scalar("$ref"));
        Assert.Equal(
            "#/components/responses/DispatchAssignmentConflict",
            responses.Mapping("409").Scalar("$ref"));
        Assert.Equal(
            "shape-validation-then-capability-before-persisted-state",
            assignmentOperation.Scalar("x-authorization-precedence"));
        Assert.Equal(
            "invalid-request-without-productive-transaction",
            assignmentOperation.Scalar("x-shape-validation"));
        Assert.Equal(
            [
                "idempotency_lock",
                "idempotency_record",
                "replay_evidence",
                "order_packages",
                "driver_profile_documents",
            ],
            assignmentOperation.Sequence("x-capability-protected-state")
                .Children.Cast<YamlScalarNode>().Select(value => value.Value));
        Assert.Equal(
            ["order_packages", "driver_profile_documents"],
            assignmentOperation.Sequence("x-authorized-visibility-plan")
                .Children.Cast<YamlScalarNode>().Select(value => value.Value));
        Assert.Equal(
            "structural-postgresql-no-artificial-delay",
            assignmentOperation.Scalar("x-non-enumeration"));
        Assert.Equal(
            ["assignment_type", "cost_cents", "driver_id"],
            RequiredPropertyNames(request));
        Assert.Equal(
            ["cost", "driver_id", "id", "order_id", "route_id", "status"],
            RequiredPropertyNames(assignment));
        Assert.Equal(["amount_cents", "currency"], RequiredPropertyNames(money));
        Assert.Equal(
            [
                "address_summary",
                "aggregate_version",
                "cod",
                "contact_full_name",
                "contact_short_name",
                "details_available",
                "navigation",
                "order_id",
                "order_public_id",
                "service_window",
                "status",
                "stop_type",
                "zone_name",
            ],
            RequiredPropertyNames(stop));
        var stopList = schemas.Mapping("DriverStopList");
        Assert.Equal(["completed_today", "stops"], RequiredPropertyNames(stopList));
        Assert.Equal("false", stopList.Scalar("additionalProperties"));
        Assert.Equal(
            "#/components/schemas/DriverStop",
            stopList.Mapping("properties").Mapping("stops").Mapping("items").Scalar("$ref"));
        Assert.Equal("0", stopList.Mapping("properties").Mapping("completed_today").Scalar("minimum"));
        var listMyStops = root.Mapping("paths").Mapping("/driver/me/stops").Mapping("get");
        Assert.Equal(
            "#/components/schemas/DriverStopList",
            listMyStops.Mapping("responses").Mapping("200").Mapping("content").Mapping("application/json")
                .Mapping("schema").Scalar("$ref"));
        Assert.Equal(
            ["200", "401", "403", "409", "503"],
            listMyStops.Mapping("responses").Children.Keys.Cast<YamlScalarNode>().Select(value => value.Value));
        var detailsParameter = listMyStops.Sequence("parameters").Children.OfType<YamlMappingNode>()
            .Single(parameter => parameter.Children.ContainsKey(new YamlScalarNode("name")));
        Assert.Equal("details", detailsParameter.Scalar("name"));
        Assert.Equal("query", detailsParameter.Scalar("in"));
        Assert.Equal("boolean", detailsParameter.Mapping("schema").Scalar("type"));
        Assert.Equal("true", detailsParameter.Mapping("schema").Scalar("default"));
        var stopProperties = stop.Mapping("properties");
        Assert.Equal(
            ["address_text", "latitude", "longitude", "references"],
            RequiredPropertyNames(stopProperties.Mapping("navigation")));
        Assert.Equal("false", stopProperties.Mapping("navigation").Scalar("additionalProperties"));
        Assert.Equal(
            ["object", "null"],
            stopProperties.Mapping("navigation").Sequence("type").Children.Cast<YamlScalarNode>()
                .Select(value => value.Value));
        Assert.Equal("boolean", stopProperties.Mapping("details_available").Scalar("type"));
        Assert.Equal(
            Dispatch.Domain.DriverStopZoneName.MaximumLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
            stopProperties.Mapping("zone_name").Scalar("maxLength"));
        Assert.Equal(["expected", "recorded"], RequiredPropertyNames(stopProperties.Mapping("cod")));
        Assert.Equal(
            "#/components/schemas/Money",
            stopProperties.Mapping("cod").Mapping("properties").Mapping("expected").Scalar("$ref"));
        Assert.Equal(["from", "to"], RequiredPropertyNames(stopProperties.Mapping("service_window")));
        Assert.Equal(
            Dispatch.Domain.DriverStopContactName.MaximumLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
            stopProperties.Mapping("contact_short_name").Scalar("maxLength"));
        Assert.Equal(
            Dispatch.Domain.DriverStopContactName.MaximumFullLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
            stopProperties.Mapping("contact_full_name").Scalar("maxLength"));
        Assert.Equal(
            Dispatch.Domain.DriverStopAddressText.MaximumAddressLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
            stopProperties.Mapping("navigation").Mapping("properties").Mapping("address_text").Scalar("maxLength"));
        Assert.Equal(
            Dispatch.Domain.DriverStopAddressText.MaximumReferencesLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
            stopProperties.Mapping("navigation").Mapping("properties").Mapping("references").Scalar("maxLength"));

        Assert.Equal(
            ["OWN", "EXTERNAL", "ALLY_CAPACITY"],
            request.Mapping("properties").Mapping("assignment_type").Sequence("enum")
                .Children.Cast<YamlScalarNode>().Select(value => value.Value));
        Assert.Equal(
            ["OWN"],
            request.Mapping("properties").Mapping("assignment_type")
                .Sequence("x-dsp-002-enabled-values")
                .Children.Cast<YamlScalarNode>().Select(value => value.Value));
        var reservedTypes = request.Mapping("properties").Mapping("assignment_type")
            .Mapping("x-reserved-for");
        Assert.Equal("EXT-001", reservedTypes.Scalar("EXTERNAL"));
        Assert.Equal("ALY-004", reservedTypes.Scalar("ALLY_CAPACITY"));
        var routeId = request.Mapping("properties").Mapping("route_id");
        Assert.Equal(
            ["string", "null"],
            routeId.Sequence("type").Children.Cast<YamlScalarNode>().Select(value => value.Value));
        Assert.Equal("uuid", routeId.Scalar("format"));
        Assert.Equal("omitted-or-null-only", routeId.Scalar("x-dsp-002-support"));
        Assert.Equal("false", request.Scalar("additionalProperties"));
        Assert.Equal(
            ["PICKUP", "DELIVERY", "RETURN"],
            stop.Mapping("properties").Mapping("stop_type").Sequence("enum")
                .Children.Cast<YamlScalarNode>().Select(value => value.Value));
        Assert.Equal("int64", request.Mapping("properties").Mapping("cost_cents").Scalar("format"));
        Assert.Equal("MXN", money.Mapping("properties").Mapping("currency").Scalar("const"));
        Assert.Equal("409", conflict.Mapping("properties").Mapping("status").Scalar("const"));
        Assert.Equal(
            ["INVALID_REQUEST", "CONFLICT", "DRIVER_INELIGIBLE", "DRIVER_DOCUMENT_EXPIRED"],
            conflict.Mapping("properties").Mapping("code").Sequence("enum")
                .Children.Cast<YamlScalarNode>().Select(value => value.Value));
    }

    /// <summary>
    /// UI-PHASE2-DRIVER-PICKER-2026-10-05: listAssignableDrivers is published with the same precedence, visibility and
    /// conflict response as assignDriver, and its schemas carry exactly the vocabulary the server enforces.
    /// </summary>
    [Fact]
    public void AI05_assignable_driver_list_matches_the_implemented_vocabulary()
    {
        var root = YamlNodes.LoadMapping(RepositoryPaths.Normative("contracts", "AI-05_OPENAPI.yaml"));
        var operation = root.Mapping("paths").Mapping("/orders/{orderId}/assignable-drivers").Mapping("get");
        Assert.Equal("listAssignableDrivers", operation.Scalar("operationId"));
        Assert.Contains("UI-PHASE2-DRIVER-PICKER-2026-10-05", operation.Scalar("description"), StringComparison.Ordinal);
        Assert.Equal(
            "shape-validation-then-capability-before-persisted-state",
            operation.Scalar("x-authorization-precedence"));
        Assert.Equal(
            ["order_packages", "driver_profile_documents"],
            operation.Sequence("x-capability-protected-state").Children.Cast<YamlScalarNode>().Select(value => value.Value));
        var responses = operation.Mapping("responses");
        Assert.Equal(
            ["200", "401", "403", "404", "409", "429", "503"],
            responses.Children.Keys.Cast<YamlScalarNode>().Select(value => value.Value));
        Assert.Equal("#/components/responses/UniformNotFound", responses.Mapping("404").Scalar("$ref"));
        Assert.Equal("#/components/responses/DispatchAssignmentConflict", responses.Mapping("409").Scalar("$ref"));
        var cursor = Assert.Single(
            operation.Sequence("parameters").Children.Cast<YamlMappingNode>(),
            parameter => parameter.Children.ContainsKey(new YamlScalarNode("name")));
        Assert.Equal("cursor", cursor.Scalar("name"));
        Assert.Equal("query", cursor.Scalar("in"));
        Assert.Equal(["cursor"], DispatchEndpointsQueryParameters());

        var schemas = root.Mapping("components").Mapping("schemas");
        var driver = schemas.Mapping("AssignableDriver");
        Assert.Equal("false", driver.Scalar("additionalProperties"));
        Assert.Equal(
            [
                "active_assignment_count", "driver_id", "driver_reference", "eligible", "ineligibility_reasons",
                "vehicle_type",
            ],
            RequiredPropertyNames(driver));
        var properties = driver.Mapping("properties");
        Assert.Equal("^DRV-[0-9a-f]{8}$", properties.Mapping("driver_reference").Scalar("pattern"));
        Assert.Equal(
            Dispatch.Application.Assignments.AssignableDriverPolicy.VehicleTypes.ToArray(),
            properties.Mapping("vehicle_type").Sequence("enum").Children.Cast<YamlScalarNode>()
                .Select(value => value.Value!).ToArray());
        Assert.Equal(
            Dispatch.Application.Assignments.AssignableDriverPolicy.ReasonCodes.ToArray(),
            properties.Mapping("ineligibility_reasons").Mapping("items").Sequence("enum")
                .Children.Cast<YamlScalarNode>().Select(value => value.Value!).ToArray());
        Assert.Equal(["items", "next_cursor"], RequiredPropertyNames(schemas.Mapping("AssignableDriverPage")));

        // Every policy code except the two driver-type codes (only OWN profiles are listed) is published.
        var policyCodes = typeof(Drivers.Application.Eligibility.DriverEligibilityRejectionCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(field => (string)field.GetValue(null)!)
            .Where(code => code is not ("DRIVER_TYPE_NOT_OWN" or "DRIVER_TYPE_NOT_EXTERNAL"))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            policyCodes,
            Dispatch.Application.Assignments.AssignableDriverPolicy.ReasonCodes.Order(StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// UI-PHASE3-DRIVERS-2026-10-11: listOrganizationDrivers is published with the picker's precedence and conflict
    /// response, no 404, the one cursor parameter and schemas carrying exactly the vocabulary the server enforces;
    /// its stop is the DriverStopPolicy stop listMyStops shows the driver.
    /// </summary>
    [Fact]
    public void AI05_organization_driver_list_matches_the_implemented_vocabulary()
    {
        var root = YamlNodes.LoadMapping(RepositoryPaths.Normative("contracts", "AI-05_OPENAPI.yaml"));
        var operation = root.Mapping("paths").Mapping("/drivers").Mapping("get");
        Assert.Equal("listOrganizationDrivers", operation.Scalar("operationId"));
        var description = operation.Scalar("description").ReplaceLineEndings(" ");
        Assert.Contains("UI-PHASE3-DRIVERS-2026-10-11", description, StringComparison.Ordinal);
        Assert.Contains(
            string.Join(", ", DriverStopPolicy.OrderStatuses.Take(DriverStopPolicy.OrderStatuses.Count - 1)) +
            " or " + DriverStopPolicy.OrderStatuses[^1],
            description,
            StringComparison.Ordinal);
        Assert.Equal(
            "shape-validation-then-capability-before-persisted-state",
            operation.Scalar("x-authorization-precedence"));
        Assert.Equal("invalid-request-without-productive-transaction", operation.Scalar("x-shape-validation"));
        var responses = operation.Mapping("responses");
        Assert.Equal(
            ["200", "401", "403", "409", "503"],
            responses.Children.Keys.Cast<YamlScalarNode>().Select(value => value.Value));
        Assert.Equal("#/components/responses/Forbidden", responses.Mapping("403").Scalar("$ref"));
        Assert.Equal("#/components/responses/DispatchAssignmentConflict", responses.Mapping("409").Scalar("$ref"));
        var cursor = Assert.Single(
            operation.Sequence("parameters").Children.Cast<YamlMappingNode>(),
            parameter => parameter.Children.ContainsKey(new YamlScalarNode("name")));
        Assert.Equal("cursor", cursor.Scalar("name"));
        Assert.Equal("query", cursor.Scalar("in"));
        Assert.Equal(["cursor"], OrganizationDriversQueryParameters());

        var schemas = root.Mapping("components").Mapping("schemas");
        var driver = schemas.Mapping("OrganizationDriver");
        Assert.Equal("false", driver.Scalar("additionalProperties"));
        Assert.Equal(
            ["active_stop_count", "driver_id", "driver_reference", "last_position_at", "status", "vehicle_type"],
            RequiredPropertyNames(driver));
        var properties = driver.Mapping("properties");
        Assert.Equal("^DRV-[0-9a-f]{8}$", properties.Mapping("driver_reference").Scalar("pattern"));
        Assert.Equal(
            Dispatch.Application.Assignments.AssignableDriverPolicy.VehicleTypes.ToArray(),
            properties.Mapping("vehicle_type").Sequence("enum").Children.Cast<YamlScalarNode>()
                .Select(value => value.Value!).ToArray());
        Assert.Equal(
            Dispatch.Application.Fleet.OrganizationDriverPolicy.Statuses.ToArray(),
            properties.Mapping("status").Sequence("enum").Children.Cast<YamlScalarNode>()
                .Select(value => value.Value!).ToArray());
        Assert.Equal("0", properties.Mapping("active_stop_count").Scalar("minimum"));
        var lastPosition = properties.Mapping("last_position_at");
        Assert.Equal(
            ["string", "null"],
            lastPosition.Sequence("type").Children.Cast<YamlScalarNode>().Select(value => value.Value));
        Assert.Equal("date-time", lastPosition.Scalar("format"));
        Assert.Equal(["items", "next_cursor"], RequiredPropertyNames(schemas.Mapping("OrganizationDriverPage")));
    }

    /// <summary>
    /// UI-PHASE3-DRIVERS-2026-10-11: one stop definition. The statuses the driver list counts are exactly those
    /// DriverStopPolicy projects as a stop and those the listMyStops query reads, and the list reads no personal
    /// column and no coordinate.
    /// </summary>
    [Fact]
    public void Organization_driver_list_counts_the_listMyStops_stops_and_reads_no_personal_column()
    {
        var statuses = new[]
        {
            "DRAFT", "CONFIRMED", "READY_FOR_PICKUP", "ASSIGNED", "AT_PICKUP", "PICKED_UP", "IN_TRANSIT",
            "DELIVERING", "FAILED_ATTEMPT", "RESCHEDULED", "RETURNING", "RETURNED", "DELIVERED", "CLOSED",
            "CLAIM_OPEN", "CLAIM_RESOLVED", "CANCELLED",
        };
        foreach (var status in statuses)
        {
            foreach (var custody in new[] { false, true })
            {
                Assert.Equal(
                    DriverStopPolicy.OrderStatuses.Contains(status),
                    DriverStopPolicy.Project(status, custody).Included);
            }
        }

        var stops = File.ReadAllText(Path.Combine(
            RepositoryPaths.Root,
            "src", "Modules", "Dispatch", "Dispatch.Infrastructure", "Stops", "PostgreSqlDriverStopsQuery.cs"));
        var listed = System.Text.RegularExpressions.Regex.Match(
            stops, @"AND o\.status IN \(([^)]*)\)", System.Text.RegularExpressions.RegexOptions.Singleline);
        Assert.True(listed.Success);
        Assert.Equal(
            DriverStopPolicy.OrderStatuses.Order(StringComparer.Ordinal),
            System.Text.RegularExpressions.Regex.Matches(listed.Groups[1].Value, "'([A-Z_]+)'")
                .Select(match => match.Groups[1].Value)
                .Order(StringComparer.Ordinal));

        var source = File.ReadAllText(Path.Combine(
            RepositoryPaths.Root,
            "src", "Modules", "Dispatch", "Dispatch.Infrastructure", "Fleet", "PostgreSqlOrganizationDriversQuery.cs"));
        Assert.Contains("DriverStopPolicy.OrderStatuses", source, StringComparison.Ordinal);
        var start = source.IndexOf("const string sql =", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var open = source.IndexOf("\"\"\"", start, StringComparison.Ordinal);
        var close = source.IndexOf("\"\"\"", open + 3, StringComparison.Ordinal);
        Assert.True(open > start && close > open);
        var query = source[(open + 3)..close];
        Assert.Contains("o.status=ANY(@stop_statuses)", query, StringComparison.Ordinal);
        Assert.Contains("a.status IN ('ACCEPTED','ACTIVE')", query, StringComparison.Ordinal);
        Assert.Contains("p.driver_type='OWN' AND p.status<>'INACTIVE'", query, StringComparison.Ordinal);
        Assert.Contains("SELECT position.captured_at", query, StringComparison.Ordinal);
        foreach (var forbidden in new[]
                 {
                     "identity.users", "organization_memberships", "driver_documents", "email", "phone",
                     "ST_X", "ST_Y", "position.point", "accuracy_m", "FOR UPDATE", "INSERT INTO", "UPDATE ",
                     "DELETE FROM",
                 })
        {
            Assert.DoesNotContain(forbidden, query, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Ef_model_maps_only_dispatch_assignments_with_the_exact_columns_and_partial_index()
    {
        var options = new DbContextOptionsBuilder<DispatchDbContext>()
            .UseNpgsql("Host=localhost;Database=model;Username=model;Password=model")
            .Options;
        using var context = new DispatchDbContext(options, new TenantDatabaseExecutionState());
        var entity = Assert.Single(context.Model.GetEntityTypes());
        Assert.Equal("dispatch", entity.GetSchema());
        Assert.Equal("assignments", entity.GetTableName());
        var table = Microsoft.EntityFrameworkCore.Metadata.StoreObjectIdentifier.Table(
            "assignments",
            "dispatch");
        Assert.Equal(
            [
                "accepted_at", "assignment_type", "cost_cents", "created_at", "driver_id", "id",
                "operator_org_id", "order_id", "owner_org_id", "route_id", "status",
            ],
            entity.GetProperties()
                .Select(property => property.GetColumnName(table))
                .Order(StringComparer.Ordinal));
        var index = Assert.Single(entity.GetIndexes());
        Assert.True(index.IsUnique);
        Assert.Equal("one_active_assignment_per_order", index.GetDatabaseName());
        Assert.Equal("status IN ('ACCEPTED','ACTIVE')", index.GetFilter());
    }

    [Fact]
    public void Adoption_migration_is_single_non_destructive_and_uses_its_own_history()
    {
        var migrationDirectory = Path.Combine(
            RepositoryPaths.Root,
            "src", "Modules", "Dispatch", "Dispatch.Infrastructure", "Persistence", "Migrations");
        var source = File.ReadAllText(Assert.Single(
            Directory.GetFiles(migrationDirectory, "*AdoptCanonicalDispatchAssignmentsBaseline.cs")));

        Assert.Contains("20260723_AdoptCanonicalDispatchAssignmentsBaseline", source, StringComparison.Ordinal);
        Assert.Contains("dispatch.assignments", source, StringComparison.Ordinal);
        Assert.Contains("one_active_assignment_per_order", source, StringComparison.Ordinal);
        Assert.Contains("assignments_tenant", source, StringComparison.Ordinal);
        foreach (var destructive in new[]
        {
            "CREATE TABLE",
            "DROP TABLE",
            "ADD COLUMN",
            "DROP COLUMN",
            "DROP CONSTRAINT",
            "DISABLE ROW LEVEL SECURITY",
            "DROP POLICY",
        })
        {
            Assert.DoesNotContain(destructive, source, StringComparison.OrdinalIgnoreCase);
        }

        var dependencyInjection = File.ReadAllText(Path.Combine(
            RepositoryPaths.Root,
            "src", "Modules", "Dispatch", "Dispatch.Infrastructure", "DependencyInjection.cs"));
        Assert.Contains("__ef_migrations_history_dispatch", dependencyInjection, StringComparison.Ordinal);
    }

    [Fact]
    public void Coordinator_has_exact_scope_flow_and_no_returning_or_extra_assignment_topic()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryPaths.Root,
            "src", "Modules", "Dispatch", "Dispatch.Infrastructure",
            "Assignments", "PostgreSqlAssignmentToOrderCoordinator.cs"));

        Assert.Equal("DSP-002:ASSIGN_OWN_DRIVER", PostgreSqlAssignmentToOrderCoordinator.IdempotencyScope);
        Assert.Equal("assignment_to_order_status_event", PostgreSqlAssignmentToOrderCoordinator.CoordinationFlow);
        Assert.DoesNotContain("RETURNING", source, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, Count(source, "\"orders.status-changed\""));
        Assert.DoesNotContain("dispatch.assignment-created", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Operator_owner_rows_go_only_through_the_definer_functions_and_only_when_the_operator_acts()
    {
        // DSP-OPERATOR-OWNER-OUTBOX-DEFINER-2026-10-03: the operator path calls the two SECURITY DEFINER functions
        // with every value supplied, never inserts directly and never reads a value back.
        var writer = File.ReadAllText(Path.Combine(
            RepositoryPaths.Root,
            "src", "Modules", "Dispatch", "Dispatch.Infrastructure",
            "Assignments", "OperatorOwnerEventWriter.cs"));
        Assert.Contains("SELECT security.append_operator_order_outbox(", writer, StringComparison.Ordinal);
        Assert.Contains("SELECT security.append_operator_order_audit(", writer, StringComparison.Ordinal);
        Assert.DoesNotContain("INSERT INTO", writer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("RETURNING", writer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ExecuteScalar", writer, StringComparison.Ordinal);
        Assert.DoesNotContain("ExecuteReader", writer, StringComparison.Ordinal);

        var coordinator = File.ReadAllText(Path.Combine(
            RepositoryPaths.Root,
            "src", "Modules", "Dispatch", "Dispatch.Infrastructure",
            "Assignments", "PostgreSqlAssignmentToOrderCoordinator.cs"));
        Assert.Contains("var actingAsOperator = operatorOrganizationId is not null;", coordinator, StringComparison.Ordinal);
        Assert.Equal(3, Count(coordinator, "OperatorOwnerEventWriter.AppendOutboxAsync("));
        Assert.Equal(2, Count(coordinator, "OperatorOwnerEventWriter.AppendAuditAsync("));
        Assert.Equal(5, Count(coordinator, "if (actingAsOperator)"));
    }

    private static string[] DispatchEndpointsQueryParameters()
    {
        var accepted = new List<string>();
        foreach (var name in new[] { "cursor", "limit", "page_size", "status", "driver_id", "order_id" })
        {
            var query = new Microsoft.AspNetCore.Http.QueryCollection(
                new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
                {
                    [name] = name == "cursor"
                        ? Dispatch.Application.Assignments.AssignableDriverCursorCodec.Encode(
                            new Dispatch.Application.Assignments.AssignableDriverCursor(Guid.NewGuid()))
                        : "1",
                });
            if (DispatchEndpoints.TryReadAssignableDriversQuery(query, out _))
            {
                accepted.Add(name);
            }
        }

        return accepted.ToArray();
    }

    private static string[] OrganizationDriversQueryParameters()
    {
        var accepted = new List<string>();
        foreach (var name in new[] { "cursor", "limit", "page_size", "status", "driver_id", "order_id" })
        {
            var query = new Microsoft.AspNetCore.Http.QueryCollection(
                new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
                {
                    [name] = name == "cursor"
                        ? Dispatch.Application.Fleet.OrganizationDriverCursorCodec.Encode(Guid.NewGuid())
                        : "1",
                });
            if (DispatchEndpoints.TryReadOrganizationDriversQuery(query, out _))
            {
                accepted.Add(name);
            }
        }

        // A cursor the assignment picker issued is not a cursor of this operation.
        var picker = new Microsoft.AspNetCore.Http.QueryCollection(
            new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
            {
                ["cursor"] = Dispatch.Application.Assignments.AssignableDriverCursorCodec.Encode(
                    new Dispatch.Application.Assignments.AssignableDriverCursor(Guid.NewGuid())),
            });
        Assert.False(DispatchEndpoints.TryReadOrganizationDriversQuery(picker, out _));
        return accepted.ToArray();
    }

    private static void AssertJsonProperties<T>(params string[] expected)
    {
        var actual = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.GetCustomAttribute<JsonExtensionDataAttribute>() is null)
            .Select(property => property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expected.Order(StringComparer.Ordinal), actual);
    }

    private static string[] RequiredPropertyNames(YamlMappingNode schema) =>
        schema.Sequence("required").Children
            .Select(node => Assert.IsType<YamlScalarNode>(node).Value!)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static int Count(string value, string fragment) =>
        value.Split(fragment, StringSplitOptions.None).Length - 1;
}
