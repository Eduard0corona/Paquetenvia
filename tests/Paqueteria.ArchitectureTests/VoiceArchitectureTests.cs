using Paqueteria.ArchitectureTests.Architecture;

namespace Paqueteria.ArchitectureTests;

/// <summary>
/// VOICE-001-MASKED-CALLS-2026-10-11 and VOICE-001-PROVIDER-TWILIO-2026-10-11: the Twilio adapter is the only voice
/// code that talks HTTP, its client never logs requests, no voice code logs a number, a credential, a form or a body,
/// nothing is recorded, Twilio Proxy is not used, and only the two signed webhooks are anonymous.
/// </summary>
public sealed class VoiceArchitectureTests
{
    private const string VoiceDirectory = "src/BuildingBlocks/Paqueteria.Infrastructure/Voice/";
    private const string DriversVoiceDirectory = "src/Modules/Drivers/Drivers.Infrastructure/Voice/";
    private const string DriversEndpointsDirectory = "src/Modules/Drivers/Drivers.Endpoints/";

    [Fact]
    public void The_twilio_http_client_drops_the_default_request_loggers()
    {
        var registration = File.ReadAllText(TestRepository.GetPath(VoiceDirectory + "VoiceServiceCollectionExtensions.cs"));
        var registrations = registration.Split("services.AddHttpClient(", StringSplitOptions.None).Skip(1).ToArray();

        var block = Assert.Single(registrations);
        Assert.Contains(".RemoveAllLoggers();", block, StringComparison.Ordinal);
        Assert.Contains("AllowAutoRedirect = false", block, StringComparison.Ordinal);
    }

    [Fact]
    public void Voice_code_logs_only_allowlisted_dimensions()
    {
        var files = SourceFiles(VoiceDirectory.TrimEnd('/'))
            .Concat(SourceFiles(DriversVoiceDirectory.TrimEnd('/')))
            .Concat(SourceFiles(DriversEndpointsDirectory.TrimEnd('/')))
            .ToArray();
        Assert.NotEmpty(files);
        var calls = 0;
        foreach (var file in files)
        {
            Assert.DoesNotContain("Console.", file.Text, StringComparison.Ordinal);
            foreach (var call in file.Text.Split("logger.Log", StringSplitOptions.None).Skip(1)
                         .Select(call => call[..call.IndexOf(");", StringComparison.Ordinal)]))
            {
                calls++;
                // No exception object either: provider and driver errors may echo a value.
                Assert.DoesNotContain("(exception", call, StringComparison.Ordinal);
                Assert.DoesNotContain("exception,", call, StringComparison.Ordinal);
                foreach (var forbidden in new[]
                         {
                             "E164", "Digits", "digits", "Ciphertext", "Phones", "phones", "Driver.", "Recipient.",
                             "AuthToken", "ApiKey", "AccountSid", "CompanyNumber", "Twiml", "payload", "Form", "form.", "form[",
                             "Body", "body", "request", "command", "Signature", "ProviderCallId", "CallSid",
                         })
                {
                    Assert.DoesNotContain(forbidden, call, StringComparison.Ordinal);
                }
            }
        }

        Assert.True(calls >= 5, $"Only {calls} log calls were inspected.");
    }

    [Fact]
    public void Calls_are_never_recorded_and_twilio_proxy_is_not_used()
    {
        var source = string.Join('\n', SourceFiles(VoiceDirectory.TrimEnd('/')).Select(file => file.Text));

        Assert.Contains("new(\"Record\", \"false\")", source, StringComparison.Ordinal);
        Assert.Contains("new XAttribute(\"record\", \"do-not-record\")", source, StringComparison.Ordinal);
        foreach (var forbidden in new[] { "proxy.twilio.com", "/Services/", "RecordingStatusCallback", "\"Record\", \"true\"", "Transcribe" })
        {
            Assert.DoesNotContain(forbidden, source, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Only_the_two_signed_twilio_webhooks_are_anonymous()
    {
        var endpoints = SourceFiles(DriversEndpointsDirectory.TrimEnd('/')).ToArray();
        var anonymous = endpoints.Where(file => file.Text.Contains(".AllowAnonymous()", StringComparison.Ordinal)).ToArray();

        var (path, text) = Assert.Single(anonymous);
        Assert.EndsWith("VoiceWebhookEndpoints.cs", path, StringComparison.Ordinal);
        Assert.Equal(2, text.Split(".AllowAnonymous()").Length - 1);
        Assert.Equal(2, text.Split(".DisableAntiforgery()").Length - 1);
        Assert.Contains("request.Headers[\"X-Twilio-Signature\"]", text, StringComparison.Ordinal);
        Assert.Contains("verifier.Verify(", text, StringComparison.Ordinal);
        Assert.DoesNotContain("X-Forwarded", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Request.Host", text, StringComparison.Ordinal);

        // The other voice routes are DRIVER operations behind the session, the tenant context and the capability gate.
        foreach (var name in new[] { "DriverPhoneEndpoints.cs", "RecipientCallEndpoints.cs" })
        {
            var source = Assert.Single(endpoints, file => file.Path.EndsWith(name, StringComparison.Ordinal)).Text;
            Assert.Contains(".RequireAuthorization(OrganizationPolicies.ActiveOrganizationMember)", source, StringComparison.Ordinal);
            Assert.Contains(".RequireTenantContext(StatusCodes.Status403Forbidden)", source, StringComparison.Ordinal);
            Assert.Contains("TenantCapabilityGate.Deny(", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_api_loads_key_vault_secrets_before_the_voice_bridge_and_the_worker_never_calls()
    {
        var api = File.ReadAllText(TestRepository.GetPath("src/Paqueteria.Api/Program.cs"));
        var secrets = api.IndexOf("builder.Configuration.AddPaqueteriaKeyVaultSecrets();", StringComparison.Ordinal);
        var voice = api.IndexOf(
            "builder.Services.AddPaqueteriaVoiceBridge(builder.Configuration, builder.Environment);",
            StringComparison.Ordinal);
        Assert.True(secrets > 0 && voice > secrets);
        Assert.Contains("app.MapDriverVoiceEndpoints();", api, StringComparison.Ordinal);

        var worker = File.ReadAllText(TestRepository.GetPath("src/Paqueteria.Worker/Program.cs"));
        Assert.DoesNotContain("Voice", worker, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_the_voice_code_and_its_migration_touch_the_driver_phone()
    {
        var users = SourceFiles("src")
            .Where(file => file.Text.Contains("driver_profiles", StringComparison.Ordinal) &&
                           file.Text.Contains("phone_ciphertext", StringComparison.Ordinal))
            .Select(file => file.Path)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(users);
        Assert.All(users, path => Assert.True(
            path.StartsWith(DriversVoiceDirectory, StringComparison.Ordinal) ||
            path.EndsWith("Migrations/20261011000100_AddDriverVoiceBridge.cs", StringComparison.Ordinal),
            path));
    }

    private static IEnumerable<(string Path, string Text)> SourceFiles(string relativeRoot) =>
        Directory.EnumerateFiles(TestRepository.GetPath(relativeRoot), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                           !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(path => (Path.GetRelativePath(TestRepository.GetPath("."), path).Replace('\\', '/'), File.ReadAllText(path)));
}
