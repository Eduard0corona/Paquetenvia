using System.Net;
using System.Text;
using System.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Paqueteria.Application.Voice;
using Paqueteria.Infrastructure.Security.Pii;
using Paqueteria.Infrastructure.Voice;

namespace Paqueteria.UnitTests.Voice;

/// <summary>
/// VOICE-001-PROVIDER-TWILIO-2026-10-11: the Twilio Programmable Voice adapter driven through a fake
/// <see cref="HttpMessageHandler"/> (no test reaches Twilio), the TwiML, the webhook signature, the options and the
/// registration. Every number used here is synthetic.
/// </summary>
public sealed class Voice001TwilioBridgeTests
{
    // Synthetic identifiers are assembled at run time: a literal "AC" or "SK" followed by 32 hexadecimal characters is
    // what secret scanning (rightly) treats as a real Twilio credential.
    private static readonly string AccountSid = "AC" + new string('0', 31) + "1";
    private const string AuthToken = "SyntheticTwilioAuthToken0001";
    private static readonly string ApiKeySid = "SK" + new string('0', 31) + "2";
    private const string ApiKeySecret = "SyntheticTwilioApiKeySecret0001";
    private const string CompanyNumber = "+525512340000";
    private const string DriverDigits = "5511112222";
    private const string RecipientDigits = "3312345678";
    private const string WebhookBaseUri = "https://pilot.example.test";
    private const string CallSid = "CAfedcba9876543210fedcba9876543210";
    private static readonly Guid CallRequestId = Guid.Parse("8b0e3f7a-0000-4000-8000-000000000001");
    private static readonly Guid OrganizationId = Guid.Parse("8b0e3f7a-0000-4000-8000-000000000002");

    // ------------------------------------------------------------------ REST create call

