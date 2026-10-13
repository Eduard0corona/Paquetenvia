using Paqueteria.ArchitectureTests.Architecture;

namespace Paqueteria.ArchitectureTests;

/// <summary>
/// OBS-003: the Azure client guard of <see cref="TraceCorrelationArchitectureTests"/> rejects every known way around
/// it, each built here as a fixture source: a target-typed <c>new(</c>, an unlisted client or credential, a second
/// unwrapped construction in a reviewed file, a route that only a nested construction takes, the extensions that build
/// their own clients, a global using and a display name set on any receiver. The production patterns pass.
/// </summary>
public sealed class AzureClientTracingGuardTests
{
    private const string Reviewed = "src/Fixture/Reviewed.cs";
    private const string Unreviewed = "src/Fixture/Unreviewed.cs";

    public static TheoryData<string, string, bool, string> Bypasses() => new()
    {
        {
            "target-typed field initializer",
            """
            using Azure.Security.KeyVault.Secrets;

            public sealed class Reader(Uri vault, TokenCredential credential)
            {
                private readonly SecretClient _client = new(vault, credential);
            }
            """,
            true,
            "new(...) of SecretClient is not built with options passed through"
        },
        {
            "target-typed assignment to a declared field",
            """
            using Azure.Security.KeyVault.Secrets;

            public sealed class Reader
            {
                private readonly SecretClient _client;

                public Reader(Uri vault, TokenCredential credential)
                {
                    _client = new(vault, credential, new SecretClientOptions());
                }
            }
            """,
            true,
            "new(...) of SecretClient is not built with options passed through"
        },
        {
            "target-typed new whose type the statement does not name",
            """
            using Azure.Storage.Blobs;

            public static class Blobs
            {
                public static BlobServiceClient Create(Uri service, TokenCredential credential) => new(service, credential);
            }
            """,
            true,
            "a target-typed new(...) in an Azure client file must name its type"
        },
        {
            "EmailClient",
            """
            using Azure.Communication.Email;

            public sealed class Mailer(Uri endpoint, TokenCredential credential)
            {
                public EmailClient Client { get; } = new EmailClient(endpoint, credential);
            }
            """,
            true,
            "new EmailClient(...) is not built with options passed through"
        },
        {
            "ServiceBusClient",
            """
            using Azure.Messaging.ServiceBus;

            public sealed class Bus(string fullyQualifiedNamespace, TokenCredential credential)
            {
                public ServiceBusClient Client { get; } = new ServiceBusClient(fullyQualifiedNamespace, credential);
            }
            """,
            true,
            "new ServiceBusClient(...) is not built with options passed through"
        },
        {
            "QueueClient with options that skip the wrapper",
            """
            using Azure.Storage.Queues;

            public sealed class Queue(Uri queue, TokenCredential credential)
            {
                public QueueClient Client { get; } = new QueueClient(queue, credential, new QueueClientOptions());
            }
            """,
            true,
            "new QueueClient(...) is not built with options passed through"
        },
        {
            "ClientSecretCredential",
            """
            using Azure.Identity;

            public static class Credentials
            {
                public static TokenCredential Create(string tenantId, string clientId, string clientSecret) =>
                    new ClientSecretCredential(tenantId, clientId, clientSecret);
            }
            """,
            true,
            "new ClientSecretCredential(...) is not built with options passed through"
        },
        {
            "AzureCliCredential",
            """
            using Azure.Identity;

            public static class Credentials
            {
                public static TokenCredential Create() => new AzureCliCredential();
            }
            """,
            true,
            "new AzureCliCredential(...) is not built with options passed through"
        },
        {
            "a second, unwrapped construction in a reviewed file",
            """
            using Azure.Security.KeyVault.Secrets;

            public sealed class Reader
            {
                private readonly SecretClient _wrapped;
                private readonly SecretClient _plain;

                public Reader(Uri vault, TokenCredential credential)
                {
                    _wrapped = new SecretClient(vault, credential, new SecretClientOptions().WithoutDistributedTracing());
                    _plain = new SecretClient(vault, credential);
                }
            }
            """,
            true,
            "src/Fixture/Reviewed.cs:11: new SecretClient(...) is not built with options passed through"
        },
        {
            "a route taken only by a nested credential",
            """
            using Azure.Identity;
            using Azure.Security.KeyVault.Secrets;

            public sealed class Reader(Uri vault, string clientId)
            {
                public SecretClient Client { get; } = new SecretClient(
                    vault,
                    new ManagedIdentityCredential(CreateOptions(clientId)),
                    new SecretClientOptions());

                private static ManagedIdentityCredentialOptions CreateOptions(string clientId)
                {
                    var options = new ManagedIdentityCredentialOptions(ManagedIdentityId.FromUserAssignedClientId(clientId));
                    return options.WithoutDistributedTracing();
                }
            }
            """,
            true,
            "src/Fixture/Reviewed.cs:6: new SecretClient(...) is not built with options passed through"
        },
        {
            "a helper that skips the wrapper on one return",
            """
            using Azure.Security.KeyVault.Secrets;

            public sealed class Reader(Uri vault, TokenCredential credential, bool traced)
            {
                public SecretClient Client { get; } = new SecretClient(vault, credential, Options(traced));

                private static SecretClientOptions Options(bool traced)
                {
                    if (traced)
                    {
                        return new SecretClientOptions();
                    }

                    return new SecretClientOptions().WithoutDistributedTracing();
                }
            }
            """,
            true,
            "new SecretClient(...) is not built with options passed through"
        },
        {
            "services.AddAzureClients",
            """
            using Microsoft.Extensions.Azure;

            public static class Registration
            {
                public static void Add(IServiceCollection services, Uri vault) =>
                    services.AddAzureClients(clients => clients.AddSecretClient(vault));
            }
            """,
            true,
            "AddAzureClients(...) must apply"
        },
        {
            "ProtectKeysWithAzureKeyVault(keyId, credential)",
            """
            using Azure.Core;
            using Microsoft.AspNetCore.DataProtection;

            public static class KeyRing
            {
                public static void Protect(IDataProtectionBuilder builder, Uri keyId, TokenCredential credential) =>
                    builder.ProtectKeysWithAzureKeyVault(keyId, credential);
            }
            """,
            true,
            "ProtectKeysWithAzureKeyVault(...) must take the reviewed DataProtectionKeyEncryptionKeyResolver"
        },
        {
            "PersistKeysToAzureBlobStorage(uri, credential)",
            """
            using Azure.Core;
            using Microsoft.AspNetCore.DataProtection;

            public static class KeyRing
            {
                public static void Persist(IDataProtectionBuilder builder, Uri blob, TokenCredential credential) =>
                    builder.PersistKeysToAzureBlobStorage(blob, credential);
            }
            """,
            true,
            "PersistKeysToAzureBlobStorage(...) must receive a client or options built through"
        },
        {
            "HttpPipelineBuilder.Build with unwrapped options",
            """
            using Azure.Core;
            using Azure.Core.Pipeline;

            public static class Pipelines
            {
                public static HttpPipeline Create(ClientOptions options) => HttpPipelineBuilder.Build(options);
            }
            """,
            true,
            "HttpPipelineBuilder.Build(...) must receive a client or options built through"
        },
        {
            "an Azure namespace in a file nobody reviewed",
            """
            using Azure.Storage.Blobs.Models;

            public static class Models
            {
                public static bool IsPrivate(PublicAccessType? access) => access is null or PublicAccessType.None;
            }
            """,
            false,
            "src/Fixture/Unreviewed.cs: uses Azure.Storage and is not a reviewed Azure client file"
        },
        {
            "a fully qualified client in a file nobody reviewed",
            """
            public static class Blobs
            {
                public static object Create(Uri blob, Azure.Core.TokenCredential credential) =>
                    new global::Azure.Storage.Blobs.BlobClient(blob, credential);
            }
            """,
            false,
            "new BlobClient(...) is not built with options passed through"
        },
        {
            "a global using",
            """
            global using Azure.Identity;
            """,
            false,
            "'global using Azure.Identity;' hides Azure SDK use from the review"
        },
    };

