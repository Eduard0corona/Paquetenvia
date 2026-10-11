using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Drivers.Application.Voice;
using Identity.Infrastructure.Mock;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Paqueteria.Application.Voice;

namespace Paqueteria.IntegrationTests.Drivers;

/// <summary>
/// VOICE-001-MASKED-CALLS-2026-10-11 over HTTP with the services stubbed: DRIVER only, shape checked before any state,
/// uniform 404, no phone number in any request to the call operation or in any answer, and the Twilio webhooks inert
/// unless the live provider is configured, signature-checked otherwise.
/// </summary>
public sealed class DriverVoiceHttpTests : IClassFixture<DriverVoiceHttpWebApplicationFactory>
{
    private const string DriverDigits = "5511112222";
    private static readonly Guid OrderId = Guid.Parse("e4000000-0000-4000-8000-000000000001");
    private static readonly string CallPath = $"/api/v1/driver/me/stops/{OrderId:D}/recipient-call";

    private readonly DriverVoiceHttpWebApplicationFactory factory;
    private readonly HttpClient client;

    public DriverVoiceHttpTests(DriverVoiceHttpWebApplicationFactory factory)
    {
        this.factory = factory;
        factory.Reset();
        client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    public static TheoryData<string, string> Operations() => new()
    {
        { "GET", "/api/v1/driver/me/phone" },
        { "PUT", "/api/v1/driver/me/phone" },
        { "DELETE", "/api/v1/driver/me/phone" },
        { "GET", CallPath },
        { "POST", CallPath },
    };

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task Only_an_active_driver_of_the_selected_organization_reaches_the_service(string method, string path)
    {
        using (var response = await client.SendAsync(Request(method, path, null)))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        using (var response = await client.SendAsync(Request(method, path, MockIdentityProfiles.ActiveDriver, includeTenant: false)))
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        foreach (var profile in new[]
                 {
                     MockIdentityProfiles.ActiveViewer,
                     MockIdentityProfiles.ActiveDispatcher,
                     MockIdentityProfiles.ActivePlatformAdminMfa,
                     MockIdentityProfiles.ActiveFinanceMfa,
                     MockIdentityProfiles.ActiveBusinessAdmin,
                     MockIdentityProfiles.SuspendedMembership,
                 })
        {
            using var response = await client.SendAsync(Request(method, path, profile));
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        Assert.Equal(0, factory.Invocations);

        using (var response = await client.SendAsync(Request(method, path, MockIdentityProfiles.ActiveDriver)))
        {
            Assert.True(response.IsSuccessStatusCode, $"{method} {path}: {response.StatusCode}");
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            Assert.DoesNotContain(DriverDigits, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        Assert.Equal(1, factory.Invocations);
    }

    [Fact]
    public async Task The_call_request_carries_no_number_and_answers_only_the_request_and_its_status()
    {
        using var response = await client.SendAsync(Request("POST", CallPath, MockIdentityProfiles.ActiveDriver));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            ["call_request_id", "status"],
            document.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(DriverVoiceHttpWebApplicationFactory.CallRequestId, document.RootElement.GetProperty("call_request_id").GetGuid());
        Assert.Equal("PLACED", document.RootElement.GetProperty("status").GetString());

        var command = Assert.Single(factory.Calls.Requests);
        Assert.Equal(DriverVoiceHttpWebApplicationFactory.DriverActorId, command.ActorId);
        Assert.Equal(MockIdentityProfiles.ViewerOrganizationId, command.OrganizationId);
        Assert.Equal(OrderId, command.OrderId);
        Assert.Equal("voice-001-http-key-0001", command.IdempotencyKey);
    }

    [Fact]
    public async Task A_malformed_call_request_is_refused_before_any_state_is_read()
    {
        var invalid = new List<HttpRequestMessage>();
        invalid.Add(Request("POST", CallPath, MockIdentityProfiles.ActiveDriver, key: null));
        invalid.Add(Request("POST", CallPath, MockIdentityProfiles.ActiveDriver, key: "short-key"));
        var twoKeys = Request("POST", CallPath, MockIdentityProfiles.ActiveDriver);
        twoKeys.Headers.Add("Idempotency-Key", "voice-001-http-key-0002");
        invalid.Add(twoKeys);
        invalid.Add(Request("POST", CallPath + "?phone=" + DriverDigits, MockIdentityProfiles.ActiveDriver));
        var withBody = Request("POST", CallPath, MockIdentityProfiles.ActiveDriver);
        withBody.Content = new StringContent($$"""{"phone":"{{DriverDigits}}"}""", Encoding.UTF8, "application/json");
        invalid.Add(withBody);

        foreach (var request in invalid)
        {
            using (request)
            using (var response = await client.SendAsync(request))
            {
                await AssertConflictAsync(response, "INVALID_REQUEST");
            }
        }

        Assert.Equal(0, factory.Invocations);
    }

    [Fact]
    public async Task Unknown_malformed_and_foreign_stops_are_the_same_not_found()
    {
        var bodies = new List<string>();
        foreach (var path in new[]
                 {
                     "/api/v1/driver/me/stops/not-a-uuid/recipient-call",
                     $"/api/v1/driver/me/stops/{OrderId:N}/recipient-call",
                     $"/api/v1/driver/me/stops/{Guid.Empty:D}/recipient-call",
                     $"/api/v1/driver/me/stops/{DriverVoiceHttpWebApplicationFactory.ForeignOrderId:D}/recipient-call",
                 })
        {
            foreach (var method in new[] { "GET", "POST" })
            {
                using var response = await client.SendAsync(Request(method, path, MockIdentityProfiles.ActiveDriver));
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                bodies.Add($"{document.RootElement.GetProperty("status").GetInt32()}|{document.RootElement.GetProperty("title").GetString()}|" +
                    $"{document.RootElement.GetProperty("type").GetString()}|{document.RootElement.TryGetProperty("code", out _)}");
            }
        }

        Assert.Single(bodies.Distinct(StringComparer.Ordinal));
        // Only the well-formed foreign id reached the service.
        Assert.Equal(2, factory.Invocations);
    }

    [Theory]
    [InlineData("ORDER_STATE_NOT_ALLOWED")]
    [InlineData("RECIPIENT_PHONE_UNAVAILABLE")]
    [InlineData("DRIVER_PHONE_REQUIRED")]
    [InlineData("DRIVER_PHONE_REJECTED")]
    [InlineData("IDEMPOTENCY_CONFLICT")]
    public async Task Refusals_are_closed_409_codes(string code)
    {
        factory.Calls.Failure = new RecipientCallConflictException(code);

        using var response = await client.SendAsync(Request("POST", CallPath, MockIdentityProfiles.ActiveDriver));

        await AssertConflictAsync(response, code);
    }

    [Fact]
    public async Task Rate_limits_and_an_unavailable_bridge_have_their_own_statuses()
    {
        factory.Calls.Failure = new RecipientCallRateLimitedException(TimeSpan.FromSeconds(61.2));
        using (var response = await client.SendAsync(Request("POST", CallPath, MockIdentityProfiles.ActiveDriver)))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
            Assert.Equal("62", Assert.Single(response.Headers.GetValues("Retry-After")));
        }

        factory.Calls.Failure = new RecipientCallUnavailableException();
        using (var response = await client.SendAsync(Request("POST", CallPath, MockIdentityProfiles.ActiveDriver)))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }

        factory.Calls.Failure = new RecipientCallForbiddenException();
        using (var response = await client.SendAsync(Request("POST", CallPath, MockIdentityProfiles.ActiveDriver)))
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    [Fact]
    public async Task The_availability_answer_is_a_flag_and_a_closed_reason()
    {
        factory.Calls.Availability = RecipientCallAvailabilityResult.No(RecipientCallReasons.DriverPhoneRequired);

        using var response = await client.SendAsync(Request("GET", CallPath, MockIdentityProfiles.ActiveDriver));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            ["available", "reason"],
            document.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.False(document.RootElement.GetProperty("available").GetBoolean());
        Assert.Equal("DRIVER_PHONE_REQUIRED", document.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task The_phone_is_accepted_once_with_consent_and_never_returned()
    {
        using var response = await client.SendAsync(Request(
            "PUT",
            "/api/v1/driver/me/phone",
            MockIdentityProfiles.ActiveDriver,
            body: """{"phone":"+52 55 1111 2222","consent_accepted":true,"consent_version":"VOICE-001-CONSENT-V1"}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("1111", text, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(text);
        Assert.Equal(
            ["consent_version", "consented_at", "registered", "voice_calls_enabled"],
            document.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.True(document.RootElement.GetProperty("registered").GetBoolean());
        var command = Assert.Single(factory.Phones.Registrations);
        Assert.Equal((DriverVoiceHttpWebApplicationFactory.DriverActorId, DriverDigits, "VOICE-001-CONSENT-V1"),
            (command.ActorId, command.PhoneDigits, command.ConsentVersion));
    }

    [Theory]
    [InlineData("""{"phone":"5511112222","consent_accepted":false,"consent_version":"VOICE-001-CONSENT-V1"}""")]
    [InlineData("""{"phone":"5511112222","consent_version":"VOICE-001-CONSENT-V1"}""")]
    [InlineData("""{"phone":"5511112222","consent_accepted":true,"consent_version":"VOICE-001-CONSENT-V0"}""")]
    [InlineData("""{"phone":"5511112222","consent_accepted":true}""")]
    [InlineData("""{"phone":"0511112222","consent_accepted":true,"consent_version":"VOICE-001-CONSENT-V1"}""")]
    [InlineData("""{"phone":"+1 555 111 2222","consent_accepted":true,"consent_version":"VOICE-001-CONSENT-V1"}""")]
    [InlineData("""{"phone":"55111122","consent_accepted":true,"consent_version":"VOICE-001-CONSENT-V1"}""")]
    [InlineData("""{"phone":"5511112222","consent_accepted":true,"consent_version":"VOICE-001-CONSENT-V1","name":"x"}""")]
    [InlineData("""{"phone":5511112222,"consent_accepted":true,"consent_version":"VOICE-001-CONSENT-V1"}""")]
    [InlineData("""[]""")]
    [InlineData("""not json""")]
    public async Task A_phone_without_consent_or_outside_the_rules_is_refused_before_any_state(string body)
    {
        using var response = await client.SendAsync(Request("PUT", "/api/v1/driver/me/phone", MockIdentityProfiles.ActiveDriver, body: body));

        await AssertConflictAsync(response, "INVALID_REQUEST");
        Assert.Equal(0, factory.Invocations);
    }

    [Fact]
    public async Task Webhooks_are_inert_unless_the_live_provider_is_configured()
    {
        factory.Status.Mode = VoiceBridgeMode.Synthetic;
        foreach (var path in new[] { VoiceWebhookPaths.CallStatusPathAndQuery(MockIdentityProfiles.ViewerOrganizationId, Guid.NewGuid()), VoiceWebhookPaths.Inbound })
        {
            using var response = await client.SendAsync(Webhook(path, signature: "valid-signature"));
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        Assert.Equal(0, factory.Verifier.Calls);
        Assert.Equal(0, factory.Invocations);
    }

    [Fact]
    public async Task The_status_callback_needs_a_valid_signature_and_keeps_only_the_call_outcome()
    {
        factory.Status.Mode = VoiceBridgeMode.Live;
        var callRequestId = Guid.NewGuid();
        var path = VoiceWebhookPaths.CallStatusPathAndQuery(MockIdentityProfiles.ViewerOrganizationId, callRequestId);

        using (var response = await client.SendAsync(Webhook(path, signature: null)))
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        using (var response = await client.SendAsync(Webhook(path, signature: "forged")))
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        Assert.Equal(0, factory.Invocations);

        using (var response = await client.SendAsync(Webhook(path, signature: "valid-signature")))
        {
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        var report = Assert.Single(factory.Calls.Reports);
        Assert.Equal(
            new VoiceCallStatusReport(MockIdentityProfiles.ViewerOrganizationId, callRequestId, "CA" + new string('a', 32), "completed", 37),
            report);
        Assert.Equal(path, factory.Verifier.LastPathAndQuery);
        Assert.Contains("From", factory.Verifier.LastFieldNames);

        // A signed but malformed callback is a 400 and reaches nothing.
        foreach (var malformed in new[]
                 {
                     Webhook(VoiceWebhookPaths.CallStatus + $"?o={MockIdentityProfiles.ViewerOrganizationId:D}", "valid-signature"),
                     Webhook(path, "valid-signature", status: "exploded"),
                     Webhook(path, "valid-signature", duration: "forty"),
                     Webhook(path, "valid-signature", duration: "-1"),
                     Webhook(path, "valid-signature", json: true),
                 })
        {
            using (malformed)
            using (var response = await client.SendAsync(malformed))
            {
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            }
        }

        Assert.Single(factory.Calls.Reports);
    }

    [Fact]
    public async Task A_call_to_the_company_number_hears_the_fixed_twiml_without_a_session()
    {
        factory.Status.Mode = VoiceBridgeMode.Live;

        using (var forged = await client.SendAsync(Webhook(VoiceWebhookPaths.Inbound, signature: "forged")))
        {
            Assert.Equal(HttpStatusCode.Forbidden, forged.StatusCode);
        }

        using var response = await client.SendAsync(Webhook(VoiceWebhookPaths.Inbound, signature: "valid-signature"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/xml", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(DriverVoiceHttpWebApplicationFactory.InboundTwiml, await response.Content.ReadAsStringAsync());
        Assert.Equal(0, factory.Invocations);
    }

    private static HttpRequestMessage Request(
        string method,
        string path,
        string? profile,
        bool includeTenant = true,
        string? key = "voice-001-http-key-0001",
        string? body = null)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "PUT")
        {
            request.Content = new StringContent(
                body ?? """{"phone":"55 1111 2222","consent_accepted":true,"consent_version":"VOICE-001-CONSENT-V1"}""",
                Encoding.UTF8,
                "application/json");
        }

        if (method == "POST" && key is not null)
        {
            request.Headers.Add("Idempotency-Key", key);
        }

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

    private static HttpRequestMessage Webhook(
        string pathAndQuery,
        string? signature,
        string status = "completed",
        string duration = "37",
        bool json = false)
    {
        var fields = new Dictionary<string, string>
        {
            ["AccountSid"] = "AC" + new string('0', 32),
            ["CallSid"] = "CA" + new string('a', 32),
            ["CallStatus"] = status,
            ["CallDuration"] = duration,
            ["From"] = "+525512340000",
            ["To"] = "+52" + DriverDigits,
        };
        var request = new HttpRequestMessage(HttpMethod.Post, pathAndQuery)
        {
            Content = json
                ? new StringContent(JsonSerializer.Serialize(fields), Encoding.UTF8, "application/json")
                : new FormUrlEncodedContent(fields),
        };
        if (signature is not null)
        {
            request.Headers.Add("X-Twilio-Signature", signature);
        }

        return request;
    }

    private static async Task AssertConflictAsync(HttpResponseMessage response, string code)
    {
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, document.RootElement.GetProperty("code").GetString());
        Assert.False(document.RootElement.TryGetProperty("detail", out _));
    }
}

public sealed class DriverVoiceHttpWebApplicationFactory : WebApplicationFactory<Program>
{
    internal const string InboundTwiml = "<Response><Say>Mensaje fijo sintetico.</Say><Hangup /></Response>";
    internal static readonly Guid DriverActorId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaa11");
    internal static readonly Guid ForeignOrderId = Guid.Parse("e4000000-0000-4000-8000-0000000000ff");
    internal static readonly Guid CallRequestId = Guid.Parse("e4000000-0000-4000-8000-0000000000aa");

    internal StubPhoneService Phones { get; } = new();

    internal StubCallService Calls { get; } = new();

    internal StubVoiceStatus Status { get; } = new();

    internal StubVerifier Verifier { get; } = new();

    internal int Invocations => Phones.Invocations + Calls.Invocations;

    internal void Reset()
    {
        Phones.Reset();
        Calls.Reset();
        Verifier.Reset();
        Status.Mode = VoiceBridgeMode.Synthetic;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration(configuration =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Provider"] = "Mock",
                ["IdentityBootstrap:Provider"] = "Mock",
                ["Voice:Provider"] = "Synthetic",
            }));
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IDriverPhoneService>();
            services.AddSingleton<IDriverPhoneService>(Phones);
            services.RemoveAll<IRecipientCallService>();
            services.AddSingleton<IRecipientCallService>(Calls);
            services.RemoveAll<IVoiceBridgeStatus>();
            services.AddSingleton<IVoiceBridgeStatus>(Status);
            services.RemoveAll<IVoiceWebhookVerifier>();
            services.AddSingleton<IVoiceWebhookVerifier>(Verifier);
        });
    }

    internal sealed class StubVoiceStatus : IVoiceBridgeStatus
    {
        public VoiceBridgeMode Mode { get; set; } = VoiceBridgeMode.Synthetic;

        public string ProviderName => Mode == VoiceBridgeMode.Live ? "TWILIO" : "SYNTHETIC";
    }

    internal sealed class StubVerifier : IVoiceWebhookVerifier
    {
        private int calls;

        public int Calls => Volatile.Read(ref calls);

        public string? LastPathAndQuery { get; private set; }

        public IReadOnlyList<string> LastFieldNames { get; private set; } = [];

        public void Reset()
        {
            Volatile.Write(ref calls, 0);
            LastPathAndQuery = null;
            LastFieldNames = [];
        }

        public VoiceWebhookVerdict Verify(VoiceWebhookRequest request)
        {
            Interlocked.Increment(ref calls);
            LastPathAndQuery = request.PathAndQuery;
            LastFieldNames = request.Form.Select(field => field.Key).ToArray();
            return request.Signature == "valid-signature" ? VoiceWebhookVerdict.Verified : VoiceWebhookVerdict.Rejected;
        }

        public string BuildInboundCallResponse() => InboundTwiml;
    }

    internal sealed class StubPhoneService : IDriverPhoneService
    {
        private int invocations;

        public int Invocations => Volatile.Read(ref invocations);

        public ConcurrentQueue<RegisterDriverPhoneCommand> Registrations { get; private set; } = new();

        public void Reset()
        {
            Volatile.Write(ref invocations, 0);
            Registrations = new();
        }

        public Task<DriverPhoneStatusResult> GetAsync(Guid actorId, Guid organizationId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref invocations);
            return Task.FromResult(new DriverPhoneStatusResult(true, false, null, null));
        }

        public Task<DriverPhoneStatusResult> RegisterAsync(RegisterDriverPhoneCommand command, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref invocations);
            Registrations.Enqueue(command);
            return Task.FromResult(new DriverPhoneStatusResult(
                true, true, command.ConsentVersion, new DateTimeOffset(2026, 10, 11, 16, 0, 0, TimeSpan.Zero)));
        }

        public Task<DriverPhoneStatusResult> RemoveAsync(
            Guid actorId,
            Guid organizationId,
            string? requestId,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref invocations);
            return Task.FromResult(new DriverPhoneStatusResult(true, false, null, null));
        }
    }

    internal sealed class StubCallService : IRecipientCallService
    {
        private int invocations;

        public int Invocations => Volatile.Read(ref invocations);

        public Exception? Failure { get; set; }

        public RecipientCallAvailabilityResult Availability { get; set; } = RecipientCallAvailabilityResult.Yes;

        public ConcurrentQueue<RequestRecipientCallCommand> Requests { get; private set; } = new();

        public ConcurrentQueue<VoiceCallStatusReport> Reports { get; private set; } = new();

        public void Reset()
        {
            Volatile.Write(ref invocations, 0);
            Failure = null;
            Availability = RecipientCallAvailabilityResult.Yes;
            Requests = new();
            Reports = new();
        }

        public Task<RecipientCallAvailabilityResult> GetAvailabilityAsync(
            Guid actorId,
            Guid organizationId,
            Guid orderId,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref invocations);
            return orderId == ForeignOrderId
                ? Task.FromException<RecipientCallAvailabilityResult>(new RecipientCallNotFoundException())
                : Task.FromResult(Availability);
        }

        public Task<RecipientCallRequestResult> RequestAsync(RequestRecipientCallCommand command, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref invocations);
            Requests.Enqueue(command);
            if (command.OrderId == ForeignOrderId)
            {
                return Task.FromException<RecipientCallRequestResult>(new RecipientCallNotFoundException());
            }

            return Failure is { } failure
                ? Task.FromException<RecipientCallRequestResult>(failure)
                : Task.FromResult(new RecipientCallRequestResult(CallRequestId, RecipientCallStatuses.Placed));
        }

        public Task<bool> RecordStatusAsync(VoiceCallStatusReport report, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref invocations);
            Reports.Enqueue(report);
            return Task.FromResult(true);
        }
    }
}
