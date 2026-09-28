using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Identity.Infrastructure.Mock;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Realtime.Application.Events;
using Realtime.Application.Publishing;

namespace Paqueteria.IntegrationTests.Realtime;

public sealed class RealtimeHubIntegrationTests(
    RealtimeWebApplicationFactory factory)
    : IClassFixture<RealtimeWebApplicationFactory>
{
    private const string OperationsHub = "operations";
    private const string DriverHub = "driver";
    private const string TrackingHub = "tracking";

    private static readonly DateTimeOffset OccurredAt =
        new(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);

    // Budget for the server to finish OnConnectedAsync (authorization + group joins);
    // separate from, and not a replacement for, the 3 s delivery timeouts below.
    private static readonly TimeSpan ReadinessTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Operations_rejects_missing_invalid_suspended_selector_and_unauthorized_roles()
    {
        await AssertConnectionRejectedAsync(
            CreateConnection("/hubs/operations", null, RealtimeWebApplicationFactory.OrganizationA));
        await AssertConnectionRejectedAsync(
            CreateConnection("/hubs/operations", "not-valid", RealtimeWebApplicationFactory.OrganizationA));
        await AssertConnectionRejectedAsync(
            CreateConnection(
                "/hubs/operations",
                MockIdentityProfiles.SuspendedUser,
                RealtimeWebApplicationFactory.OrganizationA));
        await AssertConnectionRejectedAsync(
            CreateConnection(
                "/hubs/operations",
                MockIdentityProfiles.ActiveViewer,
                RealtimeWebApplicationFactory.OrganizationA));
        await AssertConnectionRejectedAsync(
            CreateConnection(
                "/hubs/operations",
                MockIdentityProfiles.ActiveDispatcher,
                organizationId: null));
        await AssertConnectionRejectedAsync(
            CreateConnection(
                "/hubs/operations",
                MockIdentityProfiles.ActiveDispatcher,
                RealtimeWebApplicationFactory.OrganizationB));
        await AssertConnectionRejectedAsync(
            CreateConnection(
                "/hubs/operations",
                MockIdentityProfiles.ActivePlatformAdminNoMfa,
                RealtimeWebApplicationFactory.OrganizationA));
    }

    [Fact]
    public async Task Dispatcher_and_platform_admin_with_MFA_connect()
    {
        await using var dispatcher = CreateConnection(
            "/hubs/operations",
            MockIdentityProfiles.ActiveDispatcher,
            RealtimeWebApplicationFactory.OrganizationA);
        var dispatcherAccepted = ExpectAccepted((OperationsHub, 1));
        await dispatcher.StartAsync();
        Assert.Equal(HubConnectionState.Connected, dispatcher.State);
        await dispatcherAccepted.WaitAsync(ReadinessTimeout);

        var state = factory.Services.GetRequiredService<
            RealtimeWebApplicationFactory.SyntheticRealtimeAuthorizationState>();
        var auditCount = state.PlatformAdminActivations;
        await using var admin = CreateConnection(
            "/hubs/operations",
            MockIdentityProfiles.ActivePlatformAdminMfa,
            RealtimeWebApplicationFactory.OrganizationA);
        var adminAccepted = ExpectAccepted((OperationsHub, 1));
        await admin.StartAsync();
        Assert.Equal(HubConnectionState.Connected, admin.State);
        await adminAccepted.WaitAsync(ReadinessTimeout);
        Assert.Equal(auditCount + 1, state.PlatformAdminActivations);
    }

    [Fact]
    public async Task Operations_groups_isolate_two_organizations()
    {
        await using var organizationA = CreateConnection(
            "/hubs/operations",
            MockIdentityProfiles.ActiveDispatcher,
            RealtimeWebApplicationFactory.OrganizationA);
        await using var organizationB = CreateConnection(
            "/hubs/operations",
            MockIdentityProfiles.ActiveMultiOrganization,
            RealtimeWebApplicationFactory.OrganizationB);
        var receivedA = NewCompletion<RealtimeEnvelope<OrderStatusChangedPayload>>();
        var receivedB = NewCompletion<RealtimeEnvelope<OrderStatusChangedPayload>>();
        organizationA.On("OrderStatusChanged", (RealtimeEnvelope<OrderStatusChangedPayload> value) =>
            receivedA.TrySetResult(value));
        organizationB.On("OrderStatusChanged", (RealtimeEnvelope<OrderStatusChangedPayload> value) =>
            receivedB.TrySetResult(value));
        var accepted = ExpectAccepted((OperationsHub, 2));
        await organizationA.StartAsync();
        await organizationB.StartAsync();
        await accepted.WaitAsync(ReadinessTimeout);

        var message = OrderStatusMessage();
        await Publisher().PublishOperationsOrderStatusChangedAsync(
            OperationsAudience.ForOrganization(RealtimeWebApplicationFactory.OrganizationA),
            message,
            default);

        Assert.Equal(message.EventId, (await receivedA.Task.WaitAsync(TimeSpan.FromSeconds(3))).EventId);
        await AssertNotCompletedAsync(receivedB.Task);
    }

    [Fact]
    public async Task Driver_joins_only_server_resolved_driver_and_assignment_groups()
    {
        await using var driver = CreateConnection(
            "/hubs/driver",
            MockIdentityProfiles.ActiveDriver,
            RealtimeWebApplicationFactory.OrganizationA);
        var received = NewCompletion<RealtimeEnvelope<AssignmentChangedPayload>>();
        driver.On("AssignmentChanged", (RealtimeEnvelope<AssignmentChangedPayload> value) =>
            received.TrySetResult(value));
        var accepted = ExpectAccepted((DriverHub, 1));
        await driver.StartAsync();
        await accepted.WaitAsync(ReadinessTimeout);

        var message = AssignmentMessage();
        await Publisher().PublishDriverAssignmentChangedAsync(
            DriverAudience.ForDriver(Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff")),
            message,
            default);
        await AssertNotCompletedAsync(received.Task);
        await Publisher().PublishDriverAssignmentChangedAsync(
            DriverAudience.ForAssignment(RealtimeWebApplicationFactory.AssignmentA),
            message,
            default);
        Assert.Equal(message.EventId, (await received.Task.WaitAsync(TimeSpan.FromSeconds(3))).EventId);

        await Assert.ThrowsAsync<HubException>(() => driver.InvokeAsync("JoinGroup", "driver:any"));
    }

    [Fact]
    public async Task External_offer_change_reaches_operations_and_only_the_target_driver_group()
    {
        await using var operations = CreateConnection(
            "/hubs/operations",
            MockIdentityProfiles.ActiveDispatcher,
            RealtimeWebApplicationFactory.OrganizationA);
        await using var driver = CreateConnection(
            "/hubs/driver",
            MockIdentityProfiles.ActiveDriver,
            RealtimeWebApplicationFactory.OrganizationA);
        var operationsReceived = NewCompletion<RealtimeEnvelope<ExternalOfferChangedPayload>>();
        var driverReceived = NewCompletion<RealtimeEnvelope<ExternalOfferChangedPayload>>();
        operations.On("ExternalOfferChanged", (RealtimeEnvelope<ExternalOfferChangedPayload> value) =>
            operationsReceived.TrySetResult(value));
        driver.On("ExternalOfferChanged", (RealtimeEnvelope<ExternalOfferChangedPayload> value) =>
            driverReceived.TrySetResult(value));
        var accepted = ExpectAccepted((OperationsHub, 1), (DriverHub, 1));
        await operations.StartAsync();
        await driver.StartAsync();
        await accepted.WaitAsync(ReadinessTimeout);

        var message = ExternalOfferMessage();
        await Publisher().PublishOperationsExternalOfferChangedAsync(
            OperationsAudience.ForOrganization(RealtimeWebApplicationFactory.OrganizationA),
            message,
            default);
        await Publisher().PublishDriverExternalOfferChangedAsync(
            DriverAudience.ForDriver(RealtimeWebApplicationFactory.DriverA),
            message,
            default);

        Assert.Equal(message.EventId, (await operationsReceived.Task.WaitAsync(TimeSpan.FromSeconds(3))).EventId);
        Assert.Equal(message.EventId, (await driverReceived.Task.WaitAsync(TimeSpan.FromSeconds(3))).EventId);
    }

    [Fact]
    public async Task Route_change_reaches_operations_and_the_server_selected_driver_group()
    {
        await using var operations = CreateConnection(
            "/hubs/operations",
            MockIdentityProfiles.ActiveDispatcher,
            RealtimeWebApplicationFactory.OrganizationA);
        await using var driver = CreateConnection(
            "/hubs/driver",
            MockIdentityProfiles.ActiveDriver,
            RealtimeWebApplicationFactory.OrganizationA);
        var operationsReceived = NewCompletion<RealtimeEnvelope<RouteChangedPayload>>();
        var driverReceived = NewCompletion<RealtimeEnvelope<RouteChangedPayload>>();
        operations.On("RouteChanged", (RealtimeEnvelope<RouteChangedPayload> value) =>
            operationsReceived.TrySetResult(value));
        driver.On("RouteChanged", (RealtimeEnvelope<RouteChangedPayload> value) =>
            driverReceived.TrySetResult(value));
        var accepted = ExpectAccepted((OperationsHub, 1), (DriverHub, 1));
        await operations.StartAsync();
        await driver.StartAsync();
        await accepted.WaitAsync(ReadinessTimeout);

        var message = RouteMessage();
        await Publisher().PublishOperationsRouteChangedAsync(
            OperationsAudience.ForOrganization(RealtimeWebApplicationFactory.OrganizationA),
            message,
            default);
        await Publisher().PublishDriverRouteChangedAsync(
            DriverAudience.ForDriver(RealtimeWebApplicationFactory.DriverA),
            message,
            default);

        Assert.Equal(message.EventId, (await operationsReceived.Task.WaitAsync(TimeSpan.FromSeconds(3))).EventId);
        Assert.Equal(message.EventId, (await driverReceived.Task.WaitAsync(TimeSpan.FromSeconds(3))).EventId);
        Assert.Equal(message.Payload.ChangedStopIds, (await operationsReceived.Task).Payload.ChangedStopIds);
    }

    [Fact]
    public async Task Driver_rejects_missing_invalid_non_driver_suspended_and_cross_tenant_connections()
    {
        await AssertConnectionRejectedAsync(
            CreateConnection("/hubs/driver", null, RealtimeWebApplicationFactory.OrganizationA));
        await AssertConnectionRejectedAsync(
            CreateConnection("/hubs/driver", "not-valid", RealtimeWebApplicationFactory.OrganizationA));
        await AssertConnectionRejectedAsync(
            CreateConnection(
                "/hubs/driver",
                MockIdentityProfiles.ActiveViewer,
                RealtimeWebApplicationFactory.OrganizationA));
        await AssertConnectionRejectedAsync(
            CreateConnection(
                "/hubs/driver",
                MockIdentityProfiles.ActiveDispatcher,
                RealtimeWebApplicationFactory.OrganizationA));
        await AssertConnectionRejectedAsync(
            CreateConnection(
                "/hubs/driver",
                MockIdentityProfiles.SuspendedUser,
                RealtimeWebApplicationFactory.OrganizationA));
        await AssertConnectionRejectedAsync(
            CreateConnection(
                "/hubs/driver",
                MockIdentityProfiles.SuspendedMembership,
                RealtimeWebApplicationFactory.OrganizationA));
        await AssertConnectionRejectedAsync(
            CreateConnection(
                "/hubs/driver",
                MockIdentityProfiles.ActiveDriver,
                RealtimeWebApplicationFactory.OrganizationB));
    }

    [Fact]
    public async Task Driver_groups_isolate_two_current_drivers()
    {
        await using var driverA = CreateConnection(
            "/hubs/driver",
            MockIdentityProfiles.ActiveDriver,
            RealtimeWebApplicationFactory.OrganizationA);
        await using var driverB = CreateConnection(
            "/hubs/driver",
            MockIdentityProfiles.ActiveMultiOrganization,
            RealtimeWebApplicationFactory.OrganizationB);
        var receivedA = NewCompletion<RealtimeEnvelope<AssignmentChangedPayload>>();
        var receivedB = NewCompletion<RealtimeEnvelope<AssignmentChangedPayload>>();
        driverA.On("AssignmentChanged", (RealtimeEnvelope<AssignmentChangedPayload> value) =>
            receivedA.TrySetResult(value));
        driverB.On("AssignmentChanged", (RealtimeEnvelope<AssignmentChangedPayload> value) =>
            receivedB.TrySetResult(value));
        var accepted = ExpectAccepted((DriverHub, 2));
        await driverA.StartAsync();
        await driverB.StartAsync();
        await accepted.WaitAsync(ReadinessTimeout);

        var message = AssignmentMessage();
        await Publisher().PublishDriverAssignmentChangedAsync(
            DriverAudience.ForDriver(RealtimeWebApplicationFactory.DriverA),
            message,
            default);

        Assert.Equal(message.EventId, (await receivedA.Task.WaitAsync(TimeSpan.FromSeconds(3))).EventId);
        await AssertNotCompletedAsync(receivedB.Task);
    }

    [Fact]
    public async Task Tracking_groups_isolate_tokens_and_public_payload_has_no_coordinates()
    {
        await using var trackingA = CreateConnection(
            "/hubs/tracking",
            RealtimeWebApplicationFactory.ValidTrackingTokenA,
            organizationId: null);
        await using var trackingB = CreateConnection(
            "/hubs/tracking",
            RealtimeWebApplicationFactory.ValidTrackingTokenB,
            organizationId: null);
        var receivedA = NewCompletion<PublicRealtimeEnvelope<PublicOrderStatusChangedPayload>>();
        var receivedB = NewCompletion<PublicRealtimeEnvelope<PublicOrderStatusChangedPayload>>();
        trackingA.On(
            "PublicOrderStatusChanged",
            (PublicRealtimeEnvelope<PublicOrderStatusChangedPayload> value) =>
                receivedA.TrySetResult(value));
        trackingB.On(
            "PublicOrderStatusChanged",
            (PublicRealtimeEnvelope<PublicOrderStatusChangedPayload> value) =>
                receivedB.TrySetResult(value));
        var accepted = ExpectAccepted((TrackingHub, 2));
        await trackingA.StartAsync();
        await trackingB.StartAsync();
        await accepted.WaitAsync(ReadinessTimeout);

        var message = PublicStatusMessage();
        await Publisher().PublishTrackingPublicOrderStatusChangedAsync(
            TrackingAudience.ForPublicOrder(RealtimeWebApplicationFactory.PublicOrderIdA),
            message,
            default);

        var received = await receivedA.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(message.EventId, received.EventId);
        using var serialized = JsonDocument.Parse(JsonSerializer.Serialize(received));
        var propertyNames = EnumeratePropertyNames(serialized.RootElement).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("lat", propertyNames);
        Assert.DoesNotContain("lng", propertyNames);
        Assert.DoesNotContain("driver_id", propertyNames);
        await AssertNotCompletedAsync(receivedB.Task);
    }

    [Fact]
    public async Task Tracking_failures_have_one_uniform_404_problem_details()
    {
        using var client = factory.CreateClient();
        var tokens = new string?[]
        {
            null,
            "missing",
            "expired",
            "revoked",
            "mutated",
            "unmapped",
            MockIdentityProfiles.ActiveDispatcher,
        };
        string? expectedBody = null;
        foreach (var token in tokens)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                token is null
                    ? "/hubs/tracking/negotiate?negotiateVersion=1"
                    : $"/hubs/tracking/negotiate?negotiateVersion=1&access_token={Uri.EscapeDataString(token)}");
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            var body = await response.Content.ReadAsStringAsync();
            expectedBody ??= body;
            Assert.Equal(expectedBody, body);
            using var problem = JsonDocument.Parse(body);
            Assert.Equal(["status", "title", "type"], problem.RootElement.EnumerateObject()
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal));
        }
    }

    [Fact]
    public async Task CORS_allows_only_the_configured_origin()
    {
        using var client = factory.CreateClient();
        using var allowed = await NegotiateTrackingAsync(client, RealtimeWebApplicationFactory.AllowedOrigin);
        Assert.Equal(
            RealtimeWebApplicationFactory.AllowedOrigin,
            allowed.Headers.GetValues("Access-Control-Allow-Origin").Single());

        using var denied = await NegotiateTrackingAsync(client, "https://untrusted.invalid");
        Assert.False(denied.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Connection_rate_limit_rejects_abusive_negotiate_attempts_without_token_partitioning()
    {
        using var limitedFactory = new RealtimeWebApplicationFactory(connectionPermitLimit: 2);
        using var client = limitedFactory.CreateClient();
        using var first = await NegotiateTrackingAsync(client, origin: null);
        using var second = await NegotiateTrackingAsync(client, origin: null);
        using var third = await NegotiateTrackingAsync(client, origin: null);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
    }

    [Fact]
    public async Task Disabled_provider_rejects_hubs_and_publisher_fails_closed()
    {
        using var disabledFactory = new RealtimeWebApplicationFactory(
            100,
            "Disabled",
            "InProcess",
            RealtimeWebApplicationFactory.AllowedOrigin);
        using var client = disabledFactory.CreateClient();
        using var response = await client.PostAsync(
            $"/hubs/tracking/negotiate?negotiateVersion=1&access_token={RealtimeWebApplicationFactory.ValidTrackingTokenA}",
            content: null);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        var publisher = disabledFactory.Services.GetRequiredService<IRealtimePublisher>();
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
            publisher.PublishTrackingPublicOrderStatusChangedAsync(
                TrackingAudience.ForPublicOrder(RealtimeWebApplicationFactory.PublicOrderIdA),
                PublicStatusMessage(),
                default));
    }

    [Theory]
    [InlineData("SignalR", "Redis", "https://web.synthetic.local")]
    [InlineData("SignalR", "InProcess", "*")]
    [InlineData("SignalR", "InProcess", "javascript:unsafe")]
    public void Unsupported_backplane_or_origin_fails_during_startup(
        string provider,
        string backplane,
        string origin)
    {
        using var invalidFactory = new RealtimeWebApplicationFactory(
            100,
            provider,
            backplane,
            origin);
        Assert.ThrowsAny<Exception>(() => invalidFactory.CreateClient());
    }

    private HubConnection CreateConnection(string path, string? token, Guid? organizationId)
    {
        var query = organizationId is { } value
            ? $"?organization_id={value:D}"
            : string.Empty;
        return new HubConnectionBuilder()
            .WithUrl(
                new Uri(factory.Server.BaseAddress, path + query),
                options =>
                {
                    options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                    options.Transports = HttpTransportType.LongPolling;
                    if (token is not null)
                    {
                        options.AccessTokenProvider = () => Task.FromResult<string?>(token);
                    }
                })
            .AddJsonProtocol(options =>
            {
                options.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
                options.PayloadSerializerOptions.DefaultIgnoreCondition =
                    JsonIgnoreCondition.WhenWritingNull;
            })
            .Build();
    }

    /// <summary>
    /// Registers, before the clients start, a wait for the hubs' ConnectionAccepted signal,
    /// which each hub emits only after its Groups.AddToGroupAsync calls completed. The
    /// client's StartAsync alone completes at the handshake, before OnConnectedAsync runs.
    /// </summary>
    private Task ExpectAccepted(params (string Hub, int Count)[] expectations) =>
        factory.ConnectionAcceptances.ExpectAsync(expectations);

    private IRealtimePublisher Publisher() =>
        factory.Services.GetRequiredService<IRealtimePublisher>();

    private static async Task AssertConnectionRejectedAsync(HubConnection connection)
    {
        await using (connection)
        {
            var closed = NewCompletion<Exception?>();
            connection.Closed += exception =>
            {
                closed.TrySetResult(exception);
                return Task.CompletedTask;
            };

            try
            {
                await connection.StartAsync();
                await closed.Task.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception)
            {
                // A rejected connection may fail during negotiate/handshake or close immediately
                // after the hub's current-authorization check.
            }

            Assert.NotEqual(HubConnectionState.Connected, connection.State);
        }
    }

    private static IEnumerable<string> EnumeratePropertyNames(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                yield return property.Name;
                foreach (var nested in EnumeratePropertyNames(property.Value))
                {
                    yield return nested;
                }
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                foreach (var nested in EnumeratePropertyNames(item))
                {
                    yield return nested;
                }
            }
        }
    }

    private static async Task AssertNotCompletedAsync(Task task)
    {
        var marker = Task.Delay(250);
        Assert.Same(marker, await Task.WhenAny(task, marker));
    }

    private static TaskCompletionSource<T> NewCompletion<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static RealtimeEnvelope<OrderStatusChangedPayload> OrderStatusMessage() =>
        new(
            Guid.NewGuid(),
            RealtimeEventTypes.OrderStatusChanged,
            OccurredAt,
            Guid.Parse("77777777-7777-7777-7777-777777777777"),
            2,
            null,
            new OrderStatusChangedPayload(
                Guid.Parse("77777777-7777-7777-7777-777777777777"),
                "READY_FOR_PICKUP",
                "ASSIGNED",
                OccurredAt));

    private static RealtimeEnvelope<AssignmentChangedPayload> AssignmentMessage() =>
        new(
            Guid.NewGuid(),
            RealtimeEventTypes.AssignmentChanged,
            OccurredAt,
            Guid.Parse("77777777-7777-7777-7777-777777777777"),
            2,
            null,
            new AssignmentChangedPayload(
                Guid.Parse("77777777-7777-7777-7777-777777777777"),
                RealtimeWebApplicationFactory.AssignmentA,
                RealtimeWebApplicationFactory.DriverA,
                "ACCEPTED",
                OccurredAt));

    private static RealtimeEnvelope<ExternalOfferChangedPayload> ExternalOfferMessage() =>
        new(
            Guid.NewGuid(),
            RealtimeEventTypes.ExternalOfferChanged,
            OccurredAt,
            Guid.Parse("77777777-7777-7777-7777-777777777778"),
            1,
            null,
            new ExternalOfferChangedPayload(
                Guid.Parse("77777777-7777-7777-7777-777777777778"),
                "OPEN",
                12_345,
                OccurredAt.AddHours(2)));

    private static RealtimeEnvelope<RouteChangedPayload> RouteMessage()
    {
        var routeId = Guid.Parse("77777777-7777-7777-7777-777777777779");
        return new(
            Guid.NewGuid(),
            RealtimeEventTypes.RouteChanged,
            OccurredAt,
            routeId,
            4,
            null,
            new RouteChangedPayload(
                routeId,
                4,
                [Guid.Parse("88888888-8888-8888-8888-888888888881")],
                OccurredAt));
    }

    private static PublicRealtimeEnvelope<PublicOrderStatusChangedPayload> PublicStatusMessage() =>
        new(
            Guid.NewGuid(),
            RealtimeEventTypes.PublicOrderStatusChanged,
            OccurredAt,
            RealtimeWebApplicationFactory.PublicOrderIdA,
            2,
            null,
            new PublicOrderStatusChangedPayload(
                RealtimeWebApplicationFactory.PublicOrderIdA,
                "SCHEDULED",
                OccurredAt));

    private static async Task<HttpResponseMessage> NegotiateTrackingAsync(
        HttpClient client,
        string? origin)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/hubs/tracking/negotiate?negotiateVersion=1&access_token={RealtimeWebApplicationFactory.ValidTrackingTokenA}");
        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }

        return await client.SendAsync(request);
    }
}