    [Fact]
    public async Task Twilio_calls_the_driver_from_the_company_number_and_bridges_the_recipient_with_inline_twiml()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.Created, $$"""{"sid":"{{CallSid}}","status":"queued"}"""));
        var provider = Twilio(handler, out _);

        var result = await provider.PlaceCallAsync(Request(), default);

        Assert.Equal(new VoiceBridgeResult(VoiceBridgeOutcome.Placed, VoiceBridgeResultCodes.Placed, CallSid), result);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal($"https://api.twilio.com/2010-04-01/Accounts/{AccountSid}/Calls.json", request.Uri.ToString());
        Assert.Equal(
            "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{AccountSid}:{AuthToken}")),
            request.Authorization);
        Assert.Equal("application/x-www-form-urlencoded", request.ContentType);

        var form = HttpUtility.ParseQueryString(request.Body);
        Assert.Equal(
            ["From", "Record", "StatusCallback", "StatusCallbackMethod", "TimeLimit", "Timeout", "To", "Twiml"],
            form.AllKeys.Order(StringComparer.Ordinal));
        Assert.Equal("+52" + DriverDigits, form["To"]);
        Assert.Equal(CompanyNumber, form["From"]);
        Assert.Equal("25", form["Timeout"]);
        Assert.Equal("385", form["TimeLimit"]);
        Assert.Equal("false", form["Record"]);
        Assert.Equal(
            $"{WebhookBaseUri}/api/v1/voice/twilio/call-status?o={OrganizationId:D}&r={CallRequestId:D}",
            form["StatusCallback"]);
        Assert.Equal("POST", form["StatusCallbackMethod"]);
        Assert.Equal(
            "<Response>" +
            "<Say language=\"es-MX\" voice=\"Polly.Mia\">Te comunicamos con el destinatario de tu entrega. Espera en la línea.</Say>" +
            "<Dial callerId=\"+525512340000\" timeout=\"30\" timeLimit=\"300\" record=\"do-not-record\">" +
            "<Number>+523312345678</Number></Dial>" +
            "<Say language=\"es-MX\" voice=\"Polly.Mia\">La llamada con el destinatario terminó.</Say>" +
            "</Response>",
            form["Twiml"]);

        // Each number travels exactly where it is needed: the driver only as the called party, the recipient only
        // inside <Number>, never in the callback URL (which only names the tenant and the call request).
        Assert.DoesNotContain(DriverDigits, form["Twiml"], StringComparison.Ordinal);
        Assert.DoesNotContain(DriverDigits, form["StatusCallback"], StringComparison.Ordinal);
        Assert.DoesNotContain(RecipientDigits, form["StatusCallback"], StringComparison.Ordinal);
        Assert.Equal(1, Occurrences(request.Body, RecipientDigits));
        Assert.Equal(1, Occurrences(request.Body, DriverDigits));
    }

    [Fact]
    public async Task An_api_key_replaces_the_auth_token_for_rest_but_the_account_stays_in_the_url()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.Created, $$"""{"sid":"{{CallSid}}"}"""));
        var options = LiveOptions();
        options.Twilio.ApiKeySid = ApiKeySid;
        options.Twilio.ApiKeySecret = ApiKeySecret;
        var provider = Twilio(handler, out _, options);

        Assert.Equal(VoiceBridgeOutcome.Placed, (await provider.PlaceCallAsync(Request(), default)).Outcome);
        var request = Assert.Single(handler.Requests);
        Assert.Equal($"https://api.twilio.com/2010-04-01/Accounts/{AccountSid}/Calls.json", request.Uri.ToString());
        Assert.Equal(
            "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{ApiKeySid}:{ApiKeySecret}")),
            request.Authorization);
    }

    [Theory]
    [InlineData(21211, HttpStatusCode.BadRequest, VoiceBridgeOutcome.PermanentFailure, VoiceBridgeResultCodes.DriverPhoneRejected)]
    [InlineData(21214, HttpStatusCode.BadRequest, VoiceBridgeOutcome.PermanentFailure, VoiceBridgeResultCodes.DriverPhoneRejected)]
    [InlineData(21217, HttpStatusCode.BadRequest, VoiceBridgeOutcome.PermanentFailure, VoiceBridgeResultCodes.DriverPhoneRejected)]
    [InlineData(21401, HttpStatusCode.BadRequest, VoiceBridgeOutcome.PermanentFailure, VoiceBridgeResultCodes.DriverPhoneRejected)]
    [InlineData(21215, HttpStatusCode.BadRequest, VoiceBridgeOutcome.PermanentFailure, VoiceBridgeResultCodes.DestinationNotAllowed)]
    [InlineData(21216, HttpStatusCode.BadRequest, VoiceBridgeOutcome.PermanentFailure, VoiceBridgeResultCodes.DestinationNotAllowed)]
    [InlineData(21210, HttpStatusCode.BadRequest, VoiceBridgeOutcome.PermanentFailure, VoiceBridgeResultCodes.CallerIdRejected)]
    [InlineData(21212, HttpStatusCode.BadRequest, VoiceBridgeOutcome.PermanentFailure, VoiceBridgeResultCodes.CallerIdRejected)]
    [InlineData(21213, HttpStatusCode.BadRequest, VoiceBridgeOutcome.PermanentFailure, VoiceBridgeResultCodes.CallerIdRejected)]
    [InlineData(21219, HttpStatusCode.BadRequest, VoiceBridgeOutcome.PermanentFailure, VoiceBridgeResultCodes.TrialRestricted)]
    [InlineData(20003, HttpStatusCode.Unauthorized, VoiceBridgeOutcome.PermanentFailure, VoiceBridgeResultCodes.AuthenticationFailed)]
    [InlineData(20005, HttpStatusCode.Forbidden, VoiceBridgeOutcome.PermanentFailure, VoiceBridgeResultCodes.AuthenticationFailed)]
    [InlineData(20404, HttpStatusCode.NotFound, VoiceBridgeOutcome.PermanentFailure, VoiceBridgeResultCodes.AuthenticationFailed)]
    [InlineData(20429, HttpStatusCode.TooManyRequests, VoiceBridgeOutcome.TransientFailure, VoiceBridgeResultCodes.RateLimited)]
    [InlineData(99999, HttpStatusCode.ServiceUnavailable, VoiceBridgeOutcome.TransientFailure, VoiceBridgeResultCodes.Unavailable)]
    [InlineData(99999, HttpStatusCode.BadRequest, VoiceBridgeOutcome.PermanentFailure, VoiceBridgeResultCodes.Rejected)]
    public async Task Twilio_error_codes_are_classified_without_reading_the_message(
        int code,
        HttpStatusCode status,
        VoiceBridgeOutcome outcome,
        string expectedCode)
    {
        // Twilio's error message can echo the number: it must never reach the result or the logs.
        var handler = new FakeHandler(_ => Json(
            status,
            $$"""{"code":{{code}},"message":"The 'To' number +52{{DriverDigits}} is not valid.","more_info":"https://www.twilio.com/docs/errors/{{code}}","status":{{(int)status}}}"""));
        var provider = Twilio(handler, out var logs);

        var result = await provider.PlaceCallAsync(Request(), default);

        Assert.Equal(new VoiceBridgeResult(outcome, expectedCode), result);
        Assert.Contains(expectedCode, VoiceBridgeResultCodes.All);
        Assert.All(logs.Entries, entry => Assert.DoesNotContain(DriverDigits, entry, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, VoiceBridgeOutcome.PermanentFailure, VoiceBridgeResultCodes.AuthenticationFailed)]
    [InlineData(HttpStatusCode.Forbidden, VoiceBridgeOutcome.PermanentFailure, VoiceBridgeResultCodes.AuthenticationFailed)]
    [InlineData(HttpStatusCode.TooManyRequests, VoiceBridgeOutcome.TransientFailure, VoiceBridgeResultCodes.RateLimited)]
    [InlineData(HttpStatusCode.RequestTimeout, VoiceBridgeOutcome.Ambiguous, VoiceBridgeResultCodes.Timeout)]
    [InlineData(HttpStatusCode.GatewayTimeout, VoiceBridgeOutcome.Ambiguous, VoiceBridgeResultCodes.Timeout)]
    [InlineData(HttpStatusCode.InternalServerError, VoiceBridgeOutcome.TransientFailure, VoiceBridgeResultCodes.Unavailable)]
    [InlineData(HttpStatusCode.BadGateway, VoiceBridgeOutcome.TransientFailure, VoiceBridgeResultCodes.Unavailable)]
    [InlineData(HttpStatusCode.BadRequest, VoiceBridgeOutcome.PermanentFailure, VoiceBridgeResultCodes.Rejected)]
    [InlineData(HttpStatusCode.Conflict, VoiceBridgeOutcome.PermanentFailure, VoiceBridgeResultCodes.Rejected)]
    public async Task Without_a_twilio_error_body_the_http_status_decides(
        HttpStatusCode status,
        VoiceBridgeOutcome outcome,
        string expectedCode)
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(status) { Content = new StringContent("<html>gateway</html>") });

        var result = await Twilio(handler, out _).PlaceCallAsync(Request(), default);

        Assert.Equal(new VoiceBridgeResult(outcome, expectedCode), result);
    }

    [Theory]
    [InlineData("""{"status":"queued"}""")]
    [InlineData("""{"sid":"SM0123456789abcdef0123456789abcdef"}""")]
    [InlineData("""{"sid":"CA0123"}""")]
    [InlineData("not json")]
    [InlineData("")]
    public async Task A_success_without_a_valid_call_sid_is_ambiguous_never_placed(string body)
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });

        var result = await Twilio(handler, out _).PlaceCallAsync(Request(), default);

        Assert.Equal(new VoiceBridgeResult(VoiceBridgeOutcome.Ambiguous, VoiceBridgeResultCodes.ResponseInvalid), result);
    }

    [Fact]
    public async Task A_timeout_after_the_request_left_is_ambiguous_and_caller_cancellation_propagates()
    {
        var slow = new FakeHandler(async (_, token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), token);
            return Json(HttpStatusCode.Created, $$"""{"sid":"{{CallSid}}"}""");
        });
        var options = LiveOptions();
        options.Twilio.TimeoutSeconds = 1;

        var timedOut = await Twilio(slow, out _, options).PlaceCallAsync(Request(), default);
        Assert.Equal(new VoiceBridgeResult(VoiceBridgeOutcome.Ambiguous, VoiceBridgeResultCodes.Timeout), timedOut);

        using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await Twilio(slow, out _).PlaceCallAsync(Request(), cancelled.Token));
    }

    [Theory]
    [InlineData(HttpRequestError.ConnectionError, VoiceBridgeOutcome.TransientFailure, VoiceBridgeResultCodes.Unreachable)]
    [InlineData(HttpRequestError.NameResolutionError, VoiceBridgeOutcome.TransientFailure, VoiceBridgeResultCodes.Unreachable)]
    [InlineData(HttpRequestError.SecureConnectionError, VoiceBridgeOutcome.TransientFailure, VoiceBridgeResultCodes.Unreachable)]
    [InlineData(HttpRequestError.ResponseEnded, VoiceBridgeOutcome.Ambiguous, VoiceBridgeResultCodes.Timeout)]
    public async Task Transport_failures_are_unreachable_before_sending_and_ambiguous_after(
        HttpRequestError error,
        VoiceBridgeOutcome outcome,
        string expectedCode)
    {
        var handler = new FakeHandler(_ => throw new HttpRequestException(error, "synthetic"));

        var result = await Twilio(handler, out _).PlaceCallAsync(Request(), default);

        Assert.Equal(new VoiceBridgeResult(outcome, expectedCode), result);
    }

    [Fact]
    public async Task Synthetic_or_equal_numbers_and_a_missing_company_number_never_reach_twilio()
    {
        var handler = new FakeHandler(_ => throw new InvalidOperationException("must not be called"));
        var provider = Twilio(handler, out _);

        Assert.True(VoicePhoneNumber.TryParseE164("+520000000001", out var placeholder));
        Assert.False(placeholder.IsDialable);
        var invalid = new VoiceBridgeResult(VoiceBridgeOutcome.PermanentFailure, VoiceBridgeResultCodes.PhoneInvalid);
        Assert.Equal(invalid, await provider.PlaceCallAsync(Request(driver: placeholder), default));
        Assert.Equal(invalid, await provider.PlaceCallAsync(Request(recipient: placeholder), default));
        Assert.Equal(invalid, await provider.PlaceCallAsync(Request(recipient: Number(DriverDigits)), default));
        Assert.Equal(invalid, await provider.PlaceCallAsync(Request(callRequestId: Guid.Empty), default));

        var withoutCompany = LiveOptions();
        withoutCompany.Twilio.CompanyNumber = string.Empty;
        Assert.Equal(invalid, await Twilio(handler, out _, withoutCompany).PlaceCallAsync(Request(), default));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task The_circuit_opens_after_consecutive_provider_failures_and_permanent_answers_do_not_count()
    {
        var calls = 0;
        var handler = new FakeHandler(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });
        var options = LiveOptions();
        options.Twilio.CircuitBreakerFailureThreshold = 2;
        options.Twilio.CircuitBreakerBreakSeconds = 600;
        var provider = Twilio(handler, out _, options);

        Assert.Equal(VoiceBridgeResultCodes.Unavailable, (await provider.PlaceCallAsync(Request(), default)).Code);
        Assert.Equal(VoiceBridgeResultCodes.Unavailable, (await provider.PlaceCallAsync(Request(), default)).Code);
        Assert.Equal(
            new VoiceBridgeResult(VoiceBridgeOutcome.TransientFailure, VoiceBridgeResultCodes.CircuitOpen),
            await provider.PlaceCallAsync(Request(), default));
        Assert.Equal(2, calls);

        var rejecting = Twilio(new FakeHandler(_ => Json(HttpStatusCode.BadRequest, """{"code":21211}""")), out _, options);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            Assert.Equal(VoiceBridgeResultCodes.DriverPhoneRejected, (await rejecting.PlaceCallAsync(Request(), default)).Code);
        }
    }

    [Fact]
    public async Task Logs_carry_only_provider_outcome_code_and_status()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.Created, $$"""{"sid":"{{CallSid}}","to":"+52{{DriverDigits}}","from":"{{CompanyNumber}}"}"""));
        var provider = Twilio(handler, out var logs);

        await provider.PlaceCallAsync(Request(), default);

        var entry = Assert.Single(logs.Entries);
        Assert.Contains("Provider=TWILIO", entry, StringComparison.Ordinal);
        Assert.Contains("Outcome=Placed", entry, StringComparison.Ordinal);
        Assert.Contains("Code=VOICE_CALL_PLACED", entry, StringComparison.Ordinal);
        Assert.Contains("Status=201", entry, StringComparison.Ordinal);
        foreach (var secret in new[] { DriverDigits, RecipientDigits, "5512340000", AuthToken, AccountSid, CallSid, OrganizationId.ToString() })
        {
            Assert.DoesNotContain(secret, entry, StringComparison.Ordinal);
        }
    }

    // ------------------------------------------------------------------ redaction

    [Fact]
    public void Numbers_and_requests_are_redacted_in_their_text_form()
    {
        var driver = Number(DriverDigits);
        var request = Request();

        Assert.Equal("[redacted]", driver.ToString());
        Assert.Equal("[redacted]", $"{driver}");
        var text = request.ToString();
        Assert.DoesNotContain(DriverDigits, text, StringComparison.Ordinal);
        Assert.DoesNotContain(RecipientDigits, text, StringComparison.Ordinal);
        Assert.Contains(CallRequestId.ToString("D"), text, StringComparison.Ordinal);
        Assert.Equal(
            "VoiceWebhookRequest { [redacted] }",
            new VoiceWebhookRequest("/x", "sig", [new("From", "+52" + RecipientDigits)]).ToString());
    }

    [Theory]
    [InlineData("+525511112222", true, true)]
    [InlineData("+523312345678", true, true)]
    [InlineData("+520000000001", true, false)]
    [InlineData("+521234567890", true, false)]
    [InlineData("525511112222", false, false)]
    [InlineData("+5255111122223", false, false)]
    [InlineData("+15511112222", false, false)]
    [InlineData("+52 5511112222", false, false)]
    [InlineData("", false, false)]
    [InlineData(null, false, false)]
    public void Only_plus_52_and_ten_digits_parse_and_only_2_to_9_dial(string? value, bool parses, bool dialable)
    {
        Assert.Equal(parses, VoicePhoneNumber.TryParseE164(value, out var number));
        if (parses)
        {
            Assert.Equal(dialable, number.IsDialable);
            Assert.Equal(value, number.E164);
        }
    }

    [Fact]
    public void National_digits_become_plus_52()
    {
        Assert.True(VoicePhoneNumber.TryFromNationalDigits("5511112222", out var number));
        Assert.Equal("+525511112222", number.E164);
        Assert.False(VoicePhoneNumber.TryFromNationalDigits("551111222", out _));
        Assert.False(VoicePhoneNumber.TryFromNationalDigits("+525511112222", out _));
        Assert.False(VoicePhoneNumber.TryFromNationalDigits(null, out _));
    }

    [Fact]
    public void Result_codes_are_a_closed_low_cardinality_set()
    {
        var declared = typeof(VoiceBridgeResultCodes)
            .GetFields()
            .Where(field => field.IsLiteral)
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);

        Assert.True(declared.SetEquals(VoiceBridgeResultCodes.All));
        Assert.All(declared, code => Assert.Matches("^VOICE_[A-Z_]+$", code));
    }

    // ------------------------------------------------------------------ webhooks

    [Fact]
    public void The_signature_matches_the_twilio_documented_example()
    {
        // https://www.twilio.com/docs/usage/security#validating-requests (documented example).
        var signature = TwilioVoiceWebhookVerifier.ComputeSignature(
            "12345",
            "https://mycompany.com/myapp.php?foo=1&bar=2",
            [
                new("CallSid", "CA1234567890ABCDE"),
                new("Caller", "+12349013030"),
                new("Digits", "1234"),
                new("From", "+12349013030"),
                new("To", "+18005551212"),
            ]);

        Assert.Equal("0/KCTR6DLpKmkAf8muzZqo1nDgQ=", signature);
    }

    [Fact]
    public void Webhooks_need_the_signature_over_the_configured_url_and_the_configured_account()
    {
        var verifier = new TwilioVoiceWebhookVerifier(Options.Create(LiveOptions()));
        var pathAndQuery = VoiceWebhookPaths.CallStatusPathAndQuery(OrganizationId, CallRequestId);
        KeyValuePair<string, string>[] form =
        [
            new("AccountSid", AccountSid),
            new("CallSid", CallSid),
            new("CallStatus", "completed"),
            new("CallDuration", "42"),
            new("From", CompanyNumber),
            new("To", "+52" + DriverDigits),
        ];
        var signature = TwilioVoiceWebhookVerifier.ComputeSignature(AuthToken, WebhookBaseUri + pathAndQuery, form);

        Assert.Equal(VoiceWebhookVerdict.Verified, verifier.Verify(new(pathAndQuery, signature, form)));
        // The form order does not matter: Twilio sorts the names.
        Assert.Equal(VoiceWebhookVerdict.Verified, verifier.Verify(new(pathAndQuery, signature, form.Reverse().ToArray())));

        // Anything else is rejected: no signature, a wrong one, another URL or query, a changed field, another account.
        Assert.Equal(VoiceWebhookVerdict.Rejected, verifier.Verify(new(pathAndQuery, null, form)));
        Assert.Equal(VoiceWebhookVerdict.Rejected, verifier.Verify(new(pathAndQuery, "", form)));
        Assert.Equal(VoiceWebhookVerdict.Rejected, verifier.Verify(new(pathAndQuery, signature[..^2] + "A=", form)));
        Assert.Equal(VoiceWebhookVerdict.Rejected, verifier.Verify(new(
            VoiceWebhookPaths.CallStatusPathAndQuery(OrganizationId, Guid.NewGuid()), signature, form)));
        Assert.Equal(VoiceWebhookVerdict.Rejected, verifier.Verify(new("https://evil.example.test" + pathAndQuery, signature, form)));
        Assert.Equal(VoiceWebhookVerdict.Rejected, verifier.Verify(new(
            pathAndQuery,
            signature,
            form.Select(field => field.Key == "CallDuration" ? new KeyValuePair<string, string>("CallDuration", "4200") : field).ToArray())));
        Assert.Equal(VoiceWebhookVerdict.Rejected, verifier.Verify(new(
            pathAndQuery,
            signature,
            form.Append(new("Extra", "1")).ToArray())));

        KeyValuePair<string, string>[] otherAccount =
        [
            .. form.Where(field => field.Key != "AccountSid"),
            new("AccountSid", "AC" + new string('f', 32)),
        ];
        Assert.Equal(VoiceWebhookVerdict.Rejected, verifier.Verify(new(
            pathAndQuery,
            TwilioVoiceWebhookVerifier.ComputeSignature(AuthToken, WebhookBaseUri + pathAndQuery, otherAccount),
            otherAccount)));
        KeyValuePair<string, string>[] withoutAccount = [.. form.Where(field => field.Key != "AccountSid")];
        Assert.Equal(VoiceWebhookVerdict.Rejected, verifier.Verify(new(
            pathAndQuery,
            TwilioVoiceWebhookVerifier.ComputeSignature(AuthToken, WebhookBaseUri + pathAndQuery, withoutAccount),
            withoutAccount)));
        // Signed with another token.
        Assert.Equal(VoiceWebhookVerdict.Rejected, verifier.Verify(new(
            pathAndQuery,
            TwilioVoiceWebhookVerifier.ComputeSignature("AnotherSyntheticToken0001", WebhookBaseUri + pathAndQuery, form),
            form)));
    }

    [Fact]
    public void Without_the_live_provider_webhooks_are_not_configured()
    {
        foreach (var kind in new[] { VoiceBridgeProviderKind.Disabled, VoiceBridgeProviderKind.Synthetic })
        {
            var options = LiveOptions();
            options.Provider = kind;
            var verifier = new TwilioVoiceWebhookVerifier(Options.Create(options));
            var pathAndQuery = VoiceWebhookPaths.CallStatusPathAndQuery(OrganizationId, CallRequestId);
            KeyValuePair<string, string>[] form = [new("AccountSid", AccountSid)];
            Assert.Equal(
                VoiceWebhookVerdict.NotConfigured,
                verifier.Verify(new(
                    pathAndQuery,
                    TwilioVoiceWebhookVerifier.ComputeSignature(AuthToken, WebhookBaseUri + pathAndQuery, form),
                    form)));
        }
    }

    [Fact]
    public void A_call_to_the_company_number_hears_a_fixed_message_or_reaches_the_configured_dispatch_line()
    {
        var options = LiveOptions();
        var message = new TwilioVoiceWebhookVerifier(Options.Create(options)).BuildInboundCallResponse();
        Assert.Equal(
            "<Response><Say language=\"es-MX\" voice=\"Polly.Mia\">" + TwilioTwiml.InboundMessage + "</Say><Hangup /></Response>",
            message);
        Assert.DoesNotContain("<Dial", message, StringComparison.Ordinal);

        options.Twilio.InboundForwardNumber = "+528112345678";
        var forward = new TwilioVoiceWebhookVerifier(Options.Create(options)).BuildInboundCallResponse();
        Assert.Equal(
            "<Response><Say language=\"es-MX\" voice=\"Polly.Mia\">" + TwilioTwiml.InboundForwardGreeting + "</Say>" +
            "<Dial callerId=\"+525512340000\" timeout=\"30\" timeLimit=\"300\" record=\"do-not-record\">" +
            "<Number>+528112345678</Number></Dial></Response>",
            forward);
    }

    [Fact]
    public void Spoken_messages_are_fixed_spanish_without_personal_or_order_data()
    {
        foreach (var text in new[]
                 {
                     TwilioTwiml.BridgeGreeting, TwilioTwiml.BridgeEnded, TwilioTwiml.InboundMessage,
                     TwilioTwiml.InboundForwardGreeting,
                 })
        {
            Assert.DoesNotMatch(@"\d", text);
            Assert.DoesNotContain("{", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Paquetenvia", text, StringComparison.OrdinalIgnoreCase);
        }

        var twiml = TwilioTwiml.Bridge(LiveOptions().Twilio, Number("5512340000"), Number(RecipientDigits));
        Assert.Contains("record=\"do-not-record\"", twiml, StringComparison.Ordinal);
        Assert.DoesNotContain("<Record", twiml, StringComparison.Ordinal);
        Assert.DoesNotContain("<Gather", twiml, StringComparison.Ordinal);
        Assert.DoesNotContain("url=", twiml, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ synthetic and disabled

    [Theory]
    [InlineData(VoiceBridgeOutcome.Placed, VoiceBridgeResultCodes.SyntheticPlaced)]
    [InlineData(VoiceBridgeOutcome.TransientFailure, VoiceBridgeResultCodes.SyntheticTransient)]
    [InlineData(VoiceBridgeOutcome.PermanentFailure, VoiceBridgeResultCodes.SyntheticPermanent)]
    [InlineData(VoiceBridgeOutcome.Ambiguous, VoiceBridgeResultCodes.SyntheticAmbiguous)]
    public async Task The_synthetic_provider_is_deterministic_and_accepts_placeholders(VoiceBridgeOutcome outcome, string code)
    {
        var provider = new SyntheticVoiceBridgeProvider(Options.Create(new VoiceBridgeOptions
        {
            Provider = VoiceBridgeProviderKind.Synthetic,
            SyntheticOutcome = outcome,
        }));
        Assert.True(VoicePhoneNumber.TryParseE164("+520000000001", out var driver));
        Assert.True(VoicePhoneNumber.TryParseE164("+520000000002", out var recipient));

        var first = await provider.PlaceCallAsync(new(CallRequestId, OrganizationId, driver, recipient), default);
        var second = await provider.PlaceCallAsync(new(CallRequestId, OrganizationId, driver, recipient), default);

        Assert.Equal(first, second);
        Assert.Equal(outcome, first.Outcome);
        Assert.Equal(code, first.Code);
        if (outcome == VoiceBridgeOutcome.Placed)
        {
            Assert.Matches("^SY[0-9a-f]{32}$", first.ProviderCallId);
            Assert.DoesNotMatch(TwilioVoiceBridgeProvider.CallSidPattern(), first.ProviderCallId!);
        }
        else
        {
            Assert.Null(first.ProviderCallId);
        }

        Assert.Equal(
            new VoiceBridgeResult(VoiceBridgeOutcome.PermanentFailure, VoiceBridgeResultCodes.PhoneInvalid),
            await provider.PlaceCallAsync(new(CallRequestId, OrganizationId, driver, driver), default));
    }

    [Fact]
    public async Task By_default_the_bridge_is_disabled_and_places_nothing()
    {
        using var services = BuildServices(new Dictionary<string, string?>(), Environments.Production);

        var status = services.GetRequiredService<IVoiceBridgeStatus>();
        Assert.Equal(VoiceBridgeMode.Disabled, status.Mode);
        Assert.Equal("DISABLED", status.ProviderName);
        Assert.Equal(
            new VoiceBridgeResult(VoiceBridgeOutcome.PermanentFailure, VoiceBridgeResultCodes.Disabled),
            await services.GetRequiredService<IVoiceBridgeProvider>().PlaceCallAsync(Request(), default));
        Assert.Equal(
            VoiceWebhookVerdict.NotConfigured,
            services.GetRequiredService<IVoiceWebhookVerifier>().Verify(new("/api/v1/voice/twilio/inbound", "x", [])));
        // ADP-001 Key Vault protection is not demanded while the bridge is off.
        _ = services.GetRequiredService<IOptions<PiiProtectionOptions>>().Value;
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public async Task The_synthetic_provider_runs_only_where_data_is_synthetic(string environmentName)
    {
        var settings = new Dictionary<string, string?> { ["Voice:Provider"] = "Synthetic" };
        using var allowed = BuildServices(settings, environmentName);
        Assert.Equal(VoiceBridgeMode.Synthetic, allowed.GetRequiredService<IVoiceBridgeStatus>().Mode);
        Assert.Equal(
            VoiceBridgeResultCodes.SyntheticPlaced,
            (await allowed.GetRequiredService<IVoiceBridgeProvider>().PlaceCallAsync(Request(), default)).Code);

        using var production = BuildServices(settings, Environments.Production);
        var failure = Assert.Throws<OptionsValidationException>(() =>
            production.GetRequiredService<IOptions<VoiceBridgeOptions>>().Value);
        Assert.Contains("Voice:Provider=Synthetic is allowed only in", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    [InlineData("DevSynthetic")]
    public void Twilio_is_refused_where_data_is_synthetic(string environmentName)
    {
        using var services = BuildServices(LiveSettings(), environmentName);

        var failure = Assert.Throws<OptionsValidationException>(() =>
            services.GetRequiredService<IOptions<VoiceBridgeOptions>>().Value);
        Assert.Contains("Voice:Provider=Twilio is refused in Development, Testing and DEV_SYNTHETIC", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Twilio_in_production_needs_the_key_vault_envelope_and_never_logs_requests()
    {
        var withoutKeyVault = LiveSettings();
        withoutKeyVault.Remove("PiiProtection:AzureKeyVault:KeyId");
        using (var services = BuildServices(withoutKeyVault, Environments.Production))
        {
            Assert.Equal(VoiceBridgeMode.Live, services.GetRequiredService<IVoiceBridgeStatus>().Mode);
            Assert.Throws<OptionsValidationException>(() =>
                services.GetRequiredService<IOptions<PiiProtectionOptions>>().Value);
        }

        var handler = new FakeHandler(_ => Json(HttpStatusCode.Created, $$"""{"sid":"{{CallSid}}"}"""));
        var logs = new RecordingLoggerProvider();
        using var live = BuildServices(LiveSettings(), Environments.Production, services =>
        {
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
            services.AddHttpClient(TwilioVoiceBridgeProvider.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => handler);
        });
        Assert.Equal(VoiceBridgeMode.Live, live.GetRequiredService<IVoiceBridgeStatus>().Mode);
        Assert.Equal("TWILIO", live.GetRequiredService<IVoiceBridgeStatus>().ProviderName);
        _ = live.GetRequiredService<IOptions<PiiProtectionOptions>>().Value;

        var result = await live.GetRequiredService<IVoiceBridgeProvider>().PlaceCallAsync(Request(), default);

        Assert.Equal(VoiceBridgeOutcome.Placed, result.Outcome);
        Assert.Single(handler.Requests);
        // The HTTP client dropped the default request loggers: no URI (account), header or form reaches a log.
        Assert.DoesNotContain(logs.Entries, entry => entry.Contains("System.Net.Http.HttpClient", StringComparison.Ordinal));
        Assert.All(logs.Entries, entry =>
        {
            Assert.DoesNotContain(AccountSid, entry, StringComparison.Ordinal);
            Assert.DoesNotContain(DriverDigits, entry, StringComparison.Ordinal);
            Assert.DoesNotContain(RecipientDigits, entry, StringComparison.Ordinal);
        });
        Assert.Contains(logs.Entries, entry => entry.Contains("VOICE_CALL_PLACED", StringComparison.Ordinal));
    }

    [Fact]
    public void Validation_names_keys_never_values()
    {
        var options = LiveOptions();
        options.Twilio.AccountSid = "not-an-account-" + AuthToken;
        options.Twilio.AuthToken = "short";
        options.Twilio.ApiKeySid = ApiKeySid;
        options.Twilio.CompanyNumber = "+52" + DriverDigits[..9];
        options.Twilio.InboundForwardNumber = "+15005550006";
        options.Twilio.WebhookBaseUri = "http://pilot.example.test/hooks?x=" + RecipientDigits;
        options.Twilio.Gate007DecisionId = "PENDING";
        options.Twilio.SayVoice = "<Polly>";
        options.Twilio.TimeoutSeconds = 0;
        options.Twilio.MaxConcurrentRequests = 0;

        var failures = VoiceBridgeOptionsValidator.Validate(options, syntheticAllowed: false, liveAllowed: true);

        Assert.Equal(10, failures.Count);
        var text = string.Join('\n', failures);
        foreach (var value in new[] { AuthToken, "short", ApiKeySid, DriverDigits[..9], "5005550006", RecipientDigits, "PENDING", "<Polly>" })
        {
            Assert.DoesNotContain(value, text, StringComparison.Ordinal);
        }

        Assert.Empty(VoiceBridgeOptionsValidator.Validate(LiveOptions(), syntheticAllowed: false, liveAllowed: true));
        Assert.Empty(VoiceBridgeOptionsValidator.Validate(new VoiceBridgeOptions(), syntheticAllowed: false, liveAllowed: false));
        var gate = LiveOptions();
        gate.Twilio.Gate007DecisionId = string.Empty;
        Assert.Contains(
            VoiceBridgeOptionsValidator.Validate(gate, syntheticAllowed: false, liveAllowed: true),
            failure => failure.Contains("Gate007DecisionId", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ helpers

    private static VoicePhoneNumber Number(string digits)
    {
        Assert.True(VoicePhoneNumber.TryFromNationalDigits(digits, out var number));
        return number;
    }

    private static VoiceBridgeRequest Request(
        VoicePhoneNumber? driver = null,
        VoicePhoneNumber? recipient = null,
        Guid? callRequestId = null) =>
        new(callRequestId ?? CallRequestId, OrganizationId, driver ?? Number(DriverDigits), recipient ?? Number(RecipientDigits));

    private static VoiceBridgeOptions LiveOptions() => new()
    {
        Provider = VoiceBridgeProviderKind.Twilio,
        Twilio = new TwilioVoiceOptions
        {
            AccountSid = AccountSid,
            AuthToken = AuthToken,
            CompanyNumber = CompanyNumber,
            WebhookBaseUri = WebhookBaseUri,
            Gate007DecisionId = "GATE-007-SYNTHETIC-2026-10-11",
        },
    };

    private static Dictionary<string, string?> LiveSettings() => new()
    {
        ["Voice:Provider"] = "Twilio",
        ["Voice:Twilio:AccountSid"] = AccountSid,
        ["Voice:Twilio:AuthToken"] = AuthToken,
        ["Voice:Twilio:CompanyNumber"] = CompanyNumber,
        ["Voice:Twilio:WebhookBaseUri"] = WebhookBaseUri,
        ["Voice:Twilio:Gate007DecisionId"] = "GATE-007-SYNTHETIC-2026-10-11",
        ["PiiProtection:AzureKeyVault:KeyId"] = "https://pv-pilot-synthetic.vault.azure.net/keys/pii-envelope",
    };

    private static TwilioVoiceBridgeProvider Twilio(
        FakeHandler handler,
        out RecordingLogger<TwilioVoiceBridgeProvider> logs,
        VoiceBridgeOptions? options = null)
    {
        options ??= LiveOptions();
        Assert.DoesNotContain(
            VoiceBridgeOptionsValidator.Validate(options, syntheticAllowed: false, liveAllowed: true),
            failure => !failure.Contains("CompanyNumber", StringComparison.Ordinal));
        logs = new RecordingLogger<TwilioVoiceBridgeProvider>();
        return new TwilioVoiceBridgeProvider(new HandlerClientFactory(handler), Options.Create(options), TimeProvider.System, logs);
    }

    private static ServiceProvider BuildServices(
        Dictionary<string, string?> settings,
        string environmentName,
        Action<IServiceCollection>? configure = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPaqueteriaVoiceBridge(configuration, new FakeEnvironment(environmentName));
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    private static int Occurrences(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal);
             index >= 0;
             index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed record CapturedRequest(HttpMethod Method, Uri Uri, string? Authorization, string? ContentType, string Body);

    private sealed class HandlerClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
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
                request.Content?.Headers.ContentType?.MediaType,
                request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));
            return response;
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

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        public List<string> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, Entries);

        public void Dispose()
        {
        }

        private sealed class Logger(string category, List<string> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (entries)
                {
                    entries.Add(category + "|" + formatter(state, exception));
                }
            }
        }
    }
}
