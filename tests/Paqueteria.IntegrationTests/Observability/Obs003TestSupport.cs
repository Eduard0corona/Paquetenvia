using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Paqueteria.Infrastructure.Observability;

namespace Paqueteria.IntegrationTests.Observability;

/// <summary>OBS-003 test fixtures: an inbound trace context to ignore, the JSON console lines and a header capture.</summary>
internal static partial class Obs003
{
    /// <summary>A valid inbound context with the sampled flag set, as a browser could send it.</summary>
    internal const string InboundTraceParent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";
    internal const string InboundTraceId = "0af7651916cd43dd8448eb211c80319c";
    internal const string InboundParentSpanId = "b7ad6b7169203331";
    internal const string InboundTraceState = "vendor=synthetic-obs003";
    internal const string InboundBaggage = "tenant=synthetic-obs003-org,user=synthetic-obs003-user";

    internal static readonly string[] TraceHeaders =
        ["traceparent", "tracestate", "baggage", "Request-Id", "Correlation-Context"];

    internal static void AddInboundTraceContext(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation("traceparent", InboundTraceParent);
        request.Headers.TryAddWithoutValidation("tracestate", InboundTraceState);
        request.Headers.TryAddWithoutValidation("baggage", InboundBaggage);
        request.Headers.TryAddWithoutValidation("Request-Id", $"|{InboundTraceId}.{InboundParentSpanId}.");
        request.Headers.TryAddWithoutValidation("Correlation-Context", "tenant=synthetic-obs003-org");
    }

    /// <summary>The 32-hex <c>traceId</c> of a problem body, asserting its shape.</summary>
    internal static string ProblemTraceId(string body)
    {
        using var document = JsonDocument.Parse(body);
        var traceId = document.RootElement.GetProperty("traceId").GetString();
        Assert.True(TraceCorrelation.IsTraceId(traceId), $"traceId is not 32 lowercase hex digits: {traceId}");
        return traceId!;
    }

    [GeneratedRegex("^[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    internal static partial Regex TraceIdPattern();
}

/// <summary>
/// The lines the API and Worker JSON console write. Added to a host with <see cref="AddTo"/>, every entry the host logs is
/// formatted by the host's own JSON console formatter (so with the host's configured options, scopes included or not)
/// and with the host's scope provider, exactly as its console provider formats it. Each line also keeps the id of the
/// hosting request scope it was written in (Kestrel's or the test server's request id, set before any middleware
/// runs), so the lines of one request can be found without trusting their <c>TraceId</c>.
/// </summary>
internal sealed class JsonConsoleCapture : ILoggerProvider, ISupportExternalScope
{
    private readonly ServiceProvider _formatters = new ServiceCollection()
        .AddLogging(logging => logging.AddJsonConsole())
        .BuildServiceProvider();

    private readonly ConcurrentQueue<(string Line, string? RequestScope)> _lines = new();
    private ConsoleFormatter _formatter;
    private IExternalScopeProvider? _scopes;

    internal JsonConsoleCapture()
    {
        _formatter = _formatters.GetServices<ConsoleFormatter>().Single(item => item.Name == ConsoleFormatterNames.Json);
    }

    internal IReadOnlyList<string> Lines => _lines.Select(entry => entry.Line).ToArray();

    internal IReadOnlyList<JsonElement> Parsed() =>
        Lines.Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();

    /// <summary>
    /// Registers the capture as one more provider of the host, formatting with the host's own JSON console formatter.
    /// </summary>
    internal void AddTo(ILoggingBuilder logging) =>
        logging.Services.AddSingleton<ILoggerProvider>(provider =>
        {
            _formatter = provider.GetServices<ConsoleFormatter>().Single(item => item.Name == ConsoleFormatterNames.Json);
            return new Registration(this);
        });

    /// <summary>Lines whose state carries <paramref name="traceId"/>.</summary>
    internal IReadOnlyList<string> LinesOf(string traceId) =>
        Lines.Where(line => TraceIdOf(line) == traceId).ToArray();

    /// <summary>The hosting request scope whose lines carry <paramref name="traceId"/>; there must be exactly one.</summary>
    internal string RequestScopeOf(string traceId) =>
        Assert.Single(_lines
            .Where(entry => TraceIdOf(entry.Line) == traceId && entry.RequestScope is not null)
            .Select(entry => entry.RequestScope!)
            .Distinct(StringComparer.Ordinal));

    /// <summary>Every line written inside the hosting request scope <paramref name="requestScope"/>.</summary>
    internal IReadOnlyList<string> LinesOfRequestScope(string requestScope) =>
        _lines.Where(entry => entry.RequestScope == requestScope).Select(entry => entry.Line).ToArray();

    internal static string? TraceIdOf(string line)
    {
        using var document = JsonDocument.Parse(line);
        return TraceIdOf(document.RootElement);
    }

    internal static string? TraceIdOf(JsonElement line) =>
        line.TryGetProperty("State", out var state) && state.ValueKind == JsonValueKind.Object &&
        state.TryGetProperty(TraceCorrelation.LogProperty, out var value)
            ? value.GetString()
            : null;

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, this);

    public void Dispose() => _formatters.Dispose();

    private void Write<TState>(
        string category,
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> messageFormatter)
    {
        using var writer = new StringWriter();
        _formatter.Write(
            new LogEntry<TState>(logLevel, category, eventId, state, exception, messageFormatter),
            _scopes,
            writer);
        var line = writer.ToString().TrimEnd();
        if (line.Length > 0)
        {
            _lines.Enqueue((line, RequestScope()));
        }
    }

    /// <summary>The <c>RequestId</c> of the hosting request scope the current entry is written in, if any.</summary>
    private string? RequestScope()
    {
        var found = new StrongBox<string?>();
        _scopes?.ForEachScope(
            static (scope, box) =>
            {
                if (scope is IEnumerable<KeyValuePair<string, object?>> properties &&
                    properties.Any(property => property.Key == "RequestPath"))
                {
                    box.Value = properties.FirstOrDefault(property => property.Key == "RequestId").Value?.ToString();
                }
            },
            found);
        return found.Value;
    }

    private sealed class Logger(string category, JsonConsoleCapture capture) : ILogger
    {
        // The host's logger factory pushes scopes on the external scope provider itself.
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> messageFormatter) =>
            capture.Write(category, logLevel, eventId, state, exception, messageFormatter);
    }

