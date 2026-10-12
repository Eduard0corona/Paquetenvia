using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Identity;
using Azure.Security.KeyVault.Keys.Cryptography;
using Azure.Security.KeyVault.Secrets;
using Azure.Storage.Blobs;
using Custody.Infrastructure.ProofStorage;
using Microsoft.Extensions.Options;
using Paqueteria.Infrastructure.Cloud;
using Paqueteria.Infrastructure.DataProtection;
using Paqueteria.Infrastructure.Security.Pii;

namespace Paqueteria.UnitTests.Security;

/// <summary>
/// OBS-003: Azure SDK clients add <c>traceparent</c> from <c>Activity.Current</c> through their own pipeline policy,
/// whatever the process propagator says. Every Azure client the code builds (Key Vault secrets, keys and
/// cryptography, Blob Storage, the data protection key resolver and the managed identity credential) is driven
/// here through a fake transport while a sampled activity with trace state and baggage is current: none of their
/// requests carries trace context. A client with the SDK defaults is the control that the capture sees it.
/// </summary>
public sealed class Obs003AzureClientTracingTests
{
    private static readonly string[] TraceHeaders = ["traceparent", "tracestate", "baggage", "Request-Id", "Correlation-Context"];
    private static readonly Uri Vault = new("https://kv-obs003-synthetic.vault.azure.net/");

    [Fact]
    public async Task The_sdk_default_sends_traceparent_so_the_capture_is_real()
    {
        var transport = new CapturingTransport();
        var options = new SecretClientOptions { Transport = transport.Transport };
        options.Retry.MaxRetries = 0;
        var client = new SecretClient(Vault, new SyntheticCredential(), options);

        await WithinSampledRequestActivityAsync(() => client.GetSecretAsync("synthetic-secret"));

        Assert.Contains(transport.Requests, request => request.Contains("traceparent"));
    }

    [Fact]
    public async Task Key_vault_secret_reader_sends_no_trace_context()
    {
        var transport = new CapturingTransport();
        var reader = new AzureKeyVaultSecretReader(Vault, new SyntheticCredential(), TimeSpan.FromSeconds(5), transport.Transport);

        await WithinSampledRequestActivityAsync(() => reader.GetSecretValueAsync("synthetic-secret", CancellationToken.None));

        transport.AssertNoTraceContext();
    }

    [Fact]
    public async Task Pii_key_vault_key_and_cryptography_clients_send_no_trace_context()
    {
        var transport = new CapturingTransport();
        var client = new AzureKeyVaultPiiKeyWrapClient(
            Options.Create(new PiiProtectionOptions
            {
                AzureKeyVault = new AzureKeyVaultPiiOptions
                {
                    KeyId = new Uri(Vault, "keys/pii-envelope").AbsoluteUri,
                    CurrentVersionRefreshSeconds = 300,
                },
            }),
            new SyntheticCredential(),
            TimeProvider.System,
            transport.Transport);

        // KeyClient (current version) and CryptographyClient (unwrap of a stored row).
        await WithinSampledRequestActivityAsync(() => client.GetCurrentKeyVersionAsync(CancellationToken.None));
        var keyRequests = transport.Requests.Count;
        await WithinSampledRequestActivityAsync(() => client.UnwrapKeyAsync(
            $"akv:pii-envelope/{new string('a', 32)}",
            [1, 2, 3],
            CancellationToken.None));

        Assert.True(keyRequests > 0);
        Assert.True(transport.Requests.Count > keyRequests);
        transport.AssertNoTraceContext();
    }

    [Fact]
    public async Task Proof_blob_client_options_send_no_trace_context()
    {
        var transport = new CapturingTransport();
        var options = AzureBlobProofGateway.CreateClientOptions(transport.Transport);
        options.Retry.MaxRetries = 0;
        var service = new BlobServiceClient(
            new Uri("https://stobs003synthetic.blob.core.windows.net"),
            new SyntheticCredential(),
            options);

        await WithinSampledRequestActivityAsync(() => service.GetBlobContainerClient("proofs").GetPropertiesAsync());

        transport.AssertNoTraceContext();
    }

    [Fact]
    public async Task Data_protection_key_resolver_sends_no_trace_context()
    {
        var transport = new CapturingTransport();
        var options = DataProtectionKeyEncryptionKeyResolver.CreateClientOptions(transport.Transport);
        options.Retry.MaxRetries = 0;
        var resolver = new KeyResolver(new SyntheticCredential(), options);

        await WithinSampledRequestActivityAsync(async () =>
        {
            var key = await resolver.ResolveAsync(new Uri(Vault, $"keys/data-protection/{new string('b', 32)}"));
            await key.WrapKeyAsync("RSA-OAEP-256", new byte[32]);
        });

        transport.AssertNoTraceContext();
    }

    [Fact]
    public async Task Managed_identity_token_requests_send_no_trace_context()
    {
        var transport = new CapturingTransport(HttpStatusCode.BadRequest);
        var options = AzureWorkloadCredential.CreateOptions("00000000-0000-0000-0000-0000000000a1", transport.Transport);
        options.Retry.MaxRetries = 0;
        var credential = new ManagedIdentityCredential(options);

        await WithinSampledRequestActivityAsync(() =>
            credential.GetTokenAsync(new TokenRequestContext(["https://vault.azure.net/.default"])).AsTask());

        transport.AssertNoTraceContext();
    }

    /// <summary>
    /// The worst case: a sampled server activity continuing an inbound context, with trace state and baggage.
    /// </summary>
    private static async Task WithinSampledRequestActivityAsync(Func<Task> call)
    {
        using var source = new ActivitySource("Obs003.Azure");
        using var listener = new ActivityListener
        {
            ShouldListenTo = item => item.Name == source.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        var inbound = ActivityContext.Parse(
            "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01",
            "vendor=synthetic");
        using var activity = source.StartActivity("synthetic-request", ActivityKind.Server, inbound)!;
        activity.AddBaggage("tenant", "synthetic-org");
        Assert.True(activity.Recorded);
        try
        {
            await call();
        }
        catch (Exception exception) when (exception is not Xunit.Sdk.XunitException)
        {
            // The fake answers with an error; only the outgoing requests matter.
        }
    }

    private sealed class SyntheticCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("synthetic-token", DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken) => ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    /// <summary>A transport whose every request is recorded (header names only) and answered with an error.</summary>
    private sealed class CapturingTransport(HttpStatusCode status = HttpStatusCode.Forbidden) : HttpMessageHandler
    {
        private readonly ConcurrentQueue<IReadOnlySet<string>> _requests = new();
        private HttpPipelineTransport? _transport;

        internal HttpPipelineTransport Transport => _transport ??= new HttpClientTransport(new HttpClient(this));

        internal IReadOnlyCollection<IReadOnlySet<string>> Requests => _requests.ToArray();

        internal void AssertNoTraceContext()
        {
            Assert.NotEmpty(_requests);
            foreach (var request in _requests)
            {
                foreach (var header in TraceHeaders)
                {
                    Assert.DoesNotContain(header, request);
                }
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var names = request.Headers.Select(header => header.Key)
                .Concat(request.Content?.Headers.Select(header => header.Key) ?? [])
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            _requests.Enqueue(names);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                RequestMessage = request,
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
}
