using System.Collections;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Paqueteria.Infrastructure.Observability;

/// <summary>
/// OBS-003: adds the W3C trace id of the current activity to every log line written inside a trace, as the
/// structured property <see cref="TraceCorrelation.LogProperty"/>. The JSON console writes it in <c>State</c>
/// next to the message properties (<c>State.TraceId</c> in Log Analytics). Lines written outside any trace
/// (host start, the OBS-002 lane and HTTP summaries) carry none. Inside an organization context
/// (<see cref="TelemetryOrganizationContext"/>, OBS-003-TENANT-PSEUDONYM-2026-10-12) every line also carries
/// <c>OrganizationPseudonym</c> and <c>ActorRoleKind</c>, also when the export is off, except the events the OBS-002
/// alerts and workbook read (<see cref="TelemetryEvents.IsObservabilityEvent"/>), which never carry them.
/// </summary>
/// <remarks>
/// It decorates the host <see cref="ILoggerFactory"/>, so every provider sees the same state. The trace id is a
/// random correlation id, not an identifier of a person or an organization; the pseudonym is an HMAC under a Key
/// Vault key and the role kind a fixed role name. None of them is ever a message template placeholder, an OBS-002
/// telemetry dimension or an alert or workbook property.
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
        // An OBS-002 event keeps only its allowlisted dimensions (and the trace id, which is not one), wherever it is
        // written: the pseudonym and the role kind never become an alert or workbook dimension.
        var organization = TelemetryEvents.IsObservabilityEvent(eventId) ? null : TelemetryOrganizationContext.Current;
        if ((traceId is null && organization is null) || state is null || !inner.IsEnabled(logLevel))
        {
            inner.Log(logLevel, eventId, state, exception, formatter);
            return;
        }

        var added = new List<KeyValuePair<string, object?>>(3);
        Add(added, state, TraceCorrelation.LogProperty, traceId);
        Add(added, state, TelemetryOrganizationContext.PseudonymLogProperty, organization?.Pseudonym);
        Add(added, state, TelemetryOrganizationContext.RoleKindLogProperty, organization?.RoleKind);
        if (added.Count == 0)
        {
            inner.Log(logLevel, eventId, state, exception, formatter);
            return;
        }

        inner.Log(
            logLevel,
            eventId,
            new TraceCorrelatedLogState<TState>(state, formatter, added),
            exception,
            TraceCorrelatedLogState<TState>.Format);
    }

    private static void Add<TState>(List<KeyValuePair<string, object?>> added, TState state, string name, string? value)
    {
        if (value is not null && !HasProperty(state, name))
        {
            added.Add(new KeyValuePair<string, object?>(name, value));
        }
    }

    private static bool HasProperty<TState>(TState state, string name)
    {
        if (state is not IEnumerable<KeyValuePair<string, object?>> properties)
        {
            return false;
        }

        foreach (var property in properties)
        {
            if (string.Equals(property.Key, name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// The original state with the trace id (and, in an organization context, the pseudonym and role kind) appended. The
/// message, its template (<c>{OriginalFormat}</c>) and every original property stay as they were; formatting is
/// delegated to the original formatter.
/// </summary>
internal sealed class TraceCorrelatedLogState<TState> : IReadOnlyList<KeyValuePair<string, object?>>
{
    private readonly TState _state;
    private readonly Func<TState, Exception?, string> _formatter;
    private readonly IReadOnlyList<KeyValuePair<string, object?>> _properties;
    private readonly IReadOnlyList<KeyValuePair<string, object?>> _added;

    internal TraceCorrelatedLogState(
        TState state,
        Func<TState, Exception?, string> formatter,
        IReadOnlyList<KeyValuePair<string, object?>> added)
    {
        _state = state;
        _formatter = formatter;
        _added = added;
        _properties = state as IReadOnlyList<KeyValuePair<string, object?>>
            ?? (state as IEnumerable<KeyValuePair<string, object?>>)?.ToArray()
            ?? [];
    }

    public int Count => _properties.Count + _added.Count;

    public KeyValuePair<string, object?> this[int index]
    {
        get
        {
            if (index >= 0 && index < _properties.Count)
            {
                return _properties[index];
            }

            return index >= _properties.Count && index < Count
                ? _added[index - _properties.Count]
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
