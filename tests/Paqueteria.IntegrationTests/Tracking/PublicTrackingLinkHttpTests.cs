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
/// TRK-002-ISSUE-ENDPOINT over HTTP with the token service replaced by a recording stub: request shape, then
/// capability, then the service; the uniform 404, 409 and 503 mappings; the no-store 201 that carries the only
/// plaintext copy; and no plaintext in any log line, scope or problem body.
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
        foreach (var path in new[] { IssuePath(orderId), RevokePath(orderId) })
        {
            using var response = await SendAsync(profile, path, Key());
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.DoesNotContain("MFA_REQUIRED", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

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
            using var revoke = await SendAsync(profile, RevokePath(orderId), key);
            Assert.Equal(HttpStatusCode.Conflict, issue.StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, revoke.StatusCode);
        }

        Assert.Equal(0, factory.Service.CallsFor(orderId));
    }

    [Theory]
    [InlineData(MockIdentityProfiles.ActiveDispatcher, "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaa10")]
    [InlineData(MockIdentityProfiles.ActivePlatformAdminNoMfa, "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa3")]
    public async Task Dispatcher_and_platform_admin_receive_the_token_once_with_no_store(string profile, string actor)
    {
        var orderId = Guid.NewGuid();
        var key = Key();
        using var response = await SendAsync(profile, IssuePath(orderId), key);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Contains("no-cache", response.Headers.Pragma.Select(value => value.Name));
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Null(response.Headers.Location);
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        Assert.Equal(
            ["expires_at", "order_id", "token", "token_id"],
            root.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        var grant = factory.Service.LastGrant(orderId);
        Assert.Equal(grant.Token, root.GetProperty("token").GetString());
        Assert.Equal(grant.TokenId, root.GetProperty("token_id").GetGuid());
        Assert.Equal(orderId, root.GetProperty("order_id").GetGuid());

        var command = factory.Service.LastIssue(orderId);
        Assert.Equal(Guid.Parse(actor), command.ActorId);
        Assert.Equal(MockIdentityProfiles.ViewerOrganizationId, command.OrganizationId);
        Assert.Equal(key, command.RequestId);
        Assert.Null(command.RequestedExpiration);
        Assert.Equal(0, factory.Service.RotationsFor(orderId));
    }

    [Fact]
    public async Task Issuing_when_a_link_is_active_rotates_and_returns_only_the_new_token()
    {
        var orderId = Guid.NewGuid();
        using var first = await SendAsync(MockIdentityProfiles.ActiveDispatcher, IssuePath(orderId), Key());
        var firstToken = ReadToken(await first.Content.ReadAsStringAsync());

        using var second = await SendAsync(MockIdentityProfiles.ActiveDispatcher, IssuePath(orderId), Key());

        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var secondToken = ReadToken(await second.Content.ReadAsStringAsync());
        Assert.NotEqual(firstToken, secondToken);
        Assert.Equal(1, factory.Service.RotationsFor(orderId));
        Assert.Equal(secondToken, factory.Service.LastGrant(orderId).Token);
    }

    [Fact]
    public async Task Revoke_is_204_without_a_body_and_passes_the_key_as_the_audit_request_id()
    {
        var orderId = Guid.NewGuid();
        var key = Key();
        using var response = await SendAsync(MockIdentityProfiles.ActiveDispatcher, RevokePath(orderId), key);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        var command = factory.Service.LastRevoke(orderId);
        Assert.Equal(key, command.RequestId);
        Assert.Equal(MockIdentityProfiles.ViewerOrganizationId, command.OrganizationId);
    }

    [Fact]
    public async Task Missing_or_foreign_orders_are_the_uniform_404_and_failures_are_503_without_the_token()
    {
        foreach (var path in new[] { IssuePath(Stub.NotFoundOrderId), RevokePath(Stub.NotFoundOrderId) })
        {
            using var response = await SendAsync(MockIdentityProfiles.ActiveDispatcher, path, Key());
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

        foreach (var path in new[] { IssuePath(Stub.UnavailableOrderId), RevokePath(Stub.UnavailableOrderId) })
        {
            using var response = await SendAsync(MockIdentityProfiles.ActiveDispatcher, path, Key());
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
        using var rotated = await SendAsync(MockIdentityProfiles.ActiveDispatcher, IssuePath(orderId), Key());
        using var revoked = await SendAsync(MockIdentityProfiles.ActiveDispatcher, RevokePath(orderId), Key());
        using var failed = await SendAsync(MockIdentityProfiles.ActiveDispatcher, IssuePath(Stub.UnavailableOrderId), Key());
        Assert.Equal(HttpStatusCode.Created, issued.StatusCode);
        Assert.Equal(HttpStatusCode.Created, rotated.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);

        var tokens = factory.Service.AllTokens();
        Assert.True(tokens.Count >= 3);
        Assert.NotEmpty(factory.Logs.Entries);
        Assert.Contains(factory.Logs.Entries, entry => entry.Contains("tracking-link", StringComparison.Ordinal));
        var failure = await failed.Content.ReadAsStringAsync();
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
        Guid? organizationId = null)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
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

    private static string RevokePath(Guid orderId) => $"/api/v1/orders/{orderId:D}/tracking-link/revoke";

    private static string Key() => $"trk002-http-{Guid.NewGuid():N}";

    public sealed class Factory : WebApplicationFactory<Program>
    {
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

        private readonly ConcurrentDictionary<Guid, PublicTrackingTokenGrant> active = new();
        private readonly ConcurrentDictionary<Guid, IssuePublicTrackingTokenCommand> issues = new();
        private readonly ConcurrentDictionary<Guid, RevokePublicTrackingTokenCommand> revokes = new();
        private readonly ConcurrentDictionary<Guid, int> calls = new();
        private readonly ConcurrentDictionary<Guid, int> rotations = new();
        private readonly ConcurrentDictionary<Guid, PublicTrackingTokenGrant> last = new();
        private readonly ConcurrentBag<string> tokens = [];

        internal int CallsFor(Guid orderId) => calls.GetValueOrDefault(orderId);

        internal int RotationsFor(Guid orderId) => rotations.GetValueOrDefault(orderId);

        internal PublicTrackingTokenGrant LastGrant(Guid orderId) => last[orderId];

        internal IssuePublicTrackingTokenCommand LastIssue(Guid orderId) => issues[orderId];

        internal RevokePublicTrackingTokenCommand LastRevoke(Guid orderId) => revokes[orderId];

        internal IReadOnlyCollection<string> AllTokens() => tokens.ToArray();

        public Task<PublicTrackingTokenGrant> IssueAsync(
            IssuePublicTrackingTokenCommand command,
            CancellationToken cancellationToken)
        {
            Touch(command.OrderId);
            issues[command.OrderId] = command;
            Fail(command.OrderId);
            if (command.OrderId == ConflictOrderId || active.ContainsKey(command.OrderId))
            {
                throw new PublicTrackingTokenConflictException("An active public tracking token already exists.");
            }

            return Task.FromResult(Grant(command.OrderId));
        }

        public Task<PublicTrackingTokenGrant> RotateAsync(
            RotatePublicTrackingTokenCommand command,
            CancellationToken cancellationToken)
        {
            Touch(command.OrderId);
            Fail(command.OrderId);
            if (command.OrderId == ConflictOrderId)
            {
                throw new PublicTrackingTokenConflictException("The requested expiration is outside the supported range.");
            }

            rotations.AddOrUpdate(command.OrderId, 1, (_, value) => value + 1);
            return Task.FromResult(Grant(command.OrderId));
        }

        public Task RevokeAsync(RevokePublicTrackingTokenCommand command, CancellationToken cancellationToken)
        {
            Touch(command.OrderId);
            revokes[command.OrderId] = command;
            Fail(command.OrderId);
            active.TryRemove(command.OrderId, out _);
            return Task.CompletedTask;
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
                throw new PublicTrackingTokenInfrastructureException("Public tracking token issuance failed safely.");
            }
        }

        private PublicTrackingTokenGrant Grant(Guid orderId)
        {
            var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
            var grant = new PublicTrackingTokenGrant(Guid.NewGuid(), orderId, token, DateTimeOffset.UtcNow.AddHours(168));
            tokens.Add(token);
            active[orderId] = grant;
            last[orderId] = grant;
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
