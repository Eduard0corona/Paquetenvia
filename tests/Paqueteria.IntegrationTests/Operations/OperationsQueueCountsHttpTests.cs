using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Identity.Infrastructure.Mock;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Reporting.Application.Operations;

namespace Paqueteria.IntegrationTests.Operations;

/// <summary>
/// UI-PHASE2-QUEUE-COUNTS-2026-10-05: getOperationsQueueCounts over HTTP - any query string is 400, then the
/// operations capability (DISPATCHER; PLATFORM_ADMIN with MFA), then the exact AI-05 representation. A refused or
/// malformed request never reaches the reader.
/// </summary>
public sealed class OperationsQueueCountsHttpTests : IClassFixture<OperationsQueueCountsHttpTests.Factory>
{
    private readonly Factory factory;
    private readonly HttpClient client;

    public OperationsQueueCountsHttpTests(Factory factory)
    {
        this.factory = factory;
        client = factory.CreateClient();
    }

    [Fact]
    public async Task GET_requires_authentication_and_a_tenant()
    {
        var before = factory.Reader.Reads;
        using var anonymous = Request(null);
        using var anonymousResponse = await client.SendAsync(anonymous);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);

        using var noTenant = Request(MockIdentityProfiles.ActiveDispatcher, includeTenant: false);
        using var noTenantResponse = await client.SendAsync(noTenant);
        Assert.Equal(HttpStatusCode.Forbidden, noTenantResponse.StatusCode);
        Assert.Equal(before, factory.Reader.Reads);
    }

    [Theory]
    [InlineData(MockIdentityProfiles.ActiveViewer, null)]
    [InlineData(MockIdentityProfiles.ActiveDriver, null)]
    [InlineData(MockIdentityProfiles.ActiveFinanceMfa, null)]
    [InlineData(MockIdentityProfiles.ActiveWithoutMemberships, null)]
    [InlineData(MockIdentityProfiles.ActivePlatformAdminNoMfa, "MFA_REQUIRED")]
    public async Task GET_refuses_roles_without_the_capability_before_reading(string profile, string? code)
    {
        var before = factory.Reader.Reads;
        using var request = Request(profile);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (code is null)
        {
            Assert.False(problem.RootElement.TryGetProperty("code", out _));
        }
        else
        {
            Assert.Equal(code, problem.RootElement.GetProperty("code").GetString());
        }

        Assert.Equal(before, factory.Reader.Reads);
    }

    [Theory]
    [InlineData("?status=DRAFT")]
    [InlineData("?unassigned=true")]
    [InlineData("?cursor=abc")]
    [InlineData("?x")]
    public async Task GET_rejects_any_query_string_before_capability(string query)
    {
        var before = factory.Reader.Reads;
        foreach (var profile in new[] { MockIdentityProfiles.ActiveDispatcher, MockIdentityProfiles.ActiveViewer })
        {
            using var request = Request(profile, query: query);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        }

        Assert.Equal(before, factory.Reader.Reads);
    }

    [Theory]
    [InlineData(MockIdentityProfiles.ActiveDispatcher)]
    [InlineData(MockIdentityProfiles.ActivePlatformAdminMfa)]
    public async Task GET_returns_the_exact_integer_only_representation(string profile)
    {
        using var request = Request(profile);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
        Assert.Equal(MockIdentityProfiles.ViewerOrganizationId, factory.Reader.LastRequest!.OrganizationId);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal(
            ["generated_at", "total", "by_status", "queues"],
            root.EnumerateObject().Select(property => property.Name));
        Assert.Equal(
            OperationsDashboardVocabulary.Statuses,
            root.GetProperty("by_status").EnumerateObject().Select(property => property.Name));
        Assert.Equal(3, root.GetProperty("by_status").GetProperty("READY_FOR_PICKUP").GetInt64());
        Assert.Equal(0, root.GetProperty("by_status").GetProperty("CANCELLED").GetInt64());
        Assert.Equal(9, root.GetProperty("total").GetInt64());
        var queues = root.GetProperty("queues");
        Assert.Equal(
            ["unassigned", "needs_attention", "price_review", "delivered_not_closed", "en_route"],
            queues.EnumerateObject().Select(property => property.Name));
        Assert.Equal(2, queues.GetProperty("unassigned").GetInt64());
        Assert.Equal(3, queues.GetProperty("needs_attention").GetInt64());
        Assert.Equal(1, queues.GetProperty("price_review").GetInt64());
        Assert.Equal(1, queues.GetProperty("delivered_not_closed").GetInt64());
        Assert.Equal(2, queues.GetProperty("en_route").GetInt64());
        Assert.All(
            root.GetProperty("by_status").EnumerateObject(),
            property => Assert.Equal(JsonValueKind.Number, property.Value.ValueKind));
    }

    [Fact]
    public async Task GET_reports_an_unavailable_reader_as_503()
    {
        factory.Reader.FailNext = true;
        using var request = Request(MockIdentityProfiles.ActiveDispatcher);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.DoesNotContain("queue counts", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    private static HttpRequestMessage Request(string? profile, string query = "", bool includeTenant = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/operations/queue-counts{query}");
        if (profile is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", profile);
        }

        if (includeTenant)
        {
            request.Headers.Add("X-Organization-Id", MockIdentityProfiles.ViewerOrganizationId.ToString("D"));
        }

        return request;
    }

    public sealed class Factory : WebApplicationFactory<Program>
    {
        internal StubReader Reader { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration(configuration =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Authentication:Provider"] = "Mock",
                    ["IdentityBootstrap:Provider"] = "Mock",
                }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IOperationsQueueCountsReader>();
                services.AddSingleton<IOperationsQueueCountsReader>(Reader);
            });
        }
    }

    internal sealed class StubReader : IOperationsQueueCountsReader
    {
        private int reads;

        public int Reads => Volatile.Read(ref reads);

        public OperationsQueueCountsRequest? LastRequest { get; private set; }

        public bool FailNext { get; set; }

        public Task<OperationsQueueCounts> ReadAsync(
            OperationsQueueCountsRequest request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref reads);
            LastRequest = request;
            if (FailNext)
            {
                FailNext = false;
                throw new OperationsDashboardUnavailableException("The operations queue counts are unavailable.");
            }

            return Task.FromResult(OperationsQueueCounts.From(
                new DateTimeOffset(2026, 10, 5, 18, 0, 0, TimeSpan.Zero),
                [
                    new OperationsQueueStatusRow("READY_FOR_PICKUP", 3, 1, 0),
                    new OperationsQueueStatusRow("RESCHEDULED", 1, 1, 0),
                    new OperationsQueueStatusRow("FAILED_ATTEMPT", 1, 0, 0),
                    new OperationsQueueStatusRow("RETURNING", 1, 0, 1),
                    new OperationsQueueStatusRow("IN_TRANSIT", 1, 0, 0),
                    new OperationsQueueStatusRow("DELIVERING", 1, 0, 0),
                    new OperationsQueueStatusRow("DELIVERED", 1, 0, 0),
                ]));
        }
    }
}
