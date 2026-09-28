using Paqueteria.ArchitectureTests.Architecture;

namespace Paqueteria.ArchitectureTests;

/// <summary>
/// GATE-004-CHANNELS: outbound provider HTTP stays confined to the messaging adapters (and the
/// GATE-003 Google Maps geocoding adapter), their HTTP clients never log requests, and no adapter
/// logs a recipient, a parameter, a token or a body.
/// </summary>
public sealed class MessagingArchitectureTests
{
    private const string MessagingDirectory = "src/BuildingBlocks/Paqueteria.Infrastructure/Messaging/";

    private static readonly string[] OutboundHttpAllowlist =
    [
        MessagingDirectory,
        "src/Modules/Locations/Locations.Infrastructure/Geocoding/GoogleMaps/",
        "src/Modules/Locations/Locations.Infrastructure/DependencyInjection.cs",
    ];

    [Fact]
    public void Outbound_http_clients_exist_only_in_the_provider_adapters()
    {
        var users = SourceFiles("src")
            .Where(file => file.Text.Contains("HttpClient", StringComparison.Ordinal))
            .Select(file => file.Path)
            .ToArray();

        Assert.NotEmpty(users);
        Assert.All(users, path => Assert.Contains(
            OutboundHttpAllowlist,
            allowed => path.StartsWith(allowed, StringComparison.Ordinal)));
    }

    [Fact]
    public void Messaging_http_clients_drop_the_default_request_loggers()
    {
        var registration = File.ReadAllText(TestRepository.GetPath(
            MessagingDirectory + "MessagingServiceCollectionExtensions.cs"));
        var registrations = registration.Split("services.AddHttpClient(", StringSplitOptions.None).Skip(1).ToArray();

        Assert.Equal(2, registrations.Length);
        Assert.All(registrations, block => Assert.Contains(".RemoveAllLoggers();", block, StringComparison.Ordinal));
    }

    [Fact]
    public void Messaging_adapters_log_only_allowlisted_dimensions()
    {
        foreach (var file in SourceFiles(MessagingDirectory.TrimEnd('/')))
        {
            var logCalls = file.Text.Split("logger.Log", StringSplitOptions.None).Skip(1)
                .Select(call => call[..call.IndexOf(");", StringComparison.Ordinal)]);
            foreach (var call in logCalls)
            {
                foreach (var forbidden in new[] { "Recipient", "Parameters", "AccessToken", "PhoneNumberId", "payload", "body", "token", "Address", "Subject", "PlainText" })
                {
                    Assert.DoesNotContain(forbidden, call, StringComparison.Ordinal);
                }
            }
        }

        var contracts = File.ReadAllText(TestRepository.GetPath(
            "src/BuildingBlocks/Paqueteria.Application/Messaging/MessagingContracts.cs"));
        Assert.Contains("public override string ToString() => \"[redacted]\";", contracts, StringComparison.Ordinal);
    }

    [Fact]
    public void Acs_uses_the_workload_credential_and_never_a_connection_string_or_access_key()
    {
        var source = string.Join('\n', SourceFiles(MessagingDirectory.TrimEnd('/')).Select(file => file.Text));

        Assert.Contains("TokenCredential", source, StringComparison.Ordinal);
        Assert.Contains("services.AddAzureWorkloadCredential();", source, StringComparison.Ordinal);
        foreach (var forbidden in new[] { "accesskey", "ConnectionString", "HMACSHA256", "x-ms-content-sha256" })
        {
            Assert.DoesNotContain(forbidden, source, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void The_worker_registers_messaging_after_loading_key_vault_secrets()
    {
        var program = File.ReadAllText(TestRepository.GetPath("src/Paqueteria.Worker/Program.cs"));
        var secrets = program.IndexOf("builder.Configuration.AddPaqueteriaKeyVaultSecrets();", StringComparison.Ordinal);
        var messaging = program.IndexOf(
            "builder.Services.AddPaqueteriaMessaging(builder.Configuration, builder.Environment);",
            StringComparison.Ordinal);

        Assert.True(secrets > 0 && messaging > secrets);
    }

    private static IEnumerable<(string Path, string Text)> SourceFiles(string relativeRoot) =>
        Directory.EnumerateFiles(TestRepository.GetPath(relativeRoot), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                           !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(path => (Path.GetRelativePath(TestRepository.GetPath("."), path).Replace('\\', '/'), File.ReadAllText(path)));
}
