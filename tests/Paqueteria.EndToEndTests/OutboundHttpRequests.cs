using System.Collections.Concurrent;
using System.Diagnostics;

namespace Paqueteria.EndToEndTests;

/// <summary>
/// OBS-003 "nada sale del proceso": every real HTTP request any <see cref="HttpClient"/> of this test process sends
/// (the API and the Worker run in it), seen through the runtime's own <c>HttpHandlerDiagnosticListener</c>, the same
/// source the OBS-003 export tests read. It records the target only, never headers or bodies. Process wide and
/// started once, before the first host of the suite exists.
/// </summary>
internal sealed class OutboundHttpRequests : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>
{
    private const string ListenerName = "HttpHandlerDiagnosticListener";
    private const string RequestEvent = "System.Net.Http.Request";
    private readonly ConcurrentQueue<Uri> _uris = new();
    private readonly ConcurrentBag<IDisposable> _subscriptions = [];

    private OutboundHttpRequests() => _subscriptions.Add(DiagnosticListener.AllListeners.Subscribe(this));

    internal static OutboundHttpRequests Instance { get; } = new();

    /// <summary>The number of requests seen so far; pass it to <see cref="Since"/>.</summary>
    internal int Mark() => _uris.Count;

    /// <summary>The targets of the requests sent after <paramref name="mark"/>.</summary>
    internal IReadOnlyList<Uri> Since(int mark) => _uris.Skip(mark).ToArray();

    void IObserver<DiagnosticListener>.OnNext(DiagnosticListener value)
    {
        if (value.Name == ListenerName)
        {
            _subscriptions.Add(value.Subscribe(this, name => name == RequestEvent));
        }
    }

    void IObserver<KeyValuePair<string, object?>>.OnNext(KeyValuePair<string, object?> value)
    {
        if (value.Key == RequestEvent &&
            value.Value?.GetType().GetProperty("Request")?.GetValue(value.Value) is HttpRequestMessage { RequestUri: { } uri })
        {
            _uris.Enqueue(uri);
        }
    }

    void IObserver<DiagnosticListener>.OnCompleted()
    {
    }

    void IObserver<DiagnosticListener>.OnError(Exception error)
    {
    }

    void IObserver<KeyValuePair<string, object?>>.OnCompleted()
    {
    }

    void IObserver<KeyValuePair<string, object?>>.OnError(Exception error)
    {
    }
}
