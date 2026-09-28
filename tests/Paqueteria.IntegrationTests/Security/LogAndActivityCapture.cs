using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Paqueteria.IntegrationTests.Security;

// Captures every log message (formatted text and structured state), every scope and every activity of a host, so
// tests can assert that a secret reaches none of them whatever provider, formatter or exporter is configured.

internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> entries = new();

    internal IReadOnlyCollection<string> Entries => entries.ToArray();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, entries);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(
        string category,
        ConcurrentQueue<string> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            entries.Enqueue($"{category} scope {state} {Describe(state)}");
            return null;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            entries.Enqueue(
                $"{category} {logLevel} {eventId} {formatter(state, exception)} {Describe(state)} {exception}");

        private static string Describe<TState>(TState state) =>
            state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? string.Join(" ", pairs.Select(pair => $"{pair.Key}={pair.Value}"))
                : string.Empty;
    }
}

internal sealed class CapturingActivityListener : IDisposable
{
    private readonly ConcurrentQueue<string> entries = new();
    private readonly ActivityListener listener;

    internal CapturingActivityListener(string sourceName)
    {
        listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == sourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) =>
            {
                entries.Enqueue($"sample {options.Name} {Describe(options.Tags)}");
                return ActivitySamplingResult.AllDataAndRecorded;
            },
            ActivityStarted = activity => entries.Enqueue($"start {Describe(activity)}"),
            ActivityStopped = activity => entries.Enqueue($"stop {Describe(activity)}"),
        };
        ActivitySource.AddActivityListener(listener);
    }

    internal IReadOnlyCollection<string> Entries => entries.ToArray();

    public void Dispose() => listener.Dispose();

    private static string Describe(Activity activity) =>
        $"{activity.OperationName} display={activity.DisplayName} status={activity.StatusDescription} "
        + $"tags={Describe(activity.TagObjects)} "
        + $"baggage={string.Join(" ", activity.Baggage.Select(pair => $"{pair.Key}={pair.Value}"))} "
        + $"events={string.Join(" ", activity.Events.Select(item => $"{item.Name}:{Describe(item.Tags)}"))}";

    private static string Describe(IEnumerable<KeyValuePair<string, object?>>? tags) =>
        tags is null ? string.Empty : string.Join(" ", tags.Select(tag => $"{tag.Key}={tag.Value}"));
}
