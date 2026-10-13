using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Custody.Endpoints;
using Dispatch.Endpoints;
using Finance.Endpoints;
using Identity.Endpoints.AuthCenter;
using Identity.Infrastructure.Mock;
using Locations.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orders.Endpoints;
using Organizations.Application.Auditing;
using Paqueteria.Application.Security;
using Paqueteria.Infrastructure.Observability;
using Paqueteria.Infrastructure.Security.RateLimiting;
using Paqueteria.IntegrationTests.Security.AuthCenter;
using Pricing.Endpoints;
using Reporting.Endpoints;

namespace Paqueteria.IntegrationTests.Security;

/// <summary>
/// SEC-004 (phase 4 PR 3), over the assembled API: the decided list (SEC-004-RATE-LIMITS-2026-10-12) with the owner's
/// answers of 2026-10-12. Every new policy answers 429 <c>application/problem+json</c> with <c>traceId</c> and
/// <c>Retry-After</c> past its limit, per user; CSV preview and commit share one budget and one commit per user runs at
/// a time (Q1); the photo steps (Q2) and the dashboard and counts (Q3) keep separate budgets; the dashboard's 429 has
/// the shape of every other (Q4); sign-in starts are limited per client network and the callback is not (Q5); a
/// browser navigation sees the owner's sign-in page (Q6); and the per-minute OBS-002 summary names registry policies
/// only (Q8). The numbers are validated configuration: an invalid one stops the API.
/// </summary>
public sealed class Sec004HeavyEndpointLimitsTests
{
    private const string NetworkA = "203.0.113.51";
    private const string NetworkB = "203.0.113.52";
    private const string OrderId = "5d8e1b5c-7a3f-4c0e-9a51-2f3c4b5a6d70";
    private const string SettlementId = "6e9f2c6d-8b4a-4d1f-8b62-3a4d5c6b7e81";
    private const string SignInPage =
        "Demasiados intentos de inicio de sesión desde esta red. Espera un minuto e intenta de nuevo.";

    private static readonly string[] NewPolicies =
    [
        CsvOrderImportEndpoints.RateLimitPolicy,
        CsvOrderImportEndpoints.CommitRateLimitPolicy,
        SettlementEndpoints.ExportRateLimitPolicy,
        LocationEndpoints.CreateRateLimitPolicy,
        ProofEndpoints.UploadSessionRateLimitPolicy,
        ProofEndpoints.FinalizeRateLimitPolicy,
        OperationsDashboardEndpoints.RateLimitPolicy,
        OperationsQueueCountsEndpoints.RateLimitPolicy,
        OrderEndpoints.ListRateLimitPolicy,
        QuoteEndpoints.CreateRateLimitPolicy,
        DispatchEndpoints.AssignableDriversRateLimitPolicy,
        AuthCenterDefaults.SignInRateLimitPolicy,
    ];

    /// <summary>Policy, method, path and a caller that may use the operation.</summary>
    public static TheoryData<string, string, string, string> PerUserOperations => new()
    {
        { OperationsDashboardEndpoints.RateLimitPolicy, "GET", "/api/v1/operations/dashboard", MockIdentityProfiles.ActiveDispatcher },
        { OperationsQueueCountsEndpoints.RateLimitPolicy, "GET", "/api/v1/operations/queue-counts", MockIdentityProfiles.ActiveDispatcher },
        { OrderEndpoints.ListRateLimitPolicy, "GET", "/api/v1/orders", MockIdentityProfiles.ActiveDispatcher },
        { QuoteEndpoints.CreateRateLimitPolicy, "POST", "/api/v1/quotes", MockIdentityProfiles.ActiveDispatcher },
        { DispatchEndpoints.AssignableDriversRateLimitPolicy, "GET", $"/api/v1/orders/{OrderId}/assignable-drivers", MockIdentityProfiles.ActiveDispatcher },
        { LocationEndpoints.CreateRateLimitPolicy, "POST", "/api/v1/locations", MockIdentityProfiles.ActiveDispatcher },
        { SettlementEndpoints.ExportRateLimitPolicy, "GET", $"/api/v1/settlements/{SettlementId}/export.csv", MockIdentityProfiles.ActiveFinanceMfa },
        { ProofEndpoints.UploadSessionRateLimitPolicy, "POST", $"/api/v1/orders/{OrderId}/proof-upload-sessions", MockIdentityProfiles.ActiveDriver },
        { ProofEndpoints.FinalizeRateLimitPolicy, "POST", $"/api/v1/orders/{OrderId}/proofs", MockIdentityProfiles.ActiveDriver },
        { CsvOrderImportEndpoints.RateLimitPolicy, "POST", "/api/v1/orders/csv/preview", MockIdentityProfiles.ActiveDispatcher },
    };