    [Theory]
    [MemberData(nameof(Bypasses))]
    public void The_guard_rejects_every_way_around_it(string bypass, string code, bool reviewed, string expected)
    {
        var path = reviewed ? Reviewed : Unreviewed;

        var violations = AzureClientTracingGuard.Violations(
            [new GuardedSource(path, code)],
            reviewed ? new HashSet<string>(StringComparer.Ordinal) { Reviewed } : new HashSet<string>(StringComparer.Ordinal));

        Assert.True(
            violations.Any(violation => violation.Contains(expected, StringComparison.Ordinal)),
            $"{bypass}: expected '{expected}' in [{string.Join(" | ", violations)}]");
    }

    [Theory]
    [InlineData("a second, unwrapped construction in a reviewed file")]
    [InlineData("a route taken only by a nested credential")]
    public void Only_the_unwrapped_construction_is_reported(string bypass)
    {
        var code = (string)Bypasses().Single(row => (string)row[0] == bypass)[1];

        var violations = AzureClientTracingGuard.Violations(
            [new GuardedSource(Reviewed, code)],
            new HashSet<string>(StringComparer.Ordinal) { Reviewed });

        Assert.Single(violations);
    }

    [Fact]
    public void The_production_patterns_pass()
    {
        GuardedSource[] sources =
        [
            new(
                "src/Fixture/Options.cs",
                """
                using Azure.Core.Pipeline;
                using Azure.Security.KeyVault.Keys.Cryptography;

                public sealed class Options
                {
                    public static CryptographyClientOptions CreateClientOptions(HttpPipelineTransport? transport = null)
                    {
                        var options = new CryptographyClientOptions();
                        if (transport is not null)
                        {
                            options.Transport = transport;
                        }

                        return options.WithoutDistributedTracing();
                    }
                }
                """),
            new(
                Reviewed,
                """
                using System.Collections.Concurrent;
                using Azure.Core;
                using Azure.Core.Pipeline;
                using Azure.Identity;
                using Azure.Security.KeyVault.Keys;
                using Azure.Security.KeyVault.Keys.Cryptography;
                using Azure.Security.KeyVault.Secrets;
                using Microsoft.AspNetCore.DataProtection;

                public sealed class Clients
                {
                    private readonly ConcurrentDictionary<string, CryptographyClient> _clients = new(StringComparer.Ordinal);
                    private readonly SemaphoreSlim _lock = new(1, 1);
                    private readonly CryptographyClientOptions _cryptography;
                    private readonly KeyClient _keys;
                    private readonly SecretClient _secrets;

                    public Clients(Uri vault, TokenCredential credential, HttpPipelineTransport? transport)
                    {
                        _keys = new KeyClient(vault, credential, Configure(new KeyClientOptions(), transport));
                        _cryptography = Configure(new CryptographyClientOptions(), transport);
                        var options = new SecretClientOptions();
                        options.Retry.MaxRetries = 2;
                        _secrets = new SecretClient(vault, credential, options.WithoutDistributedTracing());
                    }

                    public static TokenCredential Credential(string? clientId) =>
                        new ManagedIdentityCredential(CreateOptions(clientId));

                    public static KeyResolver Resolver(TokenCredential credential) =>
                        new KeyResolver(credential, Options.CreateClientOptions());

                    public static void Protect(IDataProtectionBuilder builder, Uri keyId) =>
                        builder.ProtectKeysWithAzureKeyVault(
                            keyId,
                            provider => provider.GetRequiredService<DataProtectionKeyEncryptionKeyResolver>().Resolver);

                    public CryptographyClient Cryptography(Uri key, TokenCredential credential) =>
                        _clients.GetOrAdd(key.AbsoluteUri, _ => new CryptographyClient(new Uri(key, "x"), credential, _cryptography));

                    private static ManagedIdentityCredentialOptions CreateOptions(string? clientId) =>
                        new ManagedIdentityCredentialOptions(ManagedIdentityId.FromUserAssignedClientId(clientId)).WithoutDistributedTracing();

                    private static T Configure<T>(T options, HttpPipelineTransport? transport)
                        where T : ClientOptions
                    {
                        if (transport is not null)
                        {
                            options.Transport = transport;
                        }

                        return options.WithoutDistributedTracing();
                    }
                }
                """),
        ];

        Assert.Empty(AzureClientTracingGuard.Violations(
            sources,
            new HashSet<string>(StringComparer.Ordinal) { Reviewed, "src/Fixture/Options.cs" }));
    }