    /// <summary>
    /// The registration the host owns: the host disposes it, never the capture its test still reads, and passes its
    /// scope provider through.
    /// </summary>
    private sealed class Registration(JsonConsoleCapture capture) : ILoggerProvider, ISupportExternalScope
    {
        public ILogger CreateLogger(string categoryName) => capture.CreateLogger(categoryName);

        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => capture.SetScopeProvider(scopeProvider);

        public void Dispose()
        {
        }
    }
}

/// <summary>
/// A loopback HTTP server (Kestrel) that records the header names of every request it receives, so outgoing
/// requests are inspected on the wire, after every handler of the real client (including the runtime's
/// <c>DiagnosticsHandler</c>) has run.
/// </summary>
internal sealed class TraceHeaderCaptureServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentQueue<CapturedRequest> _requests = new();

    private TraceHeaderCaptureServer(WebApplication app)
    {
        _app = app;
    }

    internal Uri BaseAddress { get; private set; } = null!;

    internal IReadOnlyList<CapturedRequest> Requests => _requests.ToArray();

    internal static async Task<TraceHeaderCaptureServer> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        var server = new TraceHeaderCaptureServer(app);
        app.Run(context =>
        {
            server._requests.Enqueue(new CapturedRequest(
                context.Request.Path.Value ?? string.Empty,
                context.Request.Headers.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase)));
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "application/json";
            return context.Response.WriteAsync("{}");
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        server.BaseAddress = new Uri(address.TrimEnd('/') + "/", UriKind.Absolute);
        return server;
    }

    internal Uri For(string path) => new(BaseAddress, path.TrimStart('/'));

    internal CapturedRequest Single(string path) => Assert.Single(Requests, request => request.Path == "/" + path.TrimStart('/'));

    internal static void AssertNoTraceContext(CapturedRequest request)
    {
        foreach (var header in Obs003.TraceHeaders)
        {
            Assert.False(
                request.HeaderNames.Contains(header),
                $"{request.Path} carried {header}; headers: {string.Join(", ", request.HeaderNames)}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    internal sealed record CapturedRequest(string Path, IReadOnlySet<string> HeaderNames);
}
