extern alias WorkerHost;

using System.Diagnostics;
using System.Net;
using Azure.Core.Pipeline;
using Azure.Storage.Blobs;
using Custody.Infrastructure.ProofStorage;
using Identity.Endpoints.AuthCenter;
using Locations.Infrastructure.Geocoding.GoogleMaps;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using Paqueteria.Infrastructure.Observability;
using Paqueteria.IntegrationTests.Security.AuthCenter;
using WorkerProgram = WorkerHost::WorkerProgram;

namespace Paqueteria.IntegrationTests.Observability;

/// <summary>
/// OBS-003: no outgoing request carries <c>traceparent</c>, <c>tracestate</c>, <c>baggage</c>, <c>Request-Id</c> or
/// <c>Correlation-Context</c>, even while a request or Worker activity is current. The real clients (the API's and
/// the Worker's named HTTP clients, the AuthCenter OIDC back-channel and an Azure SDK client over its HTTP transport)
/// send to a loopback server that records the headers on the wire.
/// </summary>
public sealed class Obs003OutboundTraceHeadersTests
{
    private const string ProbePath = "/__obs003/outbound";

    // The adapters are internal; the names are checked against the registered named clients (AssertRegistered).
    private const string WhatsAppClient = "Paqueteria.Messaging.MetaWhatsAppCloudApi";
    private const string AcsEmailClient = "Paqueteria.Messaging.AzureCommunicationEmail";
    private const string TwilioClient = "Paqueteria.Voice.Twilio";

