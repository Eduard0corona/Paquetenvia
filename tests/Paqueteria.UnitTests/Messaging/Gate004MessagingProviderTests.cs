using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Azure.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Notifications.Application.Dispatching;
using Paqueteria.Application.Messaging;
using Paqueteria.Infrastructure.Cloud;
using Paqueteria.Infrastructure.Messaging;

namespace Paqueteria.UnitTests.Messaging;

/// <summary>
/// GATE-004-CHANNELS: WhatsApp (Meta Cloud API) and email (Azure Communication Services) adapters
/// driven through a fake <see cref="HttpMessageHandler"/>. No test reaches Meta or Azure.
/// </summary>
public sealed class Gate004MessagingProviderTests
{
    private const string Phone = "+5215512345678";
    private const string EmailAddress = "cliente.sintetico@example.test";
    private const string AccessToken = "EAAG-synthetic-token-7c1d";
    private const string PhoneNumberId = "106540352242922";
    private const string ParameterText = "Nombre Sintetico";
    private static readonly Guid MessageId = Guid.Parse("7a4f2c1e-0000-4000-8000-000000000001");

    // ------------------------------------------------------------------ WhatsApp (Meta Cloud API)

    [Fact]
    public async Task WhatsApp_sends_one_template_message_and_returns_the_wamid()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, """{"messaging_product":"whatsapp","contacts":[{"input":"5215512345678","wa_id":"5215512345678"}],"messages":[{"id":"wamid.SYNTHETIC=="}]}"""));
        var provider = WhatsApp(handler, out _);

        var result = await provider.SendAsync(WhatsAppRequest(), default);

        Assert.Equal(new MessagingResult(MessagingOutcome.Accepted, MessagingResultCodes.Accepted, "wamid.SYNTHETIC=="), result);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal($"https://graph.facebook.com/v23.0/{PhoneNumberId}/messages", request.Uri.ToString());
        Assert.Equal($"Bearer {AccessToken}", request.Authorization);
        var body = JsonNode.Parse(request.Body)!;
        Assert.Equal("whatsapp", (string?)body["messaging_product"]);
        Assert.Equal("5215512345678", (string?)body["to"]);
        Assert.Equal("template", (string?)body["type"]);
        Assert.Equal("owner_tracking_link", (string?)body["template"]!["name"]);
        Assert.Equal("es_MX", (string?)body["template"]!["language"]!["code"]);
        var parameters = body["template"]!["components"]![0]!["parameters"]!.AsArray();
        Assert.Equal(new[] { ParameterText, "ORD_1234567890123456789012" }, parameters.Select(p => (string)p!["text"]!).ToArray());
    }

    [Theory]
    [InlineData(130429, HttpStatusCode.BadRequest, MessagingOutcome.TransientFailure, MessagingResultCodes.RateLimited)]
    [InlineData(131056, HttpStatusCode.BadRequest, MessagingOutcome.TransientFailure, MessagingResultCodes.RateLimited)]
    [InlineData(131000, HttpStatusCode.InternalServerError, MessagingOutcome.TransientFailure, MessagingResultCodes.Unavailable)]
    [InlineData(133004, HttpStatusCode.ServiceUnavailable, MessagingOutcome.TransientFailure, MessagingResultCodes.Unavailable)]
    [InlineData(131026, HttpStatusCode.BadRequest, MessagingOutcome.PermanentFailure, MessagingResultCodes.RecipientUndeliverable)]
    [InlineData(131047, HttpStatusCode.BadRequest, MessagingOutcome.PermanentFailure, MessagingResultCodes.ReengagementRequired)]
    [InlineData(132001, HttpStatusCode.BadRequest, MessagingOutcome.PermanentFailure, MessagingResultCodes.ProviderTemplateRejected)]
    [InlineData(132000, HttpStatusCode.BadRequest, MessagingOutcome.PermanentFailure, MessagingResultCodes.ProviderTemplateRejected)]
    [InlineData(190, HttpStatusCode.Unauthorized, MessagingOutcome.PermanentFailure, MessagingResultCodes.AuthenticationFailed)]
    [InlineData(368, HttpStatusCode.BadRequest, MessagingOutcome.PermanentFailure, MessagingResultCodes.PolicyBlocked)]
    [InlineData(100, HttpStatusCode.BadRequest, MessagingOutcome.PermanentFailure, MessagingResultCodes.Rejected)]
    [InlineData(999999, HttpStatusCode.ServiceUnavailable, MessagingOutcome.TransientFailure, MessagingResultCodes.Unavailable)]
    public async Task WhatsApp_classifies_meta_error_codes(long code, HttpStatusCode status, MessagingOutcome outcome, string expectedCode)
    {
        var handler = new FakeHandler(_ => Json(status, $$$"""{"error":{"message":"(#{{{code}}}) contains {{{Phone}}}","type":"OAuthException","code":{{{code}}},"fbtrace_id":"synthetic"}}"""));

        var result = await WhatsApp(handler, out _).SendAsync(WhatsAppRequest(), default);

        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(expectedCode, result.Code);
        Assert.Null(result.ProviderReference);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, MessagingOutcome.TransientFailure, MessagingResultCodes.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, MessagingOutcome.TransientFailure, MessagingResultCodes.Unavailable)]
    [InlineData(HttpStatusCode.BadGateway, MessagingOutcome.TransientFailure, MessagingResultCodes.Unavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout, MessagingOutcome.AmbiguousTimeout, MessagingResultCodes.Timeout)]
    [InlineData(HttpStatusCode.RequestTimeout, MessagingOutcome.AmbiguousTimeout, MessagingResultCodes.Timeout)]
    [InlineData(HttpStatusCode.Forbidden, MessagingOutcome.PermanentFailure, MessagingResultCodes.AuthenticationFailed)]
    [InlineData(HttpStatusCode.BadRequest, MessagingOutcome.PermanentFailure, MessagingResultCodes.Rejected)]
    [InlineData(HttpStatusCode.NotFound, MessagingOutcome.PermanentFailure, MessagingResultCodes.Rejected)]
    public async Task WhatsApp_falls_back_to_the_http_status_without_a_meta_error_body(
        HttpStatusCode status, MessagingOutcome outcome, string expectedCode)
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(status) { Content = new StringContent("<html>gateway</html>") });

        var result = await WhatsApp(handler, out _).SendAsync(WhatsAppRequest(), default);

        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(expectedCode, result.Code);
    }

    [Fact]
    public async Task WhatsApp_rate_limit_carries_a_bounded_retry_after_hint()
    {
        var handler = new FakeHandler(_ =>
        {
            var response = Json(HttpStatusCode.TooManyRequests, """{"error":{"code":130429}}""");
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(42));
            return response;
        });

        var result = await WhatsApp(handler, out _).SendAsync(WhatsAppRequest(), default);

        Assert.Equal(MessagingOutcome.TransientFailure, result.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(42), result.RetryAfter);

        var huge = new FakeHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromDays(3));
            return response;
        });
        Assert.Equal(TimeSpan.FromHours(1), (await WhatsApp(huge, out _).SendAsync(WhatsAppRequest(), default)).RetryAfter);
    }

    [Fact]
    public async Task WhatsApp_success_without_a_message_id_is_ambiguous_not_accepted()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, """{"messages":[]}"""));

        var result = await WhatsApp(handler, out _).SendAsync(WhatsAppRequest(), default);

        Assert.Equal(new MessagingResult(MessagingOutcome.AmbiguousTimeout, MessagingResultCodes.ResponseInvalid), result);
    }

    [Fact]
    public async Task A_timeout_after_the_request_left_is_ambiguous_and_caller_cancellation_propagates()
    {
        var slow = new FakeHandler(async (_, token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), token);
            return Json(HttpStatusCode.OK, "{}");
        });
        var provider = WhatsApp(slow, out _, timeoutSeconds: 1);

        var result = await provider.SendAsync(WhatsAppRequest(), default);
        Assert.Equal(new MessagingResult(MessagingOutcome.AmbiguousTimeout, MessagingResultCodes.Timeout), result);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.SendAsync(WhatsAppRequest(), cancelled.Token).AsTask());
    }

    [Theory]
    [InlineData(HttpRequestError.ConnectionError, MessagingOutcome.TransientFailure, MessagingResultCodes.Unreachable)]
    [InlineData(HttpRequestError.NameResolutionError, MessagingOutcome.TransientFailure, MessagingResultCodes.Unreachable)]
    [InlineData(HttpRequestError.ResponseEnded, MessagingOutcome.AmbiguousTimeout, MessagingResultCodes.Timeout)]
    public async Task Transport_failures_distinguish_not_sent_from_maybe_sent(
        HttpRequestError error, MessagingOutcome outcome, string code)
    {
        var handler = new FakeHandler(_ => throw new HttpRequestException(error, "synthetic"));

        var result = await WhatsApp(handler, out _).SendAsync(WhatsAppRequest(), default);

        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(code, result.Code);
    }

    [Theory]
    [InlineData("unknown.template", Phone, new[] { "a", "b" }, MessagingResultCodes.TemplateNotConfigured)]
    [InlineData("tracking-link", Phone, new[] { "only-one" }, MessagingResultCodes.TemplateParametersInvalid)]
    [InlineData("tracking-link", Phone, new[] { "line\nbreak", "b" }, MessagingResultCodes.TemplateParametersInvalid)]
    [InlineData("tracking-link", Phone, new[] { "five     spaces", "b" }, MessagingResultCodes.TemplateParametersInvalid)]
    [InlineData("tracking-link", "55-1234", new[] { "a", "b" }, MessagingResultCodes.RecipientInvalid)]
    [InlineData("tracking-link", "+0512345678", new[] { "a", "b" }, MessagingResultCodes.RecipientInvalid)]
    public async Task WhatsApp_rejects_invalid_requests_permanently_without_calling_meta(
        string templateKey, string recipient, string[] parameters, string code)
    {
        var handler = new FakeHandler(_ => throw new InvalidOperationException("must not be called"));

        var result = await WhatsApp(handler, out _).SendAsync(
            new MessagingRequest(MessageId, MessagingChannel.WhatsApp, new MessagingRecipient(recipient), templateKey, parameters),
            default);

        Assert.Equal(new MessagingResult(MessagingOutcome.PermanentFailure, code), result);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Logs_never_contain_the_recipient_the_parameters_the_token_or_the_provider_body()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.BadRequest, $$$"""{"error":{"message":"bad {{{Phone}}} {{{ParameterText}}}","code":131026}}"""));
        var provider = WhatsApp(handler, out var logs);
        await provider.SendAsync(WhatsAppRequest(), default);
        var email = Email(new FakeHandler(_ => Json(HttpStatusCode.Accepted, """{"id":"op-1","status":"Running"}""")), new FakeCredential(), out var emailLogs);
        await email.SendAsync(EmailRequest(), default);

        var written = string.Join('\n', logs.Entries.Concat(emailLogs.Entries));
        Assert.NotEmpty(logs.Entries);
        foreach (var secret in new[] { Phone, "5215512345678", ParameterText, AccessToken, PhoneNumberId, EmailAddress, "Hola", "op-1" })
        {
            Assert.DoesNotContain(secret, written, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal("[redacted]", WhatsAppRequest().Recipient.ToString());
        Assert.DoesNotContain(Phone, WhatsAppRequest().ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(ParameterText, WhatsAppRequest().ToString(), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ Email (ACS)

    [Fact]
    public async Task Email_sends_through_acs_with_a_managed_identity_token_and_a_stable_operation_id()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.Accepted, """{"id":"acs-operation-1","status":"Running"}"""));
        var credential = new FakeCredential();
        var provider = Email(handler, credential, out _);

        var result = await provider.SendAsync(EmailRequest(), default);

        Assert.Equal(new MessagingResult(MessagingOutcome.Accepted, MessagingResultCodes.Accepted, "acs-operation-1"), result);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://pv-pilot.unitedstates.communication.azure.com/emails:send?api-version=2023-03-31", request.Uri.ToString());
        Assert.Equal("Bearer synthetic-entra-token-1", request.Authorization);
        Assert.Equal(MessageId.ToString("D"), request.OperationId);
        Assert.Equal(["https://communication.azure.com/.default"], credential.Scopes.Single());
        var body = JsonNode.Parse(request.Body)!;
        Assert.Equal("DoNotReply@notificaciones.example.test", (string?)body["senderAddress"]);
        Assert.Equal($"Asunto {ParameterText}", (string?)body["content"]!["subject"]);
        Assert.Equal($"Hola {ParameterText}, pedido ORD_1234567890123456789012.", (string?)body["content"]!["plainText"]);
        Assert.Equal(EmailAddress, (string?)body["recipients"]!["to"]![0]!["address"]);
        Assert.True((bool)body["userEngagementTrackingDisabled"]!);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("""{"status":"Running"}""")]
    [InlineData("not json")]
    public async Task Email_success_without_a_confirmed_operation_id_is_ambiguous(string payload)
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.Accepted, payload));
        var provider = Email(handler, new FakeCredential(), out _);

        var result = await provider.SendAsync(EmailRequest(), default);

        Assert.Equal(new MessagingResult(MessagingOutcome.AmbiguousTimeout, MessagingResultCodes.ResponseInvalid), result);
    }

    [Fact]
    public async Task Adapters_create_a_client_per_send_so_pooled_handlers_rotate()
    {
        var whatsAppHandler = new FakeHandler(_ => Json(HttpStatusCode.OK, """{"messages":[{"id":"wamid.SYNTHETIC=="}]}"""));
        var whatsAppFactory = new HandlerClientFactory(whatsAppHandler);
        var whatsApp = new MetaWhatsAppCloudApiProvider(
            whatsAppFactory, Options.Create(ProductionOptions()), TimeProvider.System, new RecordingLogger<MetaWhatsAppCloudApiProvider>());
        var emailHandler = new FakeHandler(_ => Json(HttpStatusCode.Accepted, """{"id":"acs-operation-1"}"""));
        var emailFactory = new HandlerClientFactory(emailHandler);
        var email = new AzureCommunicationEmailProvider(
            emailFactory, new FakeCredential(), Options.Create(ProductionOptions()), TimeProvider.System, new RecordingLogger<AzureCommunicationEmailProvider>());

        await whatsApp.SendAsync(WhatsAppRequest(), default);
        await whatsApp.SendAsync(WhatsAppRequest(), default);
        await email.SendAsync(EmailRequest(), default);
        await email.SendAsync(EmailRequest(), default);

        Assert.Equal(2, whatsAppFactory.Created);
        Assert.Equal(2, emailFactory.Created);
    }

    // ------------------------------------------------------------------ AI-03 §16 resilience

    [Fact]
    public async Task WhatsApp_circuit_opens_after_consecutive_failures_and_one_probe_closes_it()
    {
        var failing = true;
        var handler = new FakeHandler(_ => failing
            ? Json(HttpStatusCode.ServiceUnavailable, "{}")
            : Json(HttpStatusCode.OK, """{"messages":[{"id":"wamid.SYNTHETIC=="}]}"""));
        var clock = new Security.Adp001KeyVaultWrapClientHttpTests.ManualTimeProvider(DateTimeOffset.Parse("2026-09-28T00:00:00Z"));
        var options = ProductionOptions();
        options.WhatsApp.MetaCloudApi.CircuitBreakerFailureThreshold = 2;
        options.WhatsApp.MetaCloudApi.CircuitBreakerBreakSeconds = 30;
        using var provider = new MetaWhatsAppCloudApiProvider(
            new HandlerClientFactory(handler), Options.Create(options), clock, new RecordingLogger<MetaWhatsAppCloudApiProvider>());

        Assert.Equal(MessagingOutcome.TransientFailure, (await provider.SendAsync(WhatsAppRequest(), default)).Outcome);
        Assert.Equal(MessagingOutcome.TransientFailure, (await provider.SendAsync(WhatsAppRequest(), default)).Outcome);

        var rejected = await provider.SendAsync(WhatsAppRequest(), default);
        Assert.Equal(new MessagingResult(MessagingOutcome.TransientFailure, MessagingResultCodes.CircuitOpen, RetryAfter: TimeSpan.FromSeconds(30)), rejected);
        Assert.Equal(2, handler.Requests.Count);

        clock.Advance(TimeSpan.FromSeconds(31));
        failing = false;
        var probe = await provider.SendAsync(WhatsAppRequest(), default);
        Assert.Equal(MessagingOutcome.Accepted, probe.Outcome);
        Assert.Equal(MessagingCircuitBreaker.State.Closed, provider.Guard.CircuitBreaker.CurrentState);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task Permanent_provider_answers_do_not_open_the_circuit()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.BadRequest, """{"error":{"code":132001}}"""));
        var options = ProductionOptions();
        options.WhatsApp.MetaCloudApi.CircuitBreakerFailureThreshold = 1;
        using var provider = new MetaWhatsAppCloudApiProvider(
            new HandlerClientFactory(handler), Options.Create(options), TimeProvider.System, new RecordingLogger<MetaWhatsAppCloudApiProvider>());

        Assert.Equal(MessagingOutcome.PermanentFailure, (await provider.SendAsync(WhatsAppRequest(), default)).Outcome);
        Assert.Equal(MessagingOutcome.PermanentFailure, (await provider.SendAsync(WhatsAppRequest(), default)).Outcome);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Email_bulkhead_rejects_a_send_beyond_the_concurrency_limit_without_calling_acs()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FakeHandler(async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
            return Json(HttpStatusCode.Accepted, """{"id":"acs-operation-1"}""");
        });
        var options = ProductionOptions();
        options.Email.AzureCommunicationServices.MaxConcurrentRequests = 1;
        using var provider = new AzureCommunicationEmailProvider(
            new HandlerClientFactory(handler), new FakeCredential(), Options.Create(options), TimeProvider.System, new RecordingLogger<AzureCommunicationEmailProvider>());

        var first = provider.SendAsync(EmailRequest(), default).AsTask();
        await entered.Task;
        var second = await provider.SendAsync(EmailRequest(), default);
        release.SetResult();

        Assert.Equal(new MessagingResult(MessagingOutcome.TransientFailure, MessagingResultCodes.ConcurrencyLimited), second);
        Assert.Equal(MessagingOutcome.Accepted, (await first).Outcome);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Caller_cancellation_records_no_failure_and_frees_the_bulkhead()
    {
        var handler = new FakeHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("unreachable");
        });
        var options = ProductionOptions();
        options.WhatsApp.MetaCloudApi.CircuitBreakerFailureThreshold = 1;
        options.WhatsApp.MetaCloudApi.MaxConcurrentRequests = 1;
        using var provider = new MetaWhatsAppCloudApiProvider(
            new HandlerClientFactory(handler), Options.Create(options), TimeProvider.System, new RecordingLogger<MetaWhatsAppCloudApiProvider>());
        using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.SendAsync(WhatsAppRequest(), cancelled.Token).AsTask());

        Assert.Equal(MessagingCircuitBreaker.State.Closed, provider.Guard.CircuitBreaker.CurrentState);
        using var again = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.SendAsync(WhatsAppRequest(), again.Token).AsTask());
    }

    [Theory]
    [InlineData(0, 30, 8)]
    [InlineData(5, 0, 8)]
    [InlineData(5, 30, 0)]
    [InlineData(51, 30, 8)]
    public void Resilience_settings_out_of_range_fail_validation(int threshold, int breakSeconds, int concurrency)
    {
        var options = ProductionOptions();
        options.WhatsApp.MetaCloudApi.CircuitBreakerFailureThreshold = threshold;
        options.WhatsApp.MetaCloudApi.CircuitBreakerBreakSeconds = breakSeconds;
        options.WhatsApp.MetaCloudApi.MaxConcurrentRequests = concurrency;
        options.Email.AzureCommunicationServices.CircuitBreakerFailureThreshold = threshold;
        options.Email.AzureCommunicationServices.CircuitBreakerBreakSeconds = breakSeconds;
        options.Email.AzureCommunicationServices.MaxConcurrentRequests = concurrency;

        var failures = MessagingOptionsValidator.Validate(options, syntheticAllowed: false);

        Assert.Equal(2, failures.Count(failure => failure.Contains("resilience", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Email_reuses_the_token_until_it_nears_expiry_and_drops_it_after_401()
    {
        var status = HttpStatusCode.Accepted;
        var handler = new FakeHandler(_ => Json(status, """{"id":"op"}"""));
        var credential = new FakeCredential();
        var provider = Email(handler, credential, out _);

        await provider.SendAsync(EmailRequest(), default);
        await provider.SendAsync(EmailRequest(), default);
        Assert.Equal(1, credential.Calls);

        status = HttpStatusCode.Unauthorized;
        var denied = await provider.SendAsync(EmailRequest(), default);
        Assert.Equal(new MessagingResult(MessagingOutcome.PermanentFailure, MessagingResultCodes.AuthenticationFailed), denied);

        status = HttpStatusCode.Accepted;
        await provider.SendAsync(EmailRequest(), default);
        Assert.Equal(2, credential.Calls);
    }

    [Fact]
    public async Task Email_credential_failure_is_transient_and_sends_nothing()
    {
        var handler = new FakeHandler(_ => throw new InvalidOperationException("must not be called"));
        var credential = new FakeCredential { Failure = new InvalidOperationException("IMDS unavailable") };

        var result = await Email(handler, credential, out _).SendAsync(EmailRequest(), default);

        Assert.Equal(new MessagingResult(MessagingOutcome.TransientFailure, MessagingResultCodes.AuthenticationFailed), result);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, MessagingOutcome.TransientFailure, MessagingResultCodes.RateLimited)]
    [InlineData(HttpStatusCode.ServiceUnavailable, MessagingOutcome.TransientFailure, MessagingResultCodes.Unavailable)]
    [InlineData(HttpStatusCode.BadRequest, MessagingOutcome.PermanentFailure, MessagingResultCodes.Rejected)]
    [InlineData(HttpStatusCode.Forbidden, MessagingOutcome.PermanentFailure, MessagingResultCodes.AuthenticationFailed)]
    public async Task Email_classifies_acs_failures(HttpStatusCode status, MessagingOutcome outcome, string code)
    {
        var handler = new FakeHandler(_ =>
        {
            var response = Json(status, """{"error":{"code":"Synthetic","message":"cliente.sintetico@example.test"}}""");
            response.Headers.TryAddWithoutValidation("retry-after-ms", "1500");
            return response;
        });

        var result = await Email(handler, new FakeCredential(), out _).SendAsync(EmailRequest(), default);

        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(code, result.Code);
        if (outcome == MessagingOutcome.TransientFailure)
        {
            Assert.Equal(TimeSpan.FromMilliseconds(1500), result.RetryAfter);
        }
    }

    [Theory]
    [InlineData("no-at-sign")]
    [InlineData("Nombre <cliente@example.test>")]
    [InlineData("a@example.test, b@example.test")]
    public async Task Email_rejects_an_invalid_recipient_without_calling_acs(string recipient)
    {
        var handler = new FakeHandler(_ => throw new InvalidOperationException("must not be called"));

        var result = await Email(handler, new FakeCredential(), out _).SendAsync(
            EmailRequest() with { Recipient = new MessagingRecipient(recipient) },
            default);

        Assert.Equal(new MessagingResult(MessagingOutcome.PermanentFailure, MessagingResultCodes.RecipientInvalid), result);
        Assert.Empty(handler.Requests);
    }

    // ------------------------------------------------------------------ Router, synthetic, options, DI

    [Fact]
    public async Task Channels_are_disabled_by_default_and_fail_closed()
    {
        using var services = BuildServices(new Dictionary<string, string?>(), Environments.Production);
        var provider = services.GetRequiredService<IMessagingProvider>();

        Assert.Equal(MessagingResultCodes.ChannelDisabled, (await provider.SendAsync(WhatsAppRequest(), default)).Code);
        Assert.Equal(MessagingResultCodes.ChannelDisabled, (await provider.SendAsync(EmailRequest(), default)).Code);
        Assert.Equal(MessagingOutcome.PermanentFailure, (await provider.SendAsync(EmailRequest(), default)).Outcome);
    }

    [Theory]
    [InlineData(MessagingOutcome.Accepted, MessagingResultCodes.SyntheticAccepted)]
    [InlineData(MessagingOutcome.TransientFailure, MessagingResultCodes.SyntheticTransient)]
    [InlineData(MessagingOutcome.PermanentFailure, MessagingResultCodes.SyntheticPermanent)]
    [InlineData(MessagingOutcome.AmbiguousTimeout, MessagingResultCodes.SyntheticAmbiguous)]
    public async Task The_synthetic_provider_is_deterministic_and_never_uses_the_network(MessagingOutcome outcome, string code)
    {
        var settings = SyntheticSettings();
        settings["Messaging:WhatsApp:SyntheticOutcome"] = outcome.ToString();
        settings["Messaging:Email:SyntheticOutcome"] = outcome.ToString();
        using var services = BuildServices(settings, "Testing");
        var provider = services.GetRequiredService<IMessagingProvider>();

        var first = await provider.SendAsync(WhatsAppRequest(), default);
        var second = await provider.SendAsync(WhatsAppRequest(), default);
        var email = await provider.SendAsync(EmailRequest(), default);

        Assert.Equal(first, second);
        Assert.Equal(outcome, first.Outcome);
        Assert.Equal(code, first.Code);
        Assert.Equal(code, email.Code);
        Assert.Equal(outcome == MessagingOutcome.Accepted, first.ProviderReference is not null);
        if (outcome == MessagingOutcome.Accepted)
        {
            Assert.NotEqual(first.ProviderReference, email.ProviderReference);
        }

        Assert.Equal(
            MessagingResultCodes.TemplateNotConfigured,
            (await provider.SendAsync(WhatsAppRequest() with { TemplateKey = "missing" }, default)).Code);
    }

    [Fact]
    public void Synthetic_providers_are_refused_outside_development_testing_and_dev_synthetic()
    {
        using var services = BuildServices(SyntheticSettings(), Environments.Production);

        var failure = Assert.Throws<OptionsValidationException>(() => services.GetRequiredService<IOptions<MessagingOptions>>().Value);

        Assert.Contains("Messaging:WhatsApp:Provider=Synthetic", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Messaging:Email:Provider=Synthetic", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Production_providers_validate_their_configuration_without_echoing_values()
    {
        var settings = new Dictionary<string, string?>
        {
            ["Messaging:WhatsApp:Provider"] = "MetaCloudApi",
            ["Messaging:WhatsApp:MetaCloudApi:PhoneNumberId"] = "not-numeric-secretish",
            ["Messaging:WhatsApp:MetaCloudApi:BaseUri"] = "http://graph.facebook.com",
            ["Messaging:WhatsApp:Templates:tracking-link:Name"] = "Bad Name",
            ["Messaging:WhatsApp:Templates:tracking-link:LanguageCode"] = "es_MX",
            ["Messaging:Email:Provider"] = "AzureCommunicationServices",
            ["Messaging:Email:AzureCommunicationServices:Endpoint"] = "https://pv.communication.azure.com/path",
            ["Messaging:Email:AzureCommunicationServices:SenderAddress"] = "Name <x@y.test>",
            ["Messaging:Email:Templates:tracking-link:Subject"] = "S {{1}}",
            ["Messaging:Email:Templates:tracking-link:PlainText"] = "B {{2}}",
            ["Messaging:Email:Templates:tracking-link:ParameterCount"] = "1",
        };
        using var services = BuildServices(settings, Environments.Production);

        var failure = Assert.Throws<OptionsValidationException>(() => services.GetRequiredService<IOptions<MessagingOptions>>().Value);

        foreach (var expected in new[]
                 {
                     "MetaCloudApi:BaseUri", "MetaCloudApi:PhoneNumberId", "MetaCloudApi:AccessToken",
                     "WhatsApp:Templates entry 'tracking-link'", "AzureCommunicationServices:Endpoint",
                     "AzureCommunicationServices:SenderAddress", "Email:Templates entry 'tracking-link'",
                 })
        {
            Assert.Contains(expected, failure.Message, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("not-numeric-secretish", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("x@y.test", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Key_vault_mappings_feed_the_meta_token_and_phone_number_id_and_the_worker_can_send()
    {
        var environment = new Dictionary<string, string?>
        {
            ["KeyVaultSecrets:VaultUri"] = "https://kv-paquetenvia-test.vault.azure.net/",
            ["KeyVaultSecrets:Mappings:0:SecretName"] = "whatsapp-cloud-api-token",
            ["KeyVaultSecrets:Mappings:0:ConfigurationKey"] = "Messaging:WhatsApp:MetaCloudApi:AccessToken",
            ["KeyVaultSecrets:Mappings:1:SecretName"] = "whatsapp-phone-number-id",
            ["KeyVaultSecrets:Mappings:1:ConfigurationKey"] = "Messaging:WhatsApp:MetaCloudApi:PhoneNumberId",
            ["Messaging:WhatsApp:Provider"] = "MetaCloudApi",
            ["Messaging:WhatsApp:Templates:tracking-link:Name"] = "owner_tracking_link",
            ["Messaging:WhatsApp:Templates:tracking-link:LanguageCode"] = "es_MX",
            ["Messaging:WhatsApp:Templates:tracking-link:ParameterCount"] = "2",
        };
        var reader = new FakeSecretReader
        {
            ["whatsapp-cloud-api-token"] = AccessToken,
            ["whatsapp-phone-number-id"] = PhoneNumberId,
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(environment)
            .AddPaqueteriaKeyVaultSecrets((_, _) => reader)
            .Build();

        var options = new MessagingOptions();
        configuration.GetSection(MessagingOptions.SectionName).Bind(options);

        Assert.Equal(AccessToken, options.WhatsApp.MetaCloudApi.AccessToken);
        Assert.Equal(PhoneNumberId, options.WhatsApp.MetaCloudApi.PhoneNumberId);
        Assert.Empty(MessagingOptionsValidator.Validate(options, syntheticAllowed: false));
        Assert.Equal(["whatsapp-cloud-api-token", "whatsapp-phone-number-id"], reader.Requested);
    }

    // ------------------------------------------------------------------ Outbox outcome mapping

    [Theory]
    [InlineData(MessagingOutcome.Accepted, "SUCCESS", false)]
    [InlineData(MessagingOutcome.TransientFailure, "TRANSIENT", true)]
    [InlineData(MessagingOutcome.PermanentFailure, "PERMANENT", false)]
    [InlineData(MessagingOutcome.AmbiguousTimeout, "AMBIGUOUS", true)]
    public void Every_messaging_outcome_maps_onto_the_outbox_outcome_vocabulary(MessagingOutcome outcome, string expected, bool retried)
    {
        Assert.Equal(expected, NotificationDeliveryOutcome.From(outcome));
        Assert.Equal(retried, NotificationDeliveryOutcome.IsRetried(expected));
    }

    [Fact]
    public void Retry_after_raises_the_backoff_but_never_beyond_the_configured_maximum()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), NotificationRetryPolicy.CalculateDelay(1, 2, 60, null));
        Assert.Equal(TimeSpan.FromSeconds(30), NotificationRetryPolicy.CalculateDelay(1, 2, 60, TimeSpan.FromSeconds(30)));
        Assert.Equal(TimeSpan.FromSeconds(8), NotificationRetryPolicy.CalculateDelay(3, 2, 60, TimeSpan.FromSeconds(1)));
        Assert.Equal(TimeSpan.FromSeconds(60), NotificationRetryPolicy.CalculateDelay(1, 2, 60, TimeSpan.FromHours(1)));
    }

    [Fact]
    public void Result_codes_are_a_closed_low_cardinality_set()
    {
        var declared = typeof(MessagingResultCodes)
            .GetFields()
            .Where(field => field.IsLiteral)
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);

        Assert.True(declared.SetEquals(MessagingResultCodes.All));
        Assert.All(declared, code => Assert.Matches("^MESSAGING_[A-Z_]+$", code));
    }

    // ------------------------------------------------------------------ helpers

    private static MessagingRequest WhatsAppRequest() => new(
        MessageId,
        MessagingChannel.WhatsApp,
        new MessagingRecipient(Phone),
        "tracking-link",
        [ParameterText, "ORD_1234567890123456789012"]);

    private static MessagingRequest EmailRequest() => new(
        MessageId,
        MessagingChannel.Email,
        new MessagingRecipient(EmailAddress),
        "tracking-link",
        [ParameterText, "ORD_1234567890123456789012"]);

    private static MessagingOptions ProductionOptions(int timeoutSeconds = 10) => new()
    {
        WhatsApp = new()
        {
            Provider = WhatsAppProviderKind.MetaCloudApi,
            MetaCloudApi = new() { AccessToken = AccessToken, PhoneNumberId = PhoneNumberId, TimeoutSeconds = timeoutSeconds },
            Templates = new(StringComparer.Ordinal)
            {
                ["tracking-link"] = new() { Name = "owner_tracking_link", LanguageCode = "es_MX", ParameterCount = 2 },
            },
        },
        Email = new()
        {
            Provider = EmailProviderKind.AzureCommunicationServices,
            AzureCommunicationServices = new()
            {
                Endpoint = "https://pv-pilot.unitedstates.communication.azure.com",
                SenderAddress = "DoNotReply@notificaciones.example.test",
            },
            Templates = new(StringComparer.Ordinal)
            {
                ["tracking-link"] = new() { Subject = "Asunto {{1}}", PlainText = "Hola {{1}}, pedido {{2}}.", ParameterCount = 2 },
            },
        },
    };

    private static MetaWhatsAppCloudApiProvider WhatsApp(FakeHandler handler, out RecordingLogger<MetaWhatsAppCloudApiProvider> logs, int timeoutSeconds = 10)
    {
        var options = ProductionOptions(timeoutSeconds);
        Assert.Empty(MessagingOptionsValidator.Validate(options, syntheticAllowed: false));
        logs = new RecordingLogger<MetaWhatsAppCloudApiProvider>();
        return new MetaWhatsAppCloudApiProvider(new HandlerClientFactory(handler), Options.Create(options), TimeProvider.System, logs);
    }

    private static AzureCommunicationEmailProvider Email(FakeHandler handler, FakeCredential credential, out RecordingLogger<AzureCommunicationEmailProvider> logs)
    {
        logs = new RecordingLogger<AzureCommunicationEmailProvider>();
        return new AzureCommunicationEmailProvider(new HandlerClientFactory(handler), credential, Options.Create(ProductionOptions()), TimeProvider.System, logs);
    }

    private static Dictionary<string, string?> SyntheticSettings() => new()
    {
        ["Messaging:WhatsApp:Provider"] = "Synthetic",
        ["Messaging:WhatsApp:Templates:tracking-link:Name"] = "owner_tracking_link",
        ["Messaging:WhatsApp:Templates:tracking-link:LanguageCode"] = "es_MX",
        ["Messaging:WhatsApp:Templates:tracking-link:ParameterCount"] = "2",
        ["Messaging:Email:Provider"] = "Synthetic",
        ["Messaging:Email:Templates:tracking-link:Subject"] = "S {{1}}",
        ["Messaging:Email:Templates:tracking-link:PlainText"] = "B {{1}} {{2}}",
        ["Messaging:Email:Templates:tracking-link:ParameterCount"] = "2",
    };

    private static ServiceProvider BuildServices(Dictionary<string, string?> settings, string environmentName)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TokenCredential>(new FakeCredential());
        services.AddPaqueteriaMessaging(configuration, new FakeEnvironment(environmentName));
        return services.BuildServiceProvider();
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed record CapturedRequest(HttpMethod Method, Uri Uri, string? Authorization, string? OperationId, string Body);

    private sealed class HandlerClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public int Created { get; private set; }

        public HttpClient CreateClient(string name)
        {
            Created++;
            return new HttpClient(handler, disposeHandler: false);
        }
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;

        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
            _respond = (request, _) => Task.FromResult(respond(request));

        public FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) => _respond = respond;

        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await _respond(request, cancellationToken);
            Requests.Add(new(
                request.Method,
                request.RequestUri!,
                request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("Operation-Id", out var values) ? values.Single() : null,
                request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));
            return response;
        }
    }

    private sealed class FakeCredential : TokenCredential
    {
        public int Calls { get; private set; }

        public List<string[]> Scopes { get; } = [];

        public Exception? Failure { get; init; }

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            GetTokenAsync(requestContext, cancellationToken).AsTask().GetAwaiter().GetResult();

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            if (Failure is not null)
            {
                throw Failure;
            }

            Calls++;
            Scopes.Add(requestContext.Scopes);
            return ValueTask.FromResult(new AccessToken($"synthetic-entra-token-{Calls}", DateTimeOffset.UtcNow.AddHours(1)));
        }
    }

    private sealed class FakeSecretReader : Dictionary<string, string>, IKeyVaultSecretReader
    {
        public List<string> Requested { get; } = [];

        public Task<string> GetSecretValueAsync(string secretName, CancellationToken cancellationToken)
        {
            Requested.Add(secretName);
            return Task.FromResult(this[secretName]);
        }
    }

    private sealed class FakeEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Paqueteria.UnitTests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var values = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? string.Join('|', pairs.Select(pair => $"{pair.Key}={pair.Value}"))
                : string.Empty;
            Entries.Add(formatter(state, exception) + "|" + values + "|" + exception);
        }
    }
}