    [Theory]
    [MemberData(nameof(PerUserOperations))]
    public async Task Each_policy_answers_the_TooManyRequests_problem_per_user_past_its_limit(
        string policy,
        string method,
        string path,
        string profile)
    {
        await using var host = new LimitsFactory(permitLimit: 1);
        using var client = host.CreateClient();

        using var first = await client.SendAsync(Request(method, path, profile, NetworkA));
        using var second = await client.SendAsync(Request(method, path, profile, NetworkA));
        using var sameUserElsewhere = await client.SendAsync(Request(method, path, profile, NetworkB));
        using var otherUser = await client.SendAsync(Request(method, path, MockIdentityProfiles.ActiveViewer, NetworkA));

        Assert.NotEqual(HttpStatusCode.TooManyRequests, first.StatusCode);
        await AssertProblemAsync(second, retryAfter: "60");
        // Per user (the subject's hash), not per network: the same user elsewhere has no budget left, another has his.
        Assert.Equal(HttpStatusCode.TooManyRequests, sameUserElsewhere.StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, otherUser.StatusCode);
        Assert.Equal([(policy, 2L)], host.Summary.TakeSnapshot().Where(item => item.Rejected > 0));
    }

    [Fact]
    public async Task Dashboard_and_counts_and_both_photo_steps_keep_separate_budgets()
    {
        await using var host = new LimitsFactory(permitLimit: 1);
        using var client = host.CreateClient();
        const string dispatcher = MockIdentityProfiles.ActiveDispatcher;
        const string driver = MockIdentityProfiles.ActiveDriver;

        // Owner answer Q3, "Uno cada uno": the dashboard spends nothing of the counts' budget, and back.
        using var dashboard = await client.SendAsync(Request("GET", "/api/v1/operations/dashboard", dispatcher, NetworkA));
        using var dashboardAgain = await client.SendAsync(Request("GET", "/api/v1/operations/dashboard", dispatcher, NetworkA));
        using var counts = await client.SendAsync(Request("GET", "/api/v1/operations/queue-counts", dispatcher, NetworkA));
        Assert.NotEqual(HttpStatusCode.TooManyRequests, dashboard.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, dashboardAgain.StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, counts.StatusCode);

        // Owner answer Q2, "60 cada paso": requesting the upload and confirming it are two budgets.
        using var upload = await client.SendAsync(Request("POST", $"/api/v1/orders/{OrderId}/proof-upload-sessions", driver, NetworkA));
        using var uploadAgain = await client.SendAsync(Request("POST", $"/api/v1/orders/{OrderId}/proof-upload-sessions", driver, NetworkA));
        using var finalize = await client.SendAsync(Request("POST", $"/api/v1/orders/{OrderId}/proofs", driver, NetworkA));
        Assert.NotEqual(HttpStatusCode.TooManyRequests, upload.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, uploadAgain.StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, finalize.StatusCode);

        // Listing a driver's photos is not finalizing one: GET of the same path is not limited.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var list = await client.SendAsync(Request("GET", $"/api/v1/orders/{OrderId}/proofs", driver, NetworkA));
            Assert.NotEqual(HttpStatusCode.TooManyRequests, list.StatusCode);
        }
    }

    [Fact]
    public async Task Csv_preview_and_commit_share_one_budget_per_user()
    {
        await using var host = new LimitsFactory(permitLimit: 100, csvLimit: 2);
        using var client = host.CreateClient();
        const string dispatcher = MockIdentityProfiles.ActiveDispatcher;

        using var preview = await client.SendAsync(Request("POST", "/api/v1/orders/csv/preview", dispatcher, NetworkA));
        using var commit = await client.SendAsync(Request("POST", "/api/v1/orders/csv/commit", dispatcher, NetworkA));
        using var previewOver = await client.SendAsync(Request("POST", "/api/v1/orders/csv/preview", dispatcher, NetworkA));
        using var commitOver = await client.SendAsync(Request("POST", "/api/v1/orders/csv/commit", dispatcher, NetworkA));
        using var otherUser = await client.SendAsync(Request("POST", "/api/v1/orders/csv/commit", MockIdentityProfiles.ActiveViewer, NetworkA));

        Assert.NotEqual(HttpStatusCode.TooManyRequests, preview.StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, commit.StatusCode);
        await AssertProblemAsync(previewOver, retryAfter: "60");
        await AssertProblemAsync(commitOver, retryAfter: "60");
        Assert.NotEqual(HttpStatusCode.TooManyRequests, otherUser.StatusCode);
        // Both rejections are the shared limit's, whichever endpoint met it.
        Assert.Equal(
            [(CsvOrderImportEndpoints.RateLimitPolicy, 2L)],
            host.Summary.TakeSnapshot().Where(item => item.Rejected > 0));
    }

    [Fact]
    public async Task A_second_commit_of_the_same_user_waits_until_the_first_has_answered()
    {
        await using var host = new LimitsFactory(permitLimit: 100, csvLimit: 10);
        using var client = host.CreateClient();
        // A PLATFORM_ADMIN with MFA: the tenant middleware records its tenant activation, after the limiter, and the
        // test holds that call to keep the first commit in flight.
        const string admin = MockIdentityProfiles.ActivePlatformAdminMfa;

        var running = client.SendAsync(Request("POST", "/api/v1/orders/csv/commit", admin, NetworkA));
        try
        {
            await host.Audit.Entered.WaitAsync(TimeSpan.FromSeconds(30));

            // The same user, even from another network: the first commit has not answered yet.
            using (var second = await client.SendAsync(Request("POST", "/api/v1/orders/csv/commit", admin, NetworkB)))
            {
                await AssertProblemAsync(second, retryAfter: "60");
            }

            // The rule holds commits only: a preview of the same user, and another user's commit, go through.
            using (var preview = await client.SendAsync(Request("POST", "/api/v1/orders/csv/preview", admin, NetworkA)))
            using (var otherCommit = await client.SendAsync(Request("POST", "/api/v1/orders/csv/commit", MockIdentityProfiles.ActiveDispatcher, NetworkA)))
            {
                Assert.NotEqual(HttpStatusCode.TooManyRequests, preview.StatusCode);
                Assert.NotEqual(HttpStatusCode.TooManyRequests, otherCommit.StatusCode);
            }
        }
        finally
        {
            host.Audit.Release();
        }

        using (var first = await running.WaitAsync(TimeSpan.FromSeconds(30)))
        {
            Assert.NotEqual(HttpStatusCode.TooManyRequests, first.StatusCode);
        }

        using var afterwards = await client.SendAsync(Request("POST", "/api/v1/orders/csv/commit", admin, NetworkA));
        Assert.NotEqual(HttpStatusCode.TooManyRequests, afterwards.StatusCode);
        // The rejection is the in-flight rule's, and it spent nothing of the shared window: 4 of 10 were spent.
        Assert.Equal(
            [(CsvOrderImportEndpoints.CommitRateLimitPolicy, 1L)],
            host.Summary.TakeSnapshot().Where(item => item.Rejected > 0));
    }

    [Fact]
    public async Task Sign_in_starts_are_limited_per_client_network_and_the_callback_is_not()
    {
        using var factory = new AuthCenterWebApplicationFactory(new Dictionary<string, string?>
        {
            ["RateLimits:SignIn:PermitLimit"] = "2",
            ["RateLimits:SignIn:WindowSeconds"] = "60",
        });
        using var host = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddSingleton<IStartupFilter, PeerAddress>()));
        using var browser = host.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri(FakeAuthCenterServer.PublicOrigin),
        });

        using var first = await browser.SendAsync(SignIn(NetworkA, Navigation));
        using var second = await browser.SendAsync(SignIn(NetworkA, Navigation));
        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, second.StatusCode);

        // Owner answer Q6: the browser sees the simple page, verbatim, and nothing else.
        using var page = await browser.SendAsync(SignIn(NetworkA, Navigation));
        Assert.Equal(HttpStatusCode.TooManyRequests, page.StatusCode);
        Assert.Equal("text/html; charset=utf-8", page.Content.Headers.ContentType?.ToString());
        Assert.Equal("60", Assert.Single(page.Headers.GetValues("Retry-After")));
        Assert.Equal("no-store", page.Headers.CacheControl?.ToString());
        Assert.Equal(RateLimitRejectionPage.ContentSecurityPolicy, Assert.Single(page.Headers.GetValues("Content-Security-Policy")));
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains($"<p>{SignInPage}</p>", html, StringComparison.Ordinal);
        Assert.Equal(Encoding.UTF8.GetString(new RateLimitRejectionPage(SignInPage).Body.Span), html);
        Assert.Null(page.Headers.Location);

        // Anything that does not ask for HTML gets the problem, like every other 429.
        using var problem = await browser.SendAsync(SignIn(NetworkA, "application/json"));
        await AssertProblemAsync(problem, retryAfter: "60");

        // Per client network: another network still starts.
        using var elsewhere = await browser.SendAsync(SignIn(NetworkB, Navigation));
        Assert.Equal(HttpStatusCode.Redirect, elsewhere.StatusCode);

        // Owner answer Q5, "Solo /auth/login": the callback is never limited, so a sign-in that started still ends.
        for (var attempt = 0; attempt < 25; attempt++)
        {
            using var callback = new HttpRequestMessage(HttpMethod.Get, "/signin-authcenter?state=synthetic&iss=https%3A%2F%2Fauthcenter.test");
            callback.Headers.Add(PeerAddress.Header, NetworkA);
            using var answer = await browser.SendAsync(callback);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, answer.StatusCode);
        }

        // The endpoint carries the policy and the page; no other endpoint has a page.
        var endpoints = host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToArray();
        var login = Assert.Single(endpoints, endpoint => endpoint.RoutePattern.RawText == AuthCenterDefaults.LoginPath);
        Assert.Equal(AuthCenterDefaults.SignInRateLimitPolicy, login.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName);
        Assert.Equal(SignInPage, login.Metadata.GetMetadata<RateLimitRejectionPage>()?.Message);
        Assert.Equal(
            [AuthCenterDefaults.LoginPath],
            endpoints.Where(endpoint => endpoint.Metadata.GetMetadata<RateLimitRejectionPage>() is not null)
                .Select(endpoint => endpoint.RoutePattern.RawText));
        Assert.DoesNotContain(
            endpoints,
            endpoint => endpoint.RoutePattern.RawText == AuthCenterDefaults.CallbackPath &&
                endpoint.Metadata.GetMetadata<EnableRateLimitingAttribute>() is not null);
        Assert.Equal(
            [(AuthCenterDefaults.SignInRateLimitPolicy, 2L)],
            host.Services.GetRequiredService<RateLimitRejectionSummary>().TakeSnapshot().Where(item => item.Rejected > 0));
    }

    [Theory]
    [InlineData("RateLimits:CsvImport:PermitLimit", "0", "RateLimits:CsvImport")]
    [InlineData("RateLimits:OrderList:PermitLimit", "10001", "RateLimits:OrderList")]
    [InlineData("RateLimits:SignIn:WindowSeconds", "3601", "RateLimits:SignIn")]
    [InlineData("RateLimits:ProofFinalize:WindowSeconds", "0", "RateLimits:ProofFinalize")]
    [InlineData("RateLimits:Quote:PermitLimit", "-5", "RateLimits:Quote")]
    [InlineData("RateLimits:OperationsDashboard:PermitLimit", "sixty", "RateLimits")]
    public void An_invalid_number_stops_the_api_at_start(string key, string value, string named)
    {
        using var host = new LimitsFactory(permitLimit: 100, overrides: new Dictionary<string, string?> { [key] = value });

        var failure = Assert.ThrowsAny<Exception>(() => host.Services);

        Assert.Contains(named, Flatten(failure), StringComparison.Ordinal);
    }

    [Fact]
    public void The_decided_limits_have_the_owners_numbers()
    {
        // The API's own appsettings.json, as deployed, and the code defaults behind it.
        using var host = new DeployedConfigurationFactory();
        var registry = host.Services.GetRequiredService<RateLimitPolicyRegistry>();
        var minute = TimeSpan.FromSeconds(60);

        var expected = new Dictionary<string, (RateLimitPartitionKind Partition, FixedWindowLimit Limit)>(StringComparer.Ordinal)
        {
            ["Orders.CsvImport"] = (RateLimitPartitionKind.SubjectOrClientNetwork, new FixedWindowLimit(10, minute)),
            ["Orders.CsvImportCommit"] = (RateLimitPartitionKind.SubjectOrClientNetwork, new FixedWindowLimit(10, minute)),
            ["Finance.SettlementExport"] = (RateLimitPartitionKind.SubjectOrClientNetwork, new FixedWindowLimit(10, minute)),
            ["Locations.Create"] = (RateLimitPartitionKind.SubjectOrClientNetwork, new FixedWindowLimit(30, minute)),
            ["Custody.ProofUploadSession"] = (RateLimitPartitionKind.SubjectOrClientNetwork, new FixedWindowLimit(60, minute)),
            ["Custody.ProofFinalize"] = (RateLimitPartitionKind.SubjectOrClientNetwork, new FixedWindowLimit(60, minute)),
            ["Reporting.OperationsDashboard"] = (RateLimitPartitionKind.SubjectOrClientNetwork, new FixedWindowLimit(60, minute)),
            ["Reporting.QueueCounts"] = (RateLimitPartitionKind.SubjectOrClientNetwork, new FixedWindowLimit(60, minute)),
            ["Orders.List"] = (RateLimitPartitionKind.SubjectOrClientNetwork, new FixedWindowLimit(120, minute)),
            ["Pricing.Quote"] = (RateLimitPartitionKind.SubjectOrClientNetwork, new FixedWindowLimit(60, minute)),
            ["Dispatch.AssignableDrivers"] = (RateLimitPartitionKind.SubjectOrClientNetwork, new FixedWindowLimit(60, minute)),
            ["Identity.SignIn"] = (RateLimitPartitionKind.ClientNetwork, new FixedWindowLimit(20, minute)),
        };

        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), NewPolicies.Order(StringComparer.Ordinal));
        foreach (var (name, (partition, limit)) in expected)
        {
            var policy = Assert.IsType<RateLimitPolicy>(registry.Find(name));
            Assert.Equal(partition, policy.Partition);
            Assert.Equal(limit, policy.Limit(host.Services));
        }

        // Owner answer Q1: the commit spends the preview's window and runs one at a time per user; nothing else
        // shares a window or has an in-flight rule.
        var commit = registry.Find(CsvOrderImportEndpoints.CommitRateLimitPolicy)!;
        Assert.Equal(CsvOrderImportEndpoints.RateLimitPolicy, commit.SharesWindowOf);
        Assert.Equal(1, commit.InFlightLimit);
        Assert.Equal(
            [CsvOrderImportEndpoints.CommitRateLimitPolicy],
            registry.Policies.Where(policy => policy.SharesWindowOf is not null || policy.InFlightLimit is not null)
                .Select(policy => policy.Name));

        // The code defaults are the same numbers, so a deployment without the section keeps them.
        var defaults = new Paqueteria.Api.Http.ApiRateLimitOptions();
        Assert.Null(defaults.Invalid());
        Assert.Equal(
            [10, 10, 30, 60, 60, 60, 60, 120, 60, 60, 20],
            new[]
            {
                defaults.CsvImport, defaults.SettlementExport, defaults.LocationCreate, defaults.ProofUploadSession,
                defaults.ProofFinalize, defaults.OperationsDashboard, defaults.QueueCounts, defaults.OrderList,
                defaults.Quote, defaults.AssignableDrivers, defaults.SignIn,
            }.Select(setting => setting.PermitLimit));
    }

    [Fact]
    public async Task The_summary_logs_one_line_per_rejecting_policy_with_registry_names_only()
    {
        var logs = new StructuredLogs();
        await using var host = new LimitsFactory(permitLimit: 1, logs: logs);
        using var client = host.CreateClient();

        foreach (var (method, path, profile) in new[]
                 {
                     ("GET", "/api/v1/orders", MockIdentityProfiles.ActiveDispatcher),
                     ("GET", "/api/v1/operations/dashboard", MockIdentityProfiles.ActiveDispatcher),
                 })
        {
            using var first = await client.SendAsync(Request(method, path, profile, NetworkA));
            using var rejected = await client.SendAsync(Request(method, path, profile, NetworkA));
            using var again = await client.SendAsync(Request(method, path, profile, NetworkB));
            Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
            Assert.Equal(HttpStatusCode.TooManyRequests, again.StatusCode);
        }

        var reporter = host.Services.GetServices<IHostedService>().OfType<RateLimitSummaryReporter>().Single();
        reporter.Report(60);

        var lines = logs.Entries.Where(entry => entry.EventId == TelemetryEvents.RateLimitSummary.Id).ToArray();
        // In name order, as the summary reports them.
        Assert.Equal(
            [(OrderEndpoints.ListRateLimitPolicy, 2L), (OperationsDashboardEndpoints.RateLimitPolicy, 2L)],
            lines.Select(line => ((string)line.Values["Policy"]!, (long)line.Values["Rejected"]!)));
        var registry = host.Services.GetRequiredService<RateLimitPolicyRegistry>();
        Assert.All(lines, line =>
        {
            Assert.Equal("Paqueteria.Infrastructure.Observability.RateLimitSummaryReporter", line.Category);
            Assert.Equal(LogLevel.Information, line.Level);
            Assert.Equal(
                ["Policy", "Rejected", "WindowSeconds"],
                line.Values.Keys.Where(key => key != "{OriginalFormat}").Order(StringComparer.Ordinal));
            Assert.All(line.Values.Keys.Where(key => key != "{OriginalFormat}"), key => Assert.Contains(key, TelemetryDimensions.Allowed));
            Assert.NotNull(registry.Find((string)line.Values["Policy"]!));
        });

        // No line of the host carries a subject, its hash, an address or a network key.
        var secrets = new List<string>
        {
            "mock-subject-active-dispatcher",
            SubjectPartition.Key("mock-subject-active-dispatcher"),
        };
        foreach (var address in new[] { NetworkA, NetworkB })
        {
            secrets.Add(address);
            secrets.Add(ClientNetworkPartition.Describe(IPAddress.Parse(address)));
            secrets.Add(ClientNetworkPartition.Key(IPAddress.Parse(address)));
        }

        Assert.NotEmpty(logs.Entries);
        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(logs.Entries, entry => entry.Text.Contains(secret, StringComparison.OrdinalIgnoreCase));
        }

        // The window resets: a minute without rejections writes nothing.
        var before = logs.Entries.Count(entry => entry.EventId == TelemetryEvents.RateLimitSummary.Id);
        reporter.Report(60);
        Assert.Equal(before, logs.Entries.Count(entry => entry.EventId == TelemetryEvents.RateLimitSummary.Id));
    }

    private const string Navigation = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8";

    private static HttpRequestMessage SignIn(string peer, string accept)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, AuthCenterDefaults.LoginPath + "?return_url=%2Fops%2Fdashboard");
        request.Headers.TryAddWithoutValidation("Accept", accept);
        request.Headers.Add(PeerAddress.Header, peer);
        return request;
    }

    private static HttpRequestMessage Request(string method, string path, string profile, string peer)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", profile);
        request.Headers.Add("X-Organization-Id", MockIdentityProfiles.ViewerOrganizationId.ToString("D"));
        request.Headers.Add(PeerAddress.Header, peer);
        if (path.StartsWith("/api/v1/orders/csv/", StringComparison.Ordinal))
        {
            // The CSV operations accept multipart/form-data only (routing answers anything else 415 before the limiter).
            request.Content = new MultipartFormDataContent
            {
                { new StringContent("synthetic"), "content_digest" },
            };
        }
        else if (method == "POST")
        {
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        }

        return request;
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, string retryAfter)
    {
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.ToString());
        Assert.Equal(retryAfter, Assert.Single(response.Headers.GetValues("Retry-After")));
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = problem.RootElement;
        Assert.Equal(
            ["status", "title", "traceId", "type"],
            root.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal("about:blank", root.GetProperty("type").GetString());
        Assert.Equal("Too Many Requests", root.GetProperty("title").GetString());
        Assert.Equal(429, root.GetProperty("status").GetInt32());
        Assert.True(TraceCorrelation.IsTraceId(root.GetProperty("traceId").GetString()));
    }

    private static string Flatten(Exception exception) =>
        exception is AggregateException aggregate
            ? string.Join(" | ", aggregate.InnerExceptions.Select(Flatten))
            : exception.Message + (exception.InnerException is { } inner ? " | " + Flatten(inner) : string.Empty);

    /// <summary>
    /// The assembled API with Mock identities, every new limit at <paramref name="permitLimit"/> per 60 s (the CSV one
    /// at <paramref name="csvLimit"/>), a synthetic transport peer per request and a tenant activation audit the test
    /// can hold.
    /// </summary>
    private sealed class LimitsFactory(
        int permitLimit,
        int? csvLimit = null,
        IReadOnlyDictionary<string, string?>? overrides = null,
        StructuredLogs? logs = null) : WebApplicationFactory<Program>
    {
        internal HeldAudit Audit { get; } = new();

        internal RateLimitRejectionSummary Summary => Services.GetRequiredService<RateLimitRejectionSummary>();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration(configuration =>
            {
                var values = new Dictionary<string, string?>
                {
                    ["Authentication:Provider"] = "Mock",
                    ["IdentityBootstrap:Provider"] = "Mock",
                    ["ConnectionStrings:Paqueteria"] =
                        "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused;Timeout=1",
                };
                foreach (var setting in new[]
                         {
                             "CsvImport", "SettlementExport", "LocationCreate", "ProofUploadSession", "ProofFinalize",
                             "OperationsDashboard", "QueueCounts", "OrderList", "Quote", "AssignableDrivers", "SignIn",
                         })
                {
                    var limit = setting == "CsvImport" && csvLimit is { } csv ? csv : permitLimit;
                    values[$"RateLimits:{setting}:PermitLimit"] = limit.ToString(CultureInfo.InvariantCulture);
                    values[$"RateLimits:{setting}:WindowSeconds"] = "60";
                }

                foreach (var (key, value) in overrides ?? new Dictionary<string, string?>())
                {
                    values[key] = value;
                }

                configuration.AddInMemoryCollection(values);
            });
            if (logs is not null)
            {
                builder.ConfigureLogging(logging => logging
                    .AddProvider(logs)
                    .SetMinimumLevel(LogLevel.Trace)
                    .AddFilter<StructuredLogs>(null, LogLevel.Trace));
            }

            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IStartupFilter, PeerAddress>();
                services.AddScoped<IPlatformAdminTenantActivationAudit>(_ => Audit);
            });
        }
    }

    /// <summary>The API with its own appsettings.json and nothing overridden.</summary>
    private sealed class DeployedConfigurationFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("Testing");
    }

    /// <summary>Holds the first tenant activation it records until released; records every later one at once.</summary>
    internal sealed class HeldAudit : IPlatformAdminTenantActivationAudit
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _hold = 1;

        internal Task Entered => _entered.Task;

        internal void Release() => _released.TrySetResult();

        public async Task RecordAsync(Guid actorUserId, Guid organizationId, string? requestId, CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _hold, 0) == 1)
            {
                _entered.TrySetResult();
                await _released.Task.WaitAsync(cancellationToken);
            }
        }
    }

    /// <summary>TestServer has no socket peer: this gives each request the transport address it names.</summary>
    private sealed class PeerAddress : IStartupFilter
    {
        internal const string Header = "X-Test-Transport-Peer";

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            app =>
            {
                app.Use(async (context, nextMiddleware) =>
                {
                    if (context.Request.Headers.TryGetValue(Header, out var peer))
                    {
                        context.Connection.RemoteIpAddress = IPAddress.Parse(peer.ToString());
                        context.Request.Headers.Remove(Header);
                    }

                    await nextMiddleware(context);
                });
                next(app);
            };
    }

    internal sealed record StructuredEntry(
        string Category,
        LogLevel Level,
        int EventId,
        IReadOnlyDictionary<string, object?> Values,
        string Text);

    /// <summary>Every log line of a host, with its structured values and its full text (scopes included).</summary>
    private sealed class StructuredLogs : ILoggerProvider
    {
        private readonly ConcurrentQueue<StructuredEntry> _entries = new();

        internal IReadOnlyCollection<StructuredEntry> Entries => _entries.ToArray();

        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _entries);

        public void Dispose()
        {
        }

        private sealed class Logger(string category, ConcurrentQueue<StructuredEntry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
            {
                entries.Enqueue(new StructuredEntry(category, LogLevel.None, 0, Values(state), $"{category} scope {state} {Describe(state)}"));
                return null;
            }

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                entries.Enqueue(new StructuredEntry(
                    category,
                    logLevel,
                    eventId.Id,
                    Values(state),
                    $"{category} {logLevel} {eventId} {formatter(state, exception)} {Describe(state)} {exception}"));

            private static Dictionary<string, object?> Values<TState>(TState state) =>
                state is IEnumerable<KeyValuePair<string, object?>> pairs
                    ? pairs.GroupBy(pair => pair.Key, StringComparer.Ordinal)
                        .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.Ordinal)
                    : new Dictionary<string, object?>(StringComparer.Ordinal);

            private static string Describe<TState>(TState state) =>
                state is IEnumerable<KeyValuePair<string, object?>> pairs
                    ? string.Join(" ", pairs.Select(pair => $"{pair.Key}={pair.Value}"))
                    : string.Empty;
        }
    }
}
