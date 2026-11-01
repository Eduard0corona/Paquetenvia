using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dispatch.Application.Assignments;
using Dispatch.Application.ExternalOffers;
using Dispatch.Application.Stops;
using Identity.Infrastructure.Mock;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Orders.Application.Orders;
using Paqueteria.Application.Diagnostics;
using Routing.Application.Routes;

namespace Paqueteria.IntegrationTests.Observability;

/// <summary>
/// REL-001-INFRASTRUCTURE-503-2026-11-01 (owner decision Z3, "PR aparte ya"): an infrastructure failure of an order
/// transition, a route operation, an assignment, an external offer or the driver's stops answers the AI-05
/// <c>ServiceUnavailable</c> problem (it answered 409 or 403 before, with no log line) and writes exactly one error line,
/// in the API's own JSON console format, that names only the operation id, the innermost exception type and the
/// SQLSTATE. The failure the stub raises carries an e-mail, a phone number and ids in every text field a PostgreSQL error
/// has (message, detail, hint, where, internal query); none of them, no id of the request and no exception text may reach
/// the line, and no other line of the request carries the exception text.
/// </summary>
public sealed partial class InfrastructureFailureHttpTests(InfrastructureFailureHttpTests.Factory factory)
    : IClassFixture<InfrastructureFailureHttpTests.Factory>
{
    private const string Secret = "ana.sintetica@example.test 6671234567";
    private const string ReasonText = "Motivo sintetico del cliente REL001";

    private static readonly Guid OrderId = Guid.Parse("e5030000-0000-0000-0000-000000000001");
    private static readonly Guid DriverId = Guid.Parse("e5030000-0000-0000-0000-000000000002");
    private static readonly Guid CityId = Guid.Parse("e5030000-0000-0000-0000-000000000003");
    private static readonly Guid RouteId = Guid.Parse("e5030000-0000-0000-0000-000000000004");
    private static readonly Guid StopId = Guid.Parse("e5030000-0000-0000-0000-000000000005");
    private static readonly Guid OfferId = Guid.Parse("e5030000-0000-0000-0000-000000000006");

    /// <summary>Every operation the fix moved to 503, by its AI-05 operation id.</summary>
    public static TheoryData<string> Operations { get; } = new()
    {
        "transitionOrder",
        "assignDriver",
        "createRoute",
        "listRoutes",
        "getRoute",
        "addRouteStop",
        "removeRouteStop",
        "reorderRouteStops",
        "createExternalOffer",
        "acceptExternalOffer",
        "listMyEligibleExternalOffers",
        "listMyStops",
    };

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task An_infrastructure_failure_answers_the_503_problem_with_one_redacted_error_line(string operation)
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var key = $"rel001-503-{Guid.NewGuid():N}";
        using var request = Request(operation, key);
        var callsBefore = factory.Failing.Calls(operation);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(callsBefore + 1, factory.Failing.Calls(operation));
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        var traceId = Obs003.ProblemTraceId(body);
        using (var problem = JsonDocument.Parse(body))
        {
            Assert.Equal(
                ["status", "title", "traceId", "type"],
                problem.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
            Assert.Equal(503, problem.RootElement.GetProperty("status").GetInt32());
        }

        Assert.DoesNotContain(Secret, body, StringComparison.Ordinal);

        var lines = factory.Logs.LinesOfRequestScope(factory.Logs.RequestScopeOf(traceId));
        var errors = lines
            .Select(line => (Line: line, Parsed: JsonDocument.Parse(line).RootElement.Clone()))
            .Where(entry => entry.Parsed.GetProperty("LogLevel").GetString() is "Error" or "Critical")
            .ToArray();
        var (text, error) = Assert.Single(errors);

        Assert.Equal(InfrastructureFailureLog.EventId, error.GetProperty("EventId").GetInt32());
        Assert.Equal("Error", error.GetProperty("LogLevel").GetString());
        Assert.False(error.TryGetProperty("Exception", out _), text);
        Assert.Equal(
            $"Operation {operation} answered 503 after an infrastructure failure (PostgresException, SQLSTATE 23505).",
            error.GetProperty("Message").GetString());
        var state = error.GetProperty("State");
        Assert.Equal(operation, state.GetProperty("Operation").GetString());
        Assert.Equal("PostgresException", state.GetProperty("ErrorClass").GetString());
        Assert.Equal("23505", state.GetProperty("SqlState").GetString());
        Assert.Equal(traceId, state.GetProperty("TraceId").GetString());
        // The properties of the line: the three of the template, the trace id and the OBS-003 pseudonym and role kind.
        Assert.Subset(
            new HashSet<string>(StringComparer.Ordinal)
            {
                "Operation", "ErrorClass", "SqlState", "{OriginalFormat}", "TraceId", "OrganizationPseudonym", "ActorRoleKind",
            },
            state.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal));

        // No personal value, id, idempotency key, request text or exception text in the line.
        foreach (var forbidden in new[]
                 {
                     Secret, "ana.sintetica", "6671234567", ReasonText, key, FailingServices.ExceptionText,
                     "duplicate key", "users_email_key", "nbtinsert", MockIdentityProfiles.ViewerOrganizationId.ToString("D"),
                 })
        {
            Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotMatch(UuidPattern(), text);

        // The exception text reaches no line of the request at all, whatever its level or category.
        Assert.All(lines, line =>
        {
            Assert.DoesNotContain(Secret, line, StringComparison.Ordinal);
            Assert.DoesNotContain(FailingServices.ExceptionText, line, StringComparison.Ordinal);
        });
    }

    private static HttpRequestMessage Request(string operation, string key) => operation switch
    {
        "transitionOrder" => Send(
            HttpMethod.Post,
            $"/api/v1/orders/{OrderId:D}/transitions",
            MockIdentityProfiles.ActiveDispatcher,
            key,
            new { target_status = "CANCELLED", reason = ReasonText, expected_version = 1 }),
        "assignDriver" => Send(
            HttpMethod.Post,
            $"/api/v1/orders/{OrderId:D}/assignments",
            MockIdentityProfiles.ActiveDispatcher,
            key,
            new { driver_id = DriverId, assignment_type = "OWN", cost_cents = 1_500, route_id = (Guid?)null }),
        "createRoute" => Send(
            HttpMethod.Post,
            "/api/v1/routes",
            MockIdentityProfiles.ActiveDispatcher,
            key,
            new { driver_id = DriverId, city_id = CityId }),
        "listRoutes" => Send(
            HttpMethod.Get,
            $"/api/v1/routes?driver_id={DriverId:D}",
            MockIdentityProfiles.ActiveDispatcher),
        "getRoute" => Send(HttpMethod.Get, $"/api/v1/routes/{RouteId:D}", MockIdentityProfiles.ActiveDispatcher),
        "addRouteStop" => Send(
            HttpMethod.Post,
            $"/api/v1/routes/{RouteId:D}/stops",
            MockIdentityProfiles.ActiveDispatcher,
            key,
            new { order_id = OrderId, expected_version = 1 }),
        "removeRouteStop" => Send(
            HttpMethod.Delete,
            $"/api/v1/routes/{RouteId:D}/stops/{StopId:D}?expected_version=1",
            MockIdentityProfiles.ActiveDispatcher,
            key),
        "reorderRouteStops" => Send(
            HttpMethod.Put,
            $"/api/v1/routes/{RouteId:D}/stops/order",
            MockIdentityProfiles.ActiveDispatcher,
            key,
            new { expected_version = 1, stop_ids = new[] { StopId } }),
        "createExternalOffer" => Send(
            HttpMethod.Post,
            "/api/v1/external-offers",
            MockIdentityProfiles.ActiveDispatcher,
            key,
            new
            {
                order_id = OrderId,
                commission_cents = 12_345,
                expires_at = new DateTimeOffset(DateTime.UtcNow.AddHours(1).Ticks, TimeSpan.Zero),
            }),
        "acceptExternalOffer" => Send(
            HttpMethod.Post,
            $"/api/v1/external-offers/{OfferId:D}/accept",
            MockIdentityProfiles.ExternalDriver,
            key),
        "listMyEligibleExternalOffers" => Send(
            HttpMethod.Get,
            "/api/v1/driver/me/external-offers",
            MockIdentityProfiles.ExternalDriver),
        "listMyStops" => Send(HttpMethod.Get, "/api/v1/driver/me/stops", MockIdentityProfiles.ActiveDriver),
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null),
    };

    private static HttpRequestMessage Send(
        HttpMethod method,
        string path,
        string profile,
        string? key = null,
        object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", profile);
        request.Headers.Add("X-Organization-Id", MockIdentityProfiles.ViewerOrganizationId.ToString("D"));
        if (key is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }

    [GeneratedRegex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.CultureInvariant)]
    private static partial Regex UuidPattern();

    public sealed class Factory : WebApplicationFactory<Program>
    {
        private readonly JsonConsoleCapture logs = new();

        internal JsonConsoleCapture Logs => logs;

        internal FailingServices Failing { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration(configuration =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Authentication:Provider"] = "Mock",
                    ["IdentityBootstrap:Provider"] = "Mock",
                    // Hosting, routing and authentication lines too, so every line of a request can be searched.
                    ["Logging:LogLevel:Default"] = "Debug",
                    ["Logging:LogLevel:Microsoft.AspNetCore"] = "Debug",
                }));
            builder.ConfigureLogging(logs.AddTo);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IOrderTransitionService>();
                services.RemoveAll<IRouteService>();
                services.RemoveAll<IAssignmentService>();
                services.RemoveAll<IExternalOfferService>();
                services.RemoveAll<IDriverStopsQuery>();
                services.AddSingleton<IOrderTransitionService>(Failing);
                services.AddSingleton<IRouteService>(Failing);
                services.AddSingleton<IAssignmentService>(Failing);
                services.AddSingleton<IExternalOfferService>(Failing);
                services.AddSingleton<IDriverStopsQuery>(Failing);
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                logs.Dispose();
            }
        }
    }

    /// <summary>
    /// Every service of the twelve operations fails as its PostgreSQL implementation does when the store fails: the
    /// module's infrastructure exception around a PostgreSQL unique violation whose every text field carries personal
    /// values and ids.
    /// </summary>
    internal sealed class FailingServices :
        IOrderTransitionService, IRouteService, IAssignmentService, IExternalOfferService, IDriverStopsQuery
    {
        internal const string ExceptionText = "REL001 synthetic store failure";

        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> calls = new(StringComparer.Ordinal);

        internal int Calls(string operation) => calls.GetValueOrDefault(operation);

        public Task<OrderResult> TransitionAsync(TransitionOrderCommand command, CancellationToken cancellationToken) =>
            throw Count("transitionOrder", new OrderTransitionInfrastructureException(ExceptionText, Store()));

        public Task<RouteResult> CreateAsync(CreateRouteCommand command, CancellationToken cancellationToken) =>
            throw Count("createRoute", new RoutingUnavailableException(ExceptionText, Store()));

        public Task<RoutePageResult> ListAsync(ListRoutesQuery query, CancellationToken cancellationToken) =>
            throw Count("listRoutes", new RoutingUnavailableException(ExceptionText, Store()));

        public Task<RouteDetailResult> GetAsync(GetRouteQuery query, CancellationToken cancellationToken) =>
            throw Count("getRoute", new RoutingUnavailableException(ExceptionText, Store()));

        public Task<RouteDetailResult> AddStopAsync(AddRouteStopCommand command, CancellationToken cancellationToken) =>
            throw Count("addRouteStop", new RoutingUnavailableException(ExceptionText, Store()));

        public Task<RouteDetailResult> RemoveStopAsync(
            RemoveRouteStopCommand command,
            CancellationToken cancellationToken) =>
            throw Count("removeRouteStop", new RoutingUnavailableException(ExceptionText, Store()));

        public Task<RouteDetailResult> ReorderStopsAsync(
            ReorderRouteStopsCommand command,
            CancellationToken cancellationToken) =>
            throw Count("reorderRouteStops", new RoutingUnavailableException(ExceptionText, Store()));

        public Task<AssignmentResult> CreateOwnDriverAssignmentAsync(
            CreateOwnDriverAssignmentCommand command,
            CancellationToken cancellationToken) =>
            throw Count("assignDriver", new AssignmentInfrastructureException(ExceptionText, Store()));

        public Task<ExternalOfferResult> CreateAsync(
            CreateExternalOfferCommand command,
            CancellationToken cancellationToken) =>
            throw Count("createExternalOffer", new ExternalOfferInfrastructureException(ExceptionText, Store()));

        public Task<ExternalOfferPageResult> ListEligibleAsync(
            Guid actorId,
            Guid organizationId,
            string? cursor,
            CancellationToken cancellationToken) =>
            throw Count("listMyEligibleExternalOffers", new ExternalOfferInfrastructureException(ExceptionText, Store()));

        public Task<AssignmentResult> AcceptAsync(AcceptExternalOfferCommand command, CancellationToken cancellationToken) =>
            throw Count("acceptExternalOffer", new ExternalOfferInfrastructureException(ExceptionText, Store()));

        public Task<DriverStopListResult> ListCurrentDriverStopsAsync(
            Guid actorId,
            Guid organizationId,
            bool includeDetails,
            CancellationToken cancellationToken) =>
            throw Count("listMyStops", new DriverStopsInfrastructureException(ExceptionText, Store()));

        private Exception Count(string operation, Exception exception)
        {
            calls.AddOrUpdate(operation, 1, static (_, value) => value + 1);
            return exception;
        }

        /// <summary>
        /// A unique violation as PostgreSQL reports it: messageText, severity, invariantSeverity, sqlState, detail, hint,
        /// position, internalPosition, internalQuery, where, schemaName, tableName, columnName, dataTypeName,
        /// constraintName, file, line and routine.
        /// </summary>
        private static PostgresException Store() => new(
            $"duplicate key value violates unique constraint users_email_key ({Secret} {OrderId:D})",
            "ERROR",
            "ERROR",
            PostgresErrorCodes.UniqueViolation,
            $"Key (email)=({Secret}) already exists for {DriverId:D}.",
            $"hint {Secret}",
            0,
            0,
            $"internal query {Secret}",
            $"SQL statement with {Secret} {RouteId:D}",
            "identity",
            "users",
            "email",
            "text",
            "users_email_key",
            "nbtinsert.c",
            "666",
            "_bt_check_unique");
    }
}
