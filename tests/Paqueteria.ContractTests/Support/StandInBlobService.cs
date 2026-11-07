using System.Collections.Concurrent;
using System.Net;
using Azure.Core;
using Azure.Core.Pipeline;

namespace Paqueteria.ContractTests.Support;

/// <summary>
/// MDM-001 route S2: a stand-in for one private Blob container, plugged in as the Azure SDK transport (as in
/// <c>Obs003AzureClientTracingTests</c>), so the production <see cref="MasterDataBlobSource"/> runs unchanged. It answers
/// Get Blob Properties (HEAD), Get Blob (GET, honouring If-Match) and Delete Blob (DELETE, honouring If-Match) for one
/// blob, and records every request (method, path, header names and the If-Match value).
/// </summary>
internal sealed class StandInBlobService : HttpMessageHandler
{
    internal const string AccountUrl = "https://stmdm001synthetic.blob.core.windows.net/";
    internal const string Container = "master-data-reviewed";
    internal const string BlobName = "2026-11/reviewed-master-data.json";
    internal const string ETag = "\"0x8DD5A7E5D1C0001\"";

    private readonly ConcurrentQueue<StandInBlobRequest> _requests = new();
    private HttpPipelineTransport? _transport;

    internal StandInBlobService(byte[] content) => Content = content;

    internal byte[] Content { get; set; }

    /// <summary>What Get Blob Properties reports as the size; defaults to the real size.</summary>
    internal long? ReportedLength { get; set; }

    /// <summary>What Get Blob reports as the size of its body; defaults to the real size.</summary>
    internal long? DownloadReportedLength { get; set; }

    /// <summary>A status (and Azure error code) every request answers with instead of the blob.</summary>
    internal (HttpStatusCode Status, string Code)? Failure { get; set; }

    /// <summary>A status (and Azure error code) only Delete Blob answers with.</summary>
    internal (HttpStatusCode Status, string Code)? DeleteFailure { get; set; }

    /// <summary>A body a failure carries, to prove the job never prints a service response.</summary>
    internal string FailureBody { get; set; } = string.Empty;

    internal bool Deleted { get; private set; }

    internal HttpPipelineTransport Transport => _transport ??= new HttpClientTransport(new HttpClient(this));

    internal IReadOnlyList<StandInBlobRequest> Requests => _requests.ToArray();

    internal static MasterDataBlobReference Reference => MasterDataBlobReference.Parse(AccountUrl, Container, BlobName);

    internal MasterDataBlobSource Source() => new(Reference, new SyntheticCredential(), Transport);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var names = request.Headers.Select(header => header.Key)
            .Concat(request.Content?.Headers.Select(header => header.Key) ?? [])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ifMatch = request.Headers.TryGetValues("If-Match", out var values) ? string.Join(",", values) : null;
        _requests.Enqueue(new StandInBlobRequest(request.Method.Method, request.RequestUri!.AbsolutePath, names, ifMatch));
        if (request.Content is not null)
        {
            await request.Content.ReadAsByteArrayAsync(cancellationToken);
        }

        if (request.RequestUri.AbsolutePath != $"/{Container}/{BlobName}")
        {
            return Error(request, HttpStatusCode.NotFound, "BlobNotFound");
        }

        if (Failure is { } failure)
        {
            return Error(request, failure.Status, failure.Code);
        }

        if (Deleted)
        {
            return Error(request, HttpStatusCode.NotFound, "BlobNotFound");
        }

        if (ifMatch is not null && ifMatch != ETag)
        {
            return Error(request, HttpStatusCode.PreconditionFailed, "ConditionNotMet");
        }

        if (request.Method == HttpMethod.Head)
        {
            var head = Answer(request, HttpStatusCode.OK, new ByteArrayContent([]));
            head.Content.Headers.ContentLength = ReportedLength ?? Content.LongLength;
            head.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            return head;
        }

        if (request.Method == HttpMethod.Get)
        {
            var body = new StreamContent(new MemoryStream(Content, writable: false));
            body.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            body.Headers.ContentLength = DownloadReportedLength ?? Content.LongLength;
            return Answer(request, HttpStatusCode.OK, body);
        }

        if (request.Method == HttpMethod.Delete)
        {
            if (DeleteFailure is { } deleteFailure)
            {
                return Error(request, deleteFailure.Status, deleteFailure.Code);
            }

            Deleted = true;
            return Answer(request, HttpStatusCode.Accepted, new ByteArrayContent([]));
        }

        return Error(request, HttpStatusCode.MethodNotAllowed, "UnsupportedHttpVerb");
    }

    private static HttpResponseMessage Answer(HttpRequestMessage request, HttpStatusCode status, HttpContent content)
    {
        var response = new HttpResponseMessage(status) { RequestMessage = request, Content = content };
        response.Headers.TryAddWithoutValidation("ETag", ETag);
        response.Headers.TryAddWithoutValidation("x-ms-blob-type", "BlockBlob");
        response.Headers.TryAddWithoutValidation("x-ms-version", "2025-11-05");
        response.Headers.TryAddWithoutValidation("x-ms-request-id", Guid.NewGuid().ToString("D"));
        content.Headers.TryAddWithoutValidation("Last-Modified", "Fri, 06 Nov 2026 12:00:00 GMT");
        return response;
    }

    private HttpResponseMessage Error(HttpRequestMessage request, HttpStatusCode status, string code)
    {
        var body = request.Method == HttpMethod.Head
            ? new ByteArrayContent([])
            : new StringContent(
                $"<?xml version=\"1.0\" encoding=\"utf-8\"?><Error><Code>{code}</Code><Message>{FailureBody}</Message></Error>",
                System.Text.Encoding.UTF8,
                "application/xml");
        var response = new HttpResponseMessage(status) { RequestMessage = request, Content = body };
        response.Headers.TryAddWithoutValidation("x-ms-error-code", code);
        response.Headers.TryAddWithoutValidation("x-ms-request-id", Guid.NewGuid().ToString("D"));
        return response;
    }

    private sealed class SyntheticCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("synthetic-token", DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }
}

/// <summary>One request the stand-in Blob service received: never its body, only what proves the route.</summary>
internal sealed record StandInBlobRequest(string Method, string Path, IReadOnlySet<string> HeaderNames, string? IfMatch);
