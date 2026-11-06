using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Identity.Infrastructure.Mock;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Paqueteria.Api.Tenancy;
using Paqueteria.Infrastructure.Observability;
using Paqueteria.IntegrationTests.Observability;
using Paqueteria.IntegrationTests.Orders;

namespace Paqueteria.IntegrationTests.Security;

/// <summary>
/// REL-001-CROSS-TENANT-SECURITY-EVENT-2026-11-06 (owner answer S4, "Evento en el log (Recommended)"), the last step of
/// the AI-09 scenario "Un aliado no puede leer la orden de otro aliado": "debe existir un evento de seguridad sin exponer
/// datos de O". Through the real API pipeline, with every line formatted by the API's own JSON console formatter:
/// <list type="bullet">
/// <item>a refused organization (403) and an order read that ends in the uniform 404 each write exactly one line with the
/// fixed event id 4201;</item>
/// <item>its state holds only the refusal type, the route template, the pseudonym of the actor's own organization, the
/// actor's role kind and the request's trace id, and none of the ids the request used;</item>
/// <item>a missing order and another tenant's order give the same line;</item>
/// <item>an allowed read, and a refusal that is not about another tenant, write none.</item>
/// </list>
/// </summary>
public sealed class CrossTenantSecurityEventHttpTests(OrderHttpWebApplicationFactory factory)
    : IClassFixture<OrderHttpWebApplicationFactory>
{
    private const string OrderRoute = "/api/v1/orders/{orderId:guid}";

    /// <summary>The user ids of the mock profiles these tests send (MockIdentityProfiles.AuthorizationProfiles).</summary>
    private static readonly IReadOnlyDictionary<string, string> UserIds = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [MockIdentityProfiles.ActiveDispatcher] = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaa10",
        [MockIdentityProfiles.ActiveAllyAdmin] = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaa33",
        [MockIdentityProfiles.ActiveMultiOrganization] = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa4",
        [MockIdentityProfiles.ActiveWithoutMemberships] = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa9",
        [MockIdentityProfiles.SuspendedMembership] = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa7",
    };

    /// <summary>
    /// An actor asks for an organization it is not an active member of. The line carries the pseudonym of the actor's own
    /// organization (its default membership's) and the actor's role kind there; never the requested organization.
    /// </summary>
    [Theory]
    [InlineData(MockIdentityProfiles.ActiveDispatcher, "DISPATCHER")]
    [InlineData(MockIdentityProfiles.ActiveAllyAdmin, "ALLY_ADMIN")]
    [InlineData(MockIdentityProfiles.ActiveMultiOrganization, "VIEWER")]
    public async Task A_refused_organization_writes_one_event_with_the_actors_own_organization(string profile, string roleKind)
    {
        using var logs = new JsonConsoleCapture();
        await using var api = Api(logs);
        using var client = api.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var orderId = Guid.NewGuid();

        using var response = await client.SendAsync(
            Get($"/api/v1/orders/{orderId:D}", profile, MockIdentityProfiles.ForeignOrganizationId));

        await CapabilityMatrix.AssertForbiddenAsync(response);
        var traceId = Obs003.ProblemTraceId(await response.Content.ReadAsStringAsync());
        var line = Assert.Single(Events(logs));
        AssertState(
            line,
            CrossTenantSecurityEvent.OrganizationRefused,
            OrderRoute,
            Pseudonym(api, MockIdentityProfiles.ViewerOrganizationId),
            roleKind,
            traceId);
        AssertCarriesNoIdentifier(line.Raw, profile, orderId, MockIdentityProfiles.ForeignOrganizationId);
    }

    /// <summary>
    /// An actor with no active membership at all (none, or only a suspended one, which the identity context leaves out
    /// as it leaves out a membership of an inactive organization) has no own organization: the line carries neither the
    /// pseudonym nor the role kind.
    /// </summary>
    [Theory]
    [InlineData(MockIdentityProfiles.ActiveWithoutMemberships)]
    [InlineData(MockIdentityProfiles.SuspendedMembership)]
    public async Task A_refused_organization_without_an_own_organization_carries_no_pseudonym_or_role_kind(string profile)
    {
        using var logs = new JsonConsoleCapture();
        await using var api = Api(logs);
        using var client = api.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var orderId = Guid.NewGuid();

        using var response = await client.SendAsync(
            Get($"/api/v1/orders/{orderId:D}", profile, MockIdentityProfiles.ViewerOrganizationId));

        await CapabilityMatrix.AssertForbiddenAsync(response);
        var traceId = Obs003.ProblemTraceId(await response.Content.ReadAsStringAsync());
        var line = Assert.Single(Events(logs));
        AssertState(line, CrossTenantSecurityEvent.OrganizationRefused, OrderRoute, pseudonym: null, roleKind: null, traceId);
        AssertCarriesNoIdentifier(line.Raw, profile, orderId, MockIdentityProfiles.ViewerOrganizationId);
    }

    /// <summary>
    /// Inside the actor's own organization, a missing order and another tenant's order give the same uniform 404 and the
    /// same line: only the trace id differs. Neither order id is written.
    /// </summary>
    [Fact]
    public async Task A_missing_and_another_tenants_order_write_the_same_event()
    {
        using var logs = new JsonConsoleCapture();
        await using var api = Api(logs);
        using var client = api.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var missing = Guid.NewGuid();
        var foreign = OrderHttpWebApplicationFactory.ForeignOrderId;
        const string profile = MockIdentityProfiles.ActiveDispatcher;

        using var missingResponse = await client.SendAsync(
            Get($"/api/v1/orders/{missing:D}", profile, MockIdentityProfiles.ViewerOrganizationId));
        var missingTrace = await AssertUniformNotFoundAsync(missingResponse);
        using var foreignResponse = await client.SendAsync(
            Get($"/api/v1/orders/{foreign:D}", profile, MockIdentityProfiles.ViewerOrganizationId));
        var foreignTrace = await AssertUniformNotFoundAsync(foreignResponse);

        var lines = Events(logs);
        Assert.Equal(2, lines.Count);
        var pseudonym = Pseudonym(api, MockIdentityProfiles.ViewerOrganizationId);
        AssertState(lines[0], CrossTenantSecurityEvent.OrderNotFound, OrderRoute, pseudonym, "DISPATCHER", missingTrace);
        AssertState(lines[1], CrossTenantSecurityEvent.OrderNotFound, OrderRoute, pseudonym, "DISPATCHER", foreignTrace);
        Assert.Equal(WithoutTraceId(lines[0]), WithoutTraceId(lines[1]));
        foreach (var line in lines)
        {
            AssertCarriesNoIdentifier(line.Raw, profile, missing, MockIdentityProfiles.ViewerOrganizationId);
            AssertCarriesNoIdentifier(line.Raw, profile, foreign, MockIdentityProfiles.ViewerOrganizationId);
        }
    }

    /// <summary>
    /// An allowed read (the order and the list) writes no event, nor does a refusal that is not about another tenant: a
    /// role without the read capability in its own organization and a malformed organization header (both 403 here).
    /// </summary>
    [Fact]
    public async Task An_allowed_read_writes_no_event()
    {
        using var logs = new JsonConsoleCapture();
        await using var api = Api(logs);
        using var client = api.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var created = await client.SendAsync(CreateOrder(MockIdentityProfiles.ActiveDispatcher));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var orderId = body.RootElement.GetProperty("id").GetGuid();

        using var detail = await client.SendAsync(
            Get($"/api/v1/orders/{orderId:D}", MockIdentityProfiles.ActiveDispatcher, MockIdentityProfiles.ViewerOrganizationId));
        using var list = await client.SendAsync(
            Get("/api/v1/orders", MockIdentityProfiles.ActiveDispatcher, MockIdentityProfiles.ViewerOrganizationId));
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);

        using var denied = await client.SendAsync(
            Get($"/api/v1/orders/{orderId:D}", MockIdentityProfiles.ActiveAllyAdmin, MockIdentityProfiles.ViewerOrganizationId));
        await CapabilityMatrix.AssertForbiddenAsync(denied);
        using var malformed = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/orders/{orderId:D}");
        malformed.Headers.Authorization = new AuthenticationHeaderValue("Bearer", MockIdentityProfiles.ActiveDispatcher);
        malformed.Headers.TryAddWithoutValidation("X-Organization-Id", "not-a-uuid");
        using var invalid = await client.SendAsync(malformed);
        Assert.Equal(HttpStatusCode.Forbidden, invalid.StatusCode);

        Assert.NotEmpty(logs.Lines);
        Assert.Empty(Events(logs));
    }

    /// <summary>
    /// The order reads the event covers are exactly the GET operations on an order and its sub-resources, each of which
    /// answers a missing or foreign order with the uniform 404; a new one fails here until it is reviewed.
    /// </summary>
    [Fact]
    public async Task The_order_reads_are_the_gets_of_an_order_and_its_sub_resources()
    {
        using var logs = new JsonConsoleCapture();
        await using var api = Api(logs);

        var endpoints = api.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToArray();
        var reads = endpoints
            .Where(endpoint => endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods is { } methods &&
                               methods.Any(method => CrossTenantSecurityEvent.IsOrderRead(
                                   method,
                                   CrossTenantSecurityEvent.RouteTemplate(endpoint))))
            .Select(endpoint => $"{endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName} {endpoint.RoutePattern.RawText}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                "getOrder /api/v1/orders/{orderId:guid}",
                "getOrderFinancials /api/v1/orders/{orderId}/financials",
                "listAssignableDrivers /api/v1/orders/{orderId}/assignable-drivers",
                "listOrderProofs /api/v1/orders/{orderId}/proofs",
                "listOrderRecipientNotices /api/v1/orders/{orderId}/recipient-notices",
            ],
            reads);
        Assert.False(CrossTenantSecurityEvent.IsOrderRead("POST", "/api/v1/orders/{orderId:guid}/transitions"));
        Assert.False(CrossTenantSecurityEvent.IsOrderRead("GET", "/api/v1/orders"));
        Assert.Equal(CrossTenantSecurityEvent.UnknownRoute, CrossTenantSecurityEvent.RouteTemplate(null));
    }

    private WebApplicationFactory<Program> Api(JsonConsoleCapture logs) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureLogging(logs.AddTo));

    private static string Pseudonym(WebApplicationFactory<Program> api, Guid organizationId)
    {
        var pseudonym = api.Services.GetRequiredService<IOrganizationPseudonymizer>().Pseudonymize(organizationId);
        Assert.True(OrganizationPseudonym.IsPseudonym(pseudonym));
        return pseudonym;
    }

    private static HttpRequestMessage Get(string path, string profile, Guid organizationId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", profile);
        request.Headers.Add("X-Organization-Id", organizationId.ToString("D"));
        return request;
    }

    private static HttpRequestMessage CreateOrder(string profile)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders")
        {
            Content = JsonContent.Create(new
            {
                quote_id = Guid.NewGuid(),
                payer_type = "SENDER",
                restricted_goods_acknowledged = true,
                acceptance = new
                {
                    terms_version = "terms-synthetic-v1",
                    privacy_version = "privacy-synthetic-v1",
                    accepted_at = DateTimeOffset.UtcNow.AddMinutes(-10),
                    acceptance_channel = "WEB",
                },
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", profile);
        request.Headers.Add("X-Organization-Id", MockIdentityProfiles.ViewerOrganizationId.ToString("D"));
        request.Headers.Add("Idempotency-Key", $"rel001-s4-{Guid.NewGuid():N}");
        return request;
    }

    private static async Task<string> AssertUniformNotFoundAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using (var problem = JsonDocument.Parse(body))
        {
            Assert.Equal(404, problem.RootElement.GetProperty("status").GetInt32());
            Assert.Equal("Not found.", problem.RootElement.GetProperty("title").GetString());
        }

        return Obs003.ProblemTraceId(body);
    }

    private sealed record EventLine(string Raw, JsonElement Root, JsonElement State);

    private static IReadOnlyList<EventLine> Events(JsonConsoleCapture logs) =>
        logs.Lines
            .Select(line => (Line: line, Root: JsonDocument.Parse(line).RootElement.Clone()))
            .Where(entry => entry.Root.GetProperty("EventId").GetInt32() == CrossTenantSecurityEvent.Id)
            .Select(entry => new EventLine(entry.Line, entry.Root, entry.Root.GetProperty("State")))
            .ToArray();

    private static void AssertState(
        EventLine line,
        string refusalType,
        string routeTemplate,
        string? pseudonym,
        string? roleKind,
        string traceId)
    {
        Assert.Equal("Warning", line.Root.GetProperty("LogLevel").GetString());
        Assert.Equal(typeof(CrossTenantSecurityEvent).FullName, line.Root.GetProperty("Category").GetString());
        var message = $"Cross-tenant access refused ({refusalType}) on {routeTemplate}.";
        Assert.Equal(message, line.Root.GetProperty("Message").GetString());

        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["RefusalType"] = refusalType,
            ["RouteTemplate"] = routeTemplate,
            ["{OriginalFormat}"] = "Cross-tenant access refused ({RefusalType}) on {RouteTemplate}.",
            [TraceCorrelation.LogProperty] = traceId,
        };
        if (pseudonym is not null)
        {
            expected[TelemetryOrganizationContext.PseudonymLogProperty] = pseudonym;
        }

        if (roleKind is not null)
        {
            expected[TelemetryOrganizationContext.RoleKindLogProperty] = roleKind;
        }

        var actual = line.State.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.GetString()!);
        Assert.Equal(expected.OrderBy(pair => pair.Key, StringComparer.Ordinal), actual.OrderBy(pair => pair.Key, StringComparer.Ordinal));
    }

    private static string WithoutTraceId(EventLine line) => string.Join(
        '\n',
        line.State.EnumerateObject()
            .Where(property => property.Name != TraceCorrelation.LogProperty)
            .Select(property => $"{property.Name}={property.Value.GetString()}"));

    /// <summary>
    /// The serialized line holds none of the ids the request used or the actor has: the order id, the requested
    /// organization (the X-Organization-Id value), every organization of the mock profiles, the user id, the subject,
    /// the credential, an e-mail or a loopback address. Each is checked in the forms a Guid can be written in.
    /// </summary>
    private static void AssertCarriesNoIdentifier(string raw, string profile, Guid orderId, Guid requestedOrganization)
    {
        var guids = new[]
        {
            orderId,
            requestedOrganization,
            MockIdentityProfiles.ViewerOrganizationId,
            MockIdentityProfiles.OperationsOrganizationId,
            MockIdentityProfiles.ForeignOrganizationId,
            Guid.Parse(UserIds[profile]),
        };
        foreach (var guid in guids)
        {
            foreach (var format in new[] { "D", "N" })
            {
                Assert.DoesNotContain(guid.ToString(format), raw, StringComparison.OrdinalIgnoreCase);
            }
        }

        foreach (var forbidden in new[] { profile, "mock-subject", "@", "127.0.0.1", "::1", "X-Organization-Id", "/api/v1/orders/" + orderId.ToString("D") })
        {
            Assert.DoesNotContain(forbidden, raw, StringComparison.OrdinalIgnoreCase);
        }
    }
}
