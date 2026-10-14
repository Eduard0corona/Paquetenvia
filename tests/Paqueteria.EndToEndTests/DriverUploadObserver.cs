using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Playwright;

namespace Paqueteria.EndToEndTests;

/// <summary>
/// Invariant 13 from the browser side: the requests of one page seen on the Chrome DevTools Network domain (which also
/// shows the requests of the page's Service Worker), as method, target origin, sender origin and status. It keeps no
/// URL path, header value or body.
/// </summary>
internal sealed class DriverUploadObserver
{
    private readonly ConcurrentDictionary<string, (string Method, string TargetOrigin, string? BrowserOrigin)> _requests = new();
    private readonly ConcurrentQueue<(string Method, string TargetOrigin, string? BrowserOrigin, int Status)> _responses = new();

    internal static async Task<DriverUploadObserver> AttachAsync(IPage page)
    {
        var observer = new DriverUploadObserver();
        var session = await page.Context.NewCDPSessionAsync(page);
        session.Event("Network.requestWillBeSent").OnEvent += (_, payload) => observer.OnRequest(payload);
        session.Event("Network.responseReceived").OnEvent += (_, payload) => observer.OnResponse(payload);
        await session.SendAsync("Network.enable");
        return observer;
    }

    /// <summary>Successful responses of <paramref name="method"/> requests to <paramref name="targetOrigin"/>.</summary>
    internal IReadOnlyList<(string? BrowserOrigin, int Status)> Successful(string method, string targetOrigin) =>
        _responses
            .Where(response => response.Method == method &&
                               response.TargetOrigin == targetOrigin &&
                               response.Status is >= 200 and < 300)
            .Select(response => (response.BrowserOrigin, response.Status))
            .ToArray();

    private void OnRequest(JsonElement? payload)
    {
        if (payload is not { } value ||
            !value.TryGetProperty("requestId", out var id) ||
            !value.TryGetProperty("request", out var request) ||
            !request.TryGetProperty("url", out var url) ||
            !request.TryGetProperty("method", out var method) ||
            !Uri.TryCreate(url.GetString(), UriKind.Absolute, out var target))
        {
            return;
        }

        string? origin = null;
        if (request.TryGetProperty("headers", out var headers) && headers.ValueKind == JsonValueKind.Object)
        {
            origin = headers.EnumerateObject()
                .Where(header => string.Equals(header.Name, "origin", StringComparison.OrdinalIgnoreCase))
                .Select(header => header.Value.GetString())
                .FirstOrDefault();
        }

        _requests[id.GetString() ?? string.Empty] = (method.GetString() ?? string.Empty, target.GetLeftPart(UriPartial.Authority), origin);
    }

    private void OnResponse(JsonElement? payload)
    {
        if (payload is not { } value ||
            !value.TryGetProperty("requestId", out var id) ||
            !value.TryGetProperty("response", out var response) ||
            !response.TryGetProperty("status", out var status) ||
            !_requests.TryGetValue(id.GetString() ?? string.Empty, out var request))
        {
            return;
        }

        _responses.Enqueue((request.Method, request.TargetOrigin, request.BrowserOrigin, status.GetInt32()));
    }
}