    [Theory]
    [InlineData("""<Project><ItemGroup><Using Include="Azure.Identity" /></ItemGroup></Project>""", true)]
    [InlineData("""<Project><ItemGroup><Using Include="Azure.Storage.Blobs.BlobClient" Alias="Blob" /></ItemGroup></Project>""", true)]
    [InlineData("""<Project><ItemGroup><Using Include="Microsoft.Extensions.Azure" /></ItemGroup></Project>""", true)]
    [InlineData("""<Project><ItemGroup><Using Include="Azure.Core" /></ItemGroup></Project>""", false)]
    public void A_project_wide_azure_using_is_rejected(string project, bool rejected) =>
        Assert.Equal(rejected, AzureClientTracingGuard.ProjectViolations("src/Fixture/Fixture.csproj", project).Count > 0);

    [Theory]
    [InlineData("activity.DisplayName = name;", true)]
    [InlineData("Activity.Current!.DisplayName = context.Request.Path;", true)]
    [InlineData("Activity.Current?.DisplayName = context.Request.Path;", true)]
    [InlineData("current.DisplayName += suffix;", true)]
    [InlineData("current . DisplayName\n    = path;", true)]
    [InlineData("DisplayName = displayName;", false)]
    [InlineData("if (organization.DisplayName == name) { }", false)]
    [InlineData("return item.Owner.DisplayName;", false)]
    public void A_display_name_set_on_any_receiver_is_found(string code, bool found) =>
        Assert.Equal(found, AzureClientTracingGuard.DisplayNameAssignments(code).Count > 0);
}
