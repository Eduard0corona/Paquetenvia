using System.Net;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Paqueteria.IntegrationTests.Security.AuthCenter;

/// <summary>
/// The pilot's single web origin for a browser test (AUTH-001, GATE-002-BFF-001): an HTTPS listener on loopback that,
/// like the pilot's ingress, sends <c>/api/</c>, <c>/hubs/</c>, <c>/auth/</c> and <c>/signin-authcenter</c> to the API
/// and everything else to the Next.js server, byte for byte and without following redirects or keeping cookies, so the
/// browser sees one first-party origin and the API's <c>__Host-</c> cookies, Origin and CSRF checks hold. The Host header
/// the browser sent is kept. WebSocket upgrades are not relayed (a test that needs the hubs blocks them). The certificate
/// is the process-local loopback one; only the test browser ignores its name.
/// </summary>
internal sealed class SameOriginFront : IAsyncDisposable
{
    private static readonly string[] ApiPrefixes = ["/api/", "/hubs/", "/auth/"];

    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Proxy-Connection", "TE", "Trailer", "Transfer-Encoding", "Upgrade", "Host",
    };

    private readonly WebApplication _app;
    private readonly HttpClient _client;

    private SameOriginFront(WebApplication app, HttpClient client, int port)
    {
        _app = app;
        _client = client;
        Port = port;
    }

    /// <summary>The loopback port the browser's host-resolver rule maps the public origin to.</summary>
    internal int Port { get; }

    internal static async Task<SameOriginFront> StartAsync(Uri api, Uri web, X509Certificate2 certificate)
    {
        // Loopback only: never the agent's outbound proxy, never a redirect followed, never a cookie kept.
        var client = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.None,
        })
        {
            Timeout = TimeSpan.FromSeconds(60),
        };
        SameOriginFront? front = null;
        var (app, port) = await LoopbackHttpsHost.StartAsync(certificate, context => front!.RelayAsync(context, api, web));
        front = new SameOriginFront(app, client, port);
        return front;
    }

    private static bool IsApi(PathString path) =>
        path == "/signin-authcenter" || ApiPrefixes.Any(prefix => path.Value!.StartsWith(prefix, StringComparison.Ordinal));

    private async Task RelayAsync(HttpContext context, Uri api, Uri web)
    {
        var toApi = IsApi(context.Request.Path);
        var upstream = toApi ? api : web;
        using var request = new HttpRequestMessage(
            new HttpMethod(context.Request.Method),
            new Uri(upstream, $"{context.Request.Path}{context.Request.QueryString}"));
        if (context.Request.ContentLength > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
        {
            request.Content = new StreamContent(context.Request.Body);
        }

        foreach (var (name, values) in context.Request.Headers)
        {
            if (HopByHopHeaders.Contains(name)) continue;
            if (!request.Headers.TryAddWithoutValidation(name, values.ToArray()))
            {
                request.Content?.Headers.TryAddWithoutValidation(name, values.ToArray());
            }
        }

        request.Headers.Host = context.Request.Host.Value;
        request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");
        HttpResponseMessage response;
        try
        {
            response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            return;
        }

        using var _ = response;
        context.Response.StatusCode = (int)response.StatusCode;
        foreach (var (name, values) in response.Headers.Concat(response.Content.Headers))
        {
            if (HopByHopHeaders.Contains(name)) continue;
            context.Response.Headers[name] = new StringValues(values.ToArray());
        }

        await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        _client.Dispose();
    }
}

/// <summary>A one-handler HTTPS listener on a free loopback port, with the process-local loopback certificate.</summary>
internal static class LoopbackHttpsHost
{
    internal static async Task<(WebApplication App, int Port)> StartAsync(X509Certificate2 certificate, RequestDelegate handler)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel =>
            kestrel.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
        var app = builder.Build();
        app.Run(handler);
        await app.StartAsync();
        var address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        return (app, address.Port);
    }
}