    [Fact]
    public async Task The_runtime_default_propagator_would_inject_traceparent_so_the_capture_is_real()
    {
        await using var capture = await TraceHeaderCaptureServer.StartAsync();
        using var handler = new SocketsHttpHandler
        {
            ActivityHeadersPropagator = DistributedContextPropagator.CreateDefaultPropagator(),
        };
        using var client = new HttpClient(handler);

        using (TraceCorrelation.StartJobCycle("obs003.control"))
        {
            using var response = await client.GetAsync(capture.For("control"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Contains("traceparent", capture.Single("control").HeaderNames);
    }

    [Fact]
    public async Task Handlers_built_after_startup_send_no_trace_context()
    {
        TraceCorrelation.ConfigureProcess();
        await using var capture = await TraceHeaderCaptureServer.StartAsync();
        using var sockets = new HttpClient(new SocketsHttpHandler());
        using var classic = new HttpClient(new HttpClientHandler());

        using (TraceCorrelation.StartJobCycle("obs003.handlers"))
        {
            (await sockets.GetAsync(capture.For("sockets"))).Dispose();
            (await classic.GetAsync(capture.For("classic"))).Dispose();
        }

        TraceHeaderCaptureServer.AssertNoTraceContext(capture.Single("sockets"));
        TraceHeaderCaptureServer.AssertNoTraceContext(capture.Single("classic"));
    }

    [Fact]
    public async Task Api_clients_send_no_trace_context_from_inside_a_request_that_brought_one()
    {
        await using var capture = await TraceHeaderCaptureServer.StartAsync();
        await using var api = new OutboundProbeApiFactory(capture);
        using var client = api.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, ProbePath);
        Obs003.AddInboundTraceContext(request);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var requestTraceId = await response.Content.ReadAsStringAsync();
        Assert.True(TraceCorrelation.IsTraceId(requestTraceId));
        Assert.NotEqual(Obs003.InboundTraceId, requestTraceId);
        foreach (var path in new[] { "google-maps", "twilio", "authcenter" })
        {
            TraceHeaderCaptureServer.AssertNoTraceContext(capture.Single(path));
        }
    }

    [Fact]
    public async Task Worker_clients_send_no_trace_context_inside_a_cycle()
    {
        await using var capture = await TraceHeaderCaptureServer.StartAsync();
        await using var worker = new ProductionWorkerFactory();
        var clients = worker.Services.GetRequiredService<IHttpClientFactory>();
        AssertRegistered(worker.Services, WhatsAppClient);
        AssertRegistered(worker.Services, AcsEmailClient);

        using (TraceCorrelation.StartOutboxDispatchCycle(OutboxLanes.Notifications))
        {
            using var whatsApp = clients.CreateClient(WhatsAppClient);
            (await whatsApp.PostAsync(capture.For("whatsapp"), new StringContent("{}"))).Dispose();
            using var email = clients.CreateClient(AcsEmailClient);
            (await email.PostAsync(capture.For("acs-email"), new StringContent("{}"))).Dispose();
        }

        TraceHeaderCaptureServer.AssertNoTraceContext(capture.Single("whatsapp"));
        TraceHeaderCaptureServer.AssertNoTraceContext(capture.Single("acs-email"));
    }

    [Fact]
    public async Task Azure_sdk_requests_on_the_wire_carry_no_trace_context()
    {
        TraceCorrelation.ConfigureProcess();
        await using var capture = await TraceHeaderCaptureServer.StartAsync();
        // The production Blob options over a fresh HTTP transport (the SDK's shared transport may predate this
        // test's process configuration; in the hosts ConfigureProcess runs before any Azure client exists).
        var options = AzureBlobProofGateway.CreateClientOptions(new HttpClientTransport());
        options.Retry.MaxRetries = 0;
        var service = new BlobServiceClient(capture.For("devstoreaccount1"), options);
        using var source = new ActivitySource("Obs003.AzureWire");
        using var listener = new ActivityListener
        {
            ShouldListenTo = item => item.Name == source.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        using (var activity = source.StartActivity("synthetic-request", ActivityKind.Server)!)
        {
            activity.TraceStateString = Obs003.InboundTraceState;
            activity.AddBaggage("tenant", "synthetic-obs003-org");
            try
            {
                await service.GetBlobContainerClient("proofs").GetPropertiesAsync();
            }
            catch (Azure.RequestFailedException)
            {
                // The loopback answers {}; only the request matters.
            }
        }

        var request = Assert.Single(capture.Requests);
        Assert.Contains("x-ms-client-request-id", request.HeaderNames);
        TraceHeaderCaptureServer.AssertNoTraceContext(request);
    }

    private static void AssertRegistered(IServiceProvider services, string clientName) =>
        Assert.NotEmpty(services.GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>()
            .Get(clientName)
            .HttpClientActions);

    /// <summary>
    /// The API with AuthCenter configured (its real OIDC back-channel, no fake handler) and one probe that runs
    /// after the whole pipeline, inside the request's activity, calling the outbound clients.
    /// </summary>
    private sealed class OutboundProbeApiFactory(TraceHeaderCaptureServer capture) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration(configuration =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Authentication:Provider"] = "AuthCenter",
                    ["IdentityBootstrap:Provider"] = "Mock",
                    ["AuthCenter:Authority"] = FakeAuthCenterServer.Authority,
                    ["AuthCenter:Issuer"] = FakeAuthCenterServer.Issuer,
                    ["AuthCenter:ClientId"] = FakeAuthCenterServer.ClientId,
                    ["AuthCenter:ClientSecret"] = FakeAuthCenterServer.ClientSecret,
                    ["AuthCenter:PublicOrigin"] = FakeAuthCenterServer.PublicOrigin,
                    ["AuthCenter:SessionStore"] = "Memory",
                }));
            builder.ConfigureTestServices(services =>
                services.AddSingleton<IStartupFilter>(new ProbeStartupFilter(capture)));
        }
    }

    private sealed class ProductionWorkerFactory : WebApplicationFactory<WorkerProgram>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            // OBS-003 PR 6: outside Development and Testing the host needs the organization pseudonym key.
            builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(TelemetryPseudonymTestKey.Settings));
        }
    }

    private sealed class ProbeStartupFilter(TraceHeaderCaptureServer capture) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            next(app);
            app.Run(async context =>
            {
                if (context.Request.Path != ProbePath)
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                Assert.NotNull(Activity.Current);
                var clients = context.RequestServices.GetRequiredService<IHttpClientFactory>();
                AssertRegistered(context.RequestServices, GoogleMapsGeocodingProvider.HttpClientName);
                AssertRegistered(context.RequestServices, TwilioClient);
                using (var google = clients.CreateClient(GoogleMapsGeocodingProvider.HttpClientName))
                {
                    (await google.GetAsync(capture.For("google-maps"), context.RequestAborted)).Dispose();
                }

                using (var twilio = clients.CreateClient(TwilioClient))
                {
                    (await twilio.PostAsync(capture.For("twilio"), new StringContent("{}"), context.RequestAborted)).Dispose();
                }

                // The OIDC handler's back-channel: discovery, JWKS, token redemption and revocation all use it.
                var oidc = context.RequestServices.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
                    .Get(AuthCenterDefaults.OpenIdConnectScheme);
                (await oidc.Backchannel.GetAsync(capture.For("authcenter"), context.RequestAborted)).Dispose();

                await context.Response.WriteAsync(context.TraceIdentifier, context.RequestAborted);
            });
        };
    }
}
