using System.Collections;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Paqueteria.Infrastructure.Observability;

/// <summary>
/// OBS-003: adds the W3C trace id of the current activity to every log line written inside a trace, as the
/// structured property <see cref="TraceCorrelation.LogProperty"/>. The JSON console writes it in <c>State</c>
/// next to the message properties (<c>State.TraceId</c> in Log Analytics). Lines written outside any trace
/// (host start, the OBS-002 lane and HTTP summaries) carry none.
/// </summary>
/// <remarks>
/// It decorates the host <see cref="ILoggerFactory"/>, so every provider sees the same state. The trace id is a
/// random correlation id, not an identifier of a person or an organization; it is never a message template
/// placeholder, an OBS-002 telemetry dimension or an alert or workbook property.
/// </remarks>
public sealed class TraceCorrelatedLoggerFactory(ILoggerFactory inner) : ILoggerFactory
{
    private readonly ConcurrentDictionary<string, ILogger> _loggers = new(StringComparer.Ordinal);

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(
            categoryName,
            static (name, factory) => new TraceCorrelatedLogger(factory.CreateLogger(name)),
            inner);

    public void AddProvider(ILoggerProvider provider) => inner.AddProvider(provider);

    public void Dispose() => inner.Dispose();
}

internal sealed class TraceCorrelatedLogger(ILogger inner) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => inner.BeginScope(state);

    public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var traceId = TraceCorrelation.CurrentTraceId();
        if (traceId is null || state is null || !inner.IsEnabled(logLevel) || HasTraceProperty(state))
        {
            inner.Log(logLevel, eventId, state, exception, formatter);
            return;
        }

        inner.Log(
            logLevel,
            eventId,
            new TraceCorrelatedLogState<TState>(state, formatter, traceId),
            exception,
            TraceCorrelatedLogState<TState>.Format);
    }

    private static bool HasTraceProperty<TState>(TState state)
    {
        if (state is not IEnumerable<KeyValuePair<string, object?>> properties)
        {
            return false;
        }

        foreach (var property in properties)
        {
            if (string.Equals(property.Key, TraceCorrelation.LogProperty, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// The original state with the trace id appended. The message, its template (<c>{OriginalFormat}</c>) and every
/// original property stay as they were; formatting is delegated to the original formatter.
/// </summary>
internal sealed class TraceCorrelatedLogState<TState> : IReadOnlyList<KeyValuePair<string, object?>>
{
    private readonly TState _state;
    private readonly Func<TState, Exception?, string> _formatter;
    private readonly IReadOnlyList<KeyValuePair<string, object?>> _properties;
    private readonly string _traceId;

    internal TraceCorrelatedLogState(TState state, Func<TState, Exception?, string> formatter, string traceId)
    {
        _state = state;
        _formatter = formatter;
        _traceId = traceId;
        _properties = state as IReadOnlyList<KeyValuePair<string, object?>>
            ?? (state as IEnumerable<KeyValuePair<string, object?>>)?.ToArray()
            ?? [];
    }

    public int Count => _properties.Count + 1;

    public KeyValuePair<string, object?> this[int index]
    {
        get
        {
            if (index >= 0 && index < _properties.Count)
            {
                return _properties[index];
            }

            return index == _properties.Count
                ? new KeyValuePair<string, object?>(TraceCorrelation.LogProperty, _traceId)
                : throw new ArgumentOutOfRangeException(nameof(index));
        }
    }

    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
    {
        for (var index = 0; index < Count; index++)
        {
            yield return this[index];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public override string? ToString() => _state?.ToString();

    internal static string Format(TraceCorrelatedLogState<TState> state, Exception? exception) =>
        state._formatter(state._state, exception);
}
