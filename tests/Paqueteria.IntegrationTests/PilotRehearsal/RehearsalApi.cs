using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Paqueteria.IntegrationTests.Realtime;

namespace Paqueteria.IntegrationTests.PilotRehearsal;

/// <summary>
/// The rehearsal's HTTP client of the assembled API: the real endpoints over the loopback TLS listener, each call signed
/// in with a Mock identity bearer session and the synthetic organization selected, as every browser suite and OPS-001
/// do. It trusts only the API's in-memory loopback certificate. Each write carries its own Idempotency-Key. It keeps the
/// duration of every proof upload session it opens (AI-15 signed_url_generation_p95_ms, reported only).
/// </summary>
internal sealed class RehearsalApi : IDisposable
{
    private readonly HttpClient _client;
    private readonly Guid _organizationId;
    private readonly string _runTag;
    private int _keys;

    internal RehearsalApi(Uri apiAddress, Guid organizationId, string runTag)
    {
        _client = new HttpClient(LoopbackHandler()) { BaseAddress = apiAddress, Timeout = TimeSpan.FromSeconds(60) };
        _organizationId = organizationId;
        _runTag = runTag;
    }

    internal List<TimeSpan> UploadSessionDurations { get; } = [];

    /// <summary>A new Idempotency-Key, unique within the run (AI-05 IdempotencyKey: 16 to 128 safe characters).</summary>
    internal string NewKey(string operation) =>
        $"rel001-{_runTag}-{operation}-{Interlocked.Increment(ref _keys):D5}";

    internal async Task<JsonElement> PostAsync(
        string identity,
        string path,
        object? body,
        HttpStatusCode expected,
        string? idempotencyKey = null)
    {
        using var request = Request(HttpMethod.Post, path, identity);
        request.Headers.Add("Idempotency-Key", idempotencyKey ?? NewKey("post"));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await SendAsync(request, expected);
    }

    internal async Task<JsonElement> GetAsync(string identity, string path, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var request = Request(HttpMethod.Get, path, identity);
        return await SendAsync(request, expected);
    }

    internal async Task<string> GetTextAsync(string identity, string path)
    {
        using var request = Request(HttpMethod.Get, path, identity);
        using var response = await _client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new RehearsalStepException(path, response.StatusCode, text);
        }

        return text;
    }

    internal async Task<JsonElement> PostMultipartAsync(
        string identity,
        string path,
        MultipartFormDataContent content,
        HttpStatusCode expected,
        string? idempotencyKey = null)
    {
        using var request = Request(HttpMethod.Post, path, identity);
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        request.Content = content;
        return await SendAsync(request, expected);
    }

    /// <summary>createProofUploadSession, timed from the request to the answer (the signed URL generation).</summary>
    internal async Task<JsonElement> CreateUploadSessionAsync(string identity, Guid orderId, object body)
    {
        var started = Stopwatch.GetTimestamp();
        var session = await PostAsync(
            identity,
            $"/api/v1/orders/{orderId:D}/proof-upload-sessions",
            body,
            HttpStatusCode.Created,
            NewKey("upload-session"));
        lock (UploadSessionDurations)
        {
            UploadSessionDurations.Add(Stopwatch.GetElapsedTime(started));
        }

        return session;
    }

    /// <summary>
    /// The signed PUT to the storage double, with exactly the headers the session requires. The storage origin is the
    /// harness's loopback TLS tunnel, which presents the same in-memory loopback certificate as the API.
    /// </summary>
    internal static async Task PutSignedAsync(JsonElement session, byte[] bytes)
    {
        using var client = new HttpClient(LoopbackHandler());
        using var request = new HttpRequestMessage(HttpMethod.Put, session.GetProperty("upload_url").GetString())
        {
            Content = new ByteArrayContent(bytes),
        };
        foreach (var header in session.GetProperty("required_headers").EnumerateObject())
        {
            if (string.Equals(header.Name, "Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                request.Content.Headers.ContentType = new MediaTypeHeaderValue(header.Value.GetString()!);
            }
            else
            {
                request.Headers.TryAddWithoutValidation(header.Name, header.Value.GetString());
            }
        }

        using var response = await client.SendAsync(request);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new RehearsalStepException("signed upload", response.StatusCode, string.Empty);
        }
    }

    public void Dispose() => _client.Dispose();

    /// <summary>Trusts only the in-memory loopback certificate of this test process.</summary>
    private static HttpClientHandler LoopbackHandler()
    {
        var expected = RealtimeKestrelWebApplicationFactory.LoopbackCertificate.GetCertHashString();
        return new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                certificate is X509Certificate2 presented &&
                string.Equals(presented.GetCertHashString(), expected, StringComparison.OrdinalIgnoreCase),
        };
    }

    private HttpRequestMessage Request(HttpMethod method, string path, string identity)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", identity);
        request.Headers.Add("X-Organization-Id", _organizationId.ToString("D"));
        return request;
    }

    private async Task<JsonElement> SendAsync(HttpRequestMessage request, HttpStatusCode expected)
    {
        using var response = await _client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        if (response.StatusCode != expected)
        {
            throw new RehearsalStepException(
                $"{request.Method} {request.RequestUri?.AbsolutePath}",
                response.StatusCode,
                text);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return default;
        }

        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }
}

/// <summary>A rehearsal step got an answer it did not expect. The body is cut short; synthetic data only.</summary>
internal sealed class RehearsalStepException(string step, HttpStatusCode status, string body)
    : Exception($"{step} answered {(int)status}: {(body.Length > 600 ? body[..600] : body)}");
