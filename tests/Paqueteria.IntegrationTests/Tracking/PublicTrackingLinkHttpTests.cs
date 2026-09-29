using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Identity.Infrastructure.Mock;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Orders.Application.Tracking;

namespace Paqueteria.IntegrationTests.Tracking;

/// <summary>
/// TRK-002-AUTO-LINK over HTTP with the token service replaced by a recording stub: request shape, then capability
/// (DISPATCHER without MFA, PLATFORM_ADMIN only with MFA), then the service; the uniform 404, the uncoded 409, the
/// coded 409 TRACKING_LINK_ORDER_FINISHED and the 503 mappings; the no-store 200 get-or-create that returns the same
/// link on every call with its public URL; no revocation route (TRK-002-NO-REVOCATION); and no plaintext in any log
/// line, scope or problem body.
/// </summary>
public sealed class PublicTrackingLinkHttpTests : IClassFixture<PublicTrackingLinkHttpTests.Factory>
{
    private readonly Factory factory;

    public PublicTrackingLinkHttpTests(Factory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task Issue_requires_authentication()
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, IssuePath(Guid.NewGuid()));
        request.Headers.Add("Idempotency-Key", Key());
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData(MockIdentityProfiles.ActiveViewer)]
    [InlineData(MockIdentityProfiles.ActiveDriver)]
    [InlineData(MockIdentityProfiles.ActiveFinance)]
    [InlineData(MockIdentityProfiles.ActiveBusinessAdmin)]
    public async Task Roles_outside_the_capability_get_the_generic_403_before_the_service(string profile)
    {
        var orderId = Guid.NewGuid();
        using var response = await SendAsync(profile, IssuePath(orderId), Key());
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain("MFA_REQUIRED", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        Assert.Equal(0, factory.Service.CallsFor(orderId));
    }

    /// <summary>
    /// A PLATFORM_ADMIN whose only unmet requirement is the second factor receives 403 MFA_REQUIRED before the token
    /// service is called (x-capability-matrix tracking_link_operations; owner confirmation "Sí, con MFA").
    /// </summary>
    [Fact]
    public async Task Platform_admin_without_MFA_gets_MFA_REQUIRED_before_the_service()
    {
        var orderId = Guid.NewGuid();
        using var response = await SendAsync(MockIdentityProfiles.ActivePlatformAdminNoMfa, IssuePath(orderId), Key());
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("MFA_REQUIRED", json.RootElement.GetProperty("code").GetString());

        Assert.Equal(0, factory.Service.CallsFor(orderId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("short")]
    public async Task A_missing_or_malformed_Idempotency_Key_is_409_before_any_capability_or_service_call(string? key)
    {
        var orderId = Guid.NewGuid();
        foreach (var profile in new[] { MockIdentityProfiles.ActiveDispatcher, MockIdentityProfiles.ActiveViewer })
        {
            using var issue = await SendAsync(profile, IssuePath(orderId), key);
            Assert.Equal(HttpStatusCode.Conflict, issue.StatusCode);
        }

        Assert.Equal(0, factory.Service.CallsFor(orderId));
    }

    [Theory]
    [InlineData(MockIdentityProfiles.ActiveDispatcher, "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaa10")]
    [InlineData(MockIdentityProfiles.ActivePlatformAdminMfa, "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa2")]
    public async Task Dispatcher_and_platform_admin_with_MFA_get_the_link_and_its_public_url_with_no_store(
        string profile,
        string actor)
    {
        var orderId = Guid.NewGuid();
        var key = Key();
        using var response = await SendAsync(profile, IssuePath(orderId), key);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Contains("no-cache", response.Headers.Pragma.Select(value => value.Name));
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Null(response.Headers.Location);
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        Assert.Equal(
            ["generation", "order_id", "token", "token_id", "url", "valid_until"],
            root.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        var grant = factory.Service.LastGrant(orderId);
        Assert.Equal(grant.Token, root.GetProperty("token").GetString());
        Assert.Equal($"{Factory.PublicBaseUrl}/track/{grant.Token}", root.GetProperty("url").GetString());
        Assert.Equal(grant.TokenId, root.GetProperty("token_id").GetGuid());
        Assert.Equal(orderId, root.GetProperty("order_id").GetGuid());
        Assert.Equal(1, root.GetProperty("generation").GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("valid_until").ValueKind);

        var command = factory.Service.LastGetOrCreate(orderId);
        Assert.Equal(Guid.Parse(actor), command.ActorId);
        Assert.Equal(MockIdentityProfiles.ViewerOrganizationId, command.OrganizationId);
        Assert.Equal(key, command.RequestId);
        Assert.Equal(1, factory.Service.CreationsFor(orderId));
    }

    [Fact]
    public async Task Get_or_create_returns_the_same_link_for_a_retry_or_another_key_and_never_rotates()
    {
        var orderId = Guid.NewGuid();
        var key = Key();
        using var first = await SendAsync(MockIdentityProfiles.ActiveDispatcher, IssuePath(orderId), key);
        using var retry = await SendAsync(MockIdentityProfiles.ActiveDispatcher, IssuePath(orderId), key);
        using var other = await SendAsync(MockIdentityProfiles.ActiveDispatcher, IssuePath(orderId), Key());

        var bodies = new[] { first, retry, other }.Select(response =>
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        }).ToArray();
        Assert.Single(bodies.Select(ReadToken).Distinct(StringComparer.Ordinal));
        Assert.Equal(1, factory.Service.CreationsFor(orderId));
        foreach (var body in bodies)
        {
            using var json = JsonDocument.Parse(body);
            Assert.Equal(1, json.RootElement.GetProperty("generation").GetInt32());
        }
    }

    [Fact]
    public async Task A_finished_order_is_the_coded_409_and_a_shape_conflict_is_uncoded()
    {
        using var finished = await SendAsync(
            MockIdentityProfiles.ActiveDispatcher,
            IssuePath(Stub.FinishedOrderId),
            Key());
        Assert.Equal(HttpStatusCode.Conflict, finished.StatusCode);
        Assert.Equal("application/problem+json", finished.Content.Headers.ContentType?.MediaType);
        using (var json = JsonDocument.Parse(await finished.Content.ReadAsStringAsync()))
        {
            Assert.Equal("TRACKING_LINK_ORDER_FINISHED", json.RootElement.GetProperty("code").GetString());
            Assert.False(json.RootElement.TryGetProperty("token", out _));
        }

        using var uncoded = await SendAsync(MockIdentityProfiles.ActiveDispatcher, IssuePath(Stub.ConflictOrderId), Key());
        Assert.Equal(HttpStatusCode.Conflict, uncoded.StatusCode);
        using var body = JsonDocument.Parse(await uncoded.Content.ReadAsStringAsync());
        Assert.False(body.RootElement.TryGetProperty("code", out _));
    }

    /// <summary>
    /// TRK-002-NO-REVOCATION: nobody revokes a link. The former revocation route is not mapped for any role (404,
    /// even for the members who may read the link), the link route accepts no other method (405), and the token
    /// service is never called.
    /// </summary>
    [Theory]
    [InlineData(MockIdentityProfiles.ActiveDispatcher)]
    [InlineData(MockIdentityProfiles.ActivePlatformAdminMfa)]
    [InlineData(MockIdentityProfiles.ActivePlatformAdminNoMfa)]
    [InlineData(MockIdentityProfiles.ActiveViewer)]
    public async Task The_revocation_route_does_not_exist_for_any_role(string profile)
    {
        var orderId = Guid.NewGuid();
        using var revoke = await SendAsync(profile, $"{IssuePath(orderId)}/revoke", Key());
        Assert.Equal(HttpStatusCode.NotFound, revoke.StatusCode);

        using var delete = await SendAsync(profile, IssuePath(orderId), Key(), method: HttpMethod.Delete);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, delete.StatusCode);

        Assert.Equal(0, factory.Service.CallsFor(orderId));
    }

    [Fact]
    public async Task Missing_or_foreign_orders_are_the_uniform_404_and_failures_are_503_without_the_token()
    {
        using (var response = await SendAsync(MockIdentityProfiles.ActiveDispatcher, IssuePath(Stub.NotFoundOrderId), Key()))
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        }

        // A dispatcher of another organization is refused with the same 404 as a missing order.
        using var foreign = await SendAsync(
            MockIdentityProfiles.ActiveMultiOrganization,
            IssuePath(Stub.NotFoundOrderId),
            Key(),
            MockIdentityProfiles.OperationsOrganizationId);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);

        using (var response = await SendAsync(MockIdentityProfiles.ActiveDispatcher, IssuePath(Stub.UnavailableOrderId), Key()))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }

        using var conflict = await SendAsync(MockIdentityProfiles.ActiveDispatcher, IssuePath(Stub.ConflictOrderId), Key());
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
    }

    [Fact]
    public async Task The_plaintext_token_never_reaches_a_log_line_scope_or_problem_body()
    {
        var orderId = Guid.NewGuid();
        using var issued = await SendAsync(MockIdentityProfiles.ActiveDispatcher, IssuePath(orderId), Key());
        using var repeated = await SendAsync(MockIdentityProfiles.ActiveDispatcher, IssuePath(orderId), Key());
        using var other = await SendAsync(MockIdentityProfiles.ActivePlatformAdminMfa, IssuePath(Guid.NewGuid()), Key());
        using var failed = await SendAsync(MockIdentityProfiles.ActiveDispatcher, IssuePath(Stub.UnavailableOrderId), Key());
        using var finished = await SendAsync(MockIdentityProfiles.ActiveDispatcher, IssuePath(Stub.FinishedOrderId), Key());
        Assert.Equal(HttpStatusCode.OK, issued.StatusCode);
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, finished.StatusCode);

        var tokens = factory.Service.AllTokens();
        Assert.True(tokens.Count >= 2);
        Assert.NotEmpty(factory.Logs.Entries);
        Assert.Contains(factory.Logs.Entries, entry => entry.Contains("tracking-link", StringComparison.Ordinal));
        var failure = await failed.Content.ReadAsStringAsync() + await finished.Content.ReadAsStringAsync();
        foreach (var token in tokens)
        {
            Assert.DoesNotContain(factory.Logs.Entries, entry => entry.Contains(token, StringComparison.Ordinal));
            Assert.DoesNotContain(token, failure, StringComparison.Ordinal);
        }
    }

    private static string ReadToken(string body)
    {
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("token").GetString()!;
    }

    private async Task<HttpResponseMessage> SendAsync(
        string profile,
        string path,
        string? idempotencyKey,
        Guid? organizationId = null,
        HttpMethod? method = null)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(method ?? HttpMethod.Post, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", profile);
        request.Headers.Add(
            "X-Organization-Id",
            (organizationId ?? MockIdentityProfiles.ViewerOrganizationId).ToString("D"));
        if (idempotencyKey is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        }

        return await client.SendAsync(request);
    }

    private static string IssuePath(Guid orderId) => $"/api/v1/orders/{orderId:D}/tracking-link";

    private static string Key() => $"trk002-http-{Guid.NewGuid():N}";

    public sealed class Factory : WebApplicationFactory<Program>
    {
        internal const string PublicBaseUrl = "https://tracking.synthetic.test";

        internal Stub Service { get; } = new();
        internal CapturingLoggerProvider Logs { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration(configuration =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Authentication:Provider"] = "Mock",
                    ["IdentityBootstrap:Provider"] = "Mock",
                    ["PublicTracking:PublicBaseUrl"] = PublicBaseUrl,
                    // Everything the host can log, framework categories included, is captured and searched.
                    ["Logging:LogLevel:Default"] = "Trace",
                    ["Logging:LogLevel:Microsoft"] = "Trace",
                    ["Logging:LogLevel:Microsoft.AspNetCore"] = "Trace",
                }));
            builder.ConfigureLogging(logging => logging.AddProvider(Logs).SetMinimumLevel(LogLevel.Trace));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IPublicTrackingTokenService>();
                services.AddSingleton<IPublicTrackingTokenService>(Service);
            });
        }
    }

    internal sealed class Stub : IPublicTrackingTokenService
    {
        internal static readonly Guid NotFoundOrderId = Guid.Parse("92000000-0000-0000-0000-000000000404");
        internal static readonly Guid UnavailableOrderId = Guid.Parse("92000000-0000-0000-0000-000000000503");
        internal static readonly Guid ConflictOrderId = Guid.Parse("92000000-0000-0000-0000-000000000409");
        internal static readonly Guid FinishedOrderId = Guid.Parse("92000000-0000-0000-0000-000000000410");

        private readonly ConcurrentDictionary<Guid, PublicTrackingTokenGrant> active = new();
        private readonly ConcurrentDictionary<Guid, int> generations = new();
        private readonly ConcurrentDictionary<Guid, GetOrCreatePublicTrackingLinkCommand> reads = new();
        private readonly ConcurrentDictionary<Guid, int> calls = new();
        private readonly ConcurrentDictionary<Guid, int> creations = new();
        private readonly ConcurrentDictionary<Guid, PublicTrackingTokenGrant> last = new();
        private readonly ConcurrentBag<string> tokens = [];
        private readonly object gate = new();

        internal int CallsFor(Guid orderId) => calls.GetValueOrDefault(orderId);

        internal int CreationsFor(Guid orderId) => creations.GetValueOrDefault(orderId);

        internal PublicTrackingTokenGrant LastGrant(Guid orderId) => last[orderId];

        internal GetOrCreatePublicTrackingLinkCommand LastGetOrCreate(Guid orderId) => reads[orderId];

        internal IReadOnlyCollection<string> AllTokens() => tokens.ToArray();

        public Task<PublicTrackingTokenGrant> GetOrCreateAsync(
            GetOrCreatePublicTrackingLinkCommand command,
            CancellationToken cancellationToken)
        {
            Touch(command.OrderId);
            reads[command.OrderId] = command;
            Fail(command.OrderId);
            if (command.OrderId == ConflictOrderId)
            {
                throw new PublicTrackingTokenConflictException("The public tracking token command is invalid.");
            }

            if (command.OrderId == FinishedOrderId)
            {
                throw new PublicTrackingLinkOrderFinishedException();
            }

            lock (gate)
            {
                if (!active.TryGetValue(command.OrderId, out var grant))
                {
                    grant = Grant(command.OrderId, generations.AddOrUpdate(command.OrderId, 1, (_, value) => value + 1));
                    creations.AddOrUpdate(command.OrderId, 1, (_, value) => value + 1);
                }

                last[command.OrderId] = grant;
                return Task.FromResult(grant);
            }
        }

        private void Touch(Guid orderId) => calls.AddOrUpdate(orderId, 1, (_, value) => value + 1);

        private static void Fail(Guid orderId)
        {
            if (orderId == NotFoundOrderId)
            {
                throw new PublicTrackingTokenNotFoundException();
            }

            if (orderId == UnavailableOrderId)
            {
                throw new PublicTrackingTokenInfrastructureException("Public tracking link retrieval failed safely.");
            }
        }

        private PublicTrackingTokenGrant Grant(Guid orderId, int generation)
        {
            var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
            var grant = new PublicTrackingTokenGrant(Guid.NewGuid(), orderId, token, generation, null);
            tokens.Add(token);
            active[orderId] = grant;
            return grant;
        }
    }

    internal sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> entries = new();

        internal IReadOnlyCollection<string> Entries => entries.ToArray();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, entries);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(string category, ConcurrentQueue<string> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
            {
                entries.Enqueue($"{category} scope {Render(state)}");
                return null;
            }

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                entries.Enqueue($"{category} {logLevel} {formatter(state, exception)} {Render(state)} {exception}");

            private static string Render<TState>(TState state) =>
                state is IEnumerable<KeyValuePair<string, object?>> pairs
                    ? string.Join(';', pairs.Select(pair => $"{pair.Key}={pair.Value}"))
                    : state?.ToString() ?? string.Empty;
        }
    }
}
