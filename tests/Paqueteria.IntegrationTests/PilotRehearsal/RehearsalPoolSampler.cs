using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Npgsql;
using Paqueteria.IntegrationTests.PlatformEndToEnd;

namespace Paqueteria.IntegrationTests.PilotRehearsal;

/// <summary>
/// Reported only, never part of the evidence: how close the runtime pools came to their SCL-007 size while the
/// rehearsal ran. It listens to Npgsql's own meter in this process (the API and the Worker run here) and samples the
/// observable instruments every 100 ms: connections in use, requests waiting for a connection, and timeouts. A pool is
/// known only by the runtime role of its login; its name (a connection string without the password) is never printed.
/// </summary>
internal sealed class RehearsalPoolSampler : IAsyncDisposable
{
    private const string Meter = "Npgsql";
    private const string PoolNameTag = "db.client.connection.pool.name";
    private const string StateTag = "db.client.connection.state";

    private readonly Dictionary<string, string> _roles;
    private readonly ConcurrentDictionary<(string Role, string Measure), long> _peaks = new();
    private readonly ConcurrentDictionary<string, long> _sample = new();
    private readonly ConcurrentDictionary<string, string?> _poolRoles = new(StringComparer.Ordinal);
    private readonly MeterListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _sampling;

    internal RehearsalPoolSampler(PlatformEndToEndFixture fixture)
    {
        _roles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [Username(fixture.Database.ApplicationConnectionString)] = "paqueteria_app",
            [Username(fixture.Database.RealtimeConnectionString)] = "paqueteria_realtime",
            [Username(fixture.Database.WorkerConnectionString)] = "paqueteria_worker",
        };
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == Meter && instrument.Name is
                    "db.client.connection.count" or "db.client.connection.max" or
                    "db.client.connection.npgsql.pending_requests" or "db.client.connection.npgsql.timeouts")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<int>((instrument, value, tags, _) => Record(instrument, value, tags));
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument, value, tags));
        _listener.Start();
        _sampling = SampleAsync(_stop.Token);
    }

    /// <summary>
    /// Stops sampling and returns one line per runtime role whose pool was seen: peak in use of its maximum, peak
    /// waiting, timeouts.
    /// </summary>
    internal async Task<IReadOnlyList<string>> StopAsync()
    {
        await StopSamplingAsync();
        _listener.RecordObservableInstruments();
        Fold();
        var lines = _roles.Values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Where(role => _peaks.ContainsKey((role, "max")))
            .Select(role =>
                $"{role} pool: peak {Peak(role, "used")} of {Peak(role, "max")} connections in use, " +
                $"peak {Peak(role, "pending")} requests waiting for a connection, {Peak(role, "timeouts")} timeouts")
            .ToArray();
        return lines.Length > 0 ? lines : ["no Npgsql pool measurement of a runtime login was seen"];
    }

    public async ValueTask DisposeAsync()
    {
        await StopSamplingAsync();
        _listener.Dispose();
        _stop.Dispose();
    }

    private async Task StopSamplingAsync()
    {
        if (!_stop.IsCancellationRequested)
        {
            await _stop.CancelAsync();
        }

        try
        {
            await _sampling;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static string Username(string connectionString) =>
        new NpgsqlConnectionStringBuilder(connectionString).Username!;

    /// <summary>The login of a pool, from its name (Npgsql names a pool by its connection string without password).</summary>
    private static string? UsernameOfPool(string pool)
    {
        try
        {
            return new NpgsqlConnectionStringBuilder(pool).Username;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private async Task SampleAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            _listener.RecordObservableInstruments();
            Fold();
        }
    }

    /// <summary>One sample: the values of every pool of a role, summed, raise that role's peaks.</summary>
    private void Fold()
    {
        foreach (var (key, value) in _sample.ToArray())
        {
            var separator = key.IndexOf('|', StringComparison.Ordinal);
            var role = key[..separator];
            var measure = key[(separator + 1)..];
            _peaks.AddOrUpdate((role, measure), value, (_, current) => Math.Max(current, value));
        }

        _sample.Clear();
    }

    private void Record(Instrument instrument, long value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        string? pool = null;
        string? state = null;
        foreach (var tag in tags)
        {
            if (tag.Key == PoolNameTag)
            {
                pool = tag.Value as string;
            }
            else if (tag.Key == StateTag)
            {
                state = tag.Value as string;
            }
        }

        var role = pool is null
            ? null
            : _poolRoles.GetOrAdd(pool, name => UsernameOfPool(name) is { } username &&
                                                _roles.TryGetValue(username, out var known) ? known : null);
        if (role is null)
        {
            return;
        }

        var measure = instrument.Name switch
        {
            "db.client.connection.count" when state == "used" => "used",
            "db.client.connection.max" => "max",
            "db.client.connection.npgsql.pending_requests" => "pending",
            "db.client.connection.npgsql.timeouts" => "timeouts",
            _ => null,
        };
        if (measure is null)
        {
            return;
        }

        if (measure == "timeouts")
        {
            // A counter: every timeout adds one, so the peak is the running total.
            _peaks.AddOrUpdate((role, measure), value, (_, current) => current + value);
            return;
        }

        _sample.AddOrUpdate($"{role}|{measure}", value, (_, current) => current + value);
    }

    private long Peak(string role, string measure) => _peaks.TryGetValue((role, measure), out var value) ? value : 0;
}
