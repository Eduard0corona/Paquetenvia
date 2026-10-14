using System.Collections.Concurrent;
using Npgsql;
using NpgsqlTypes;

namespace Paqueteria.EndToEndTests;

/// <summary>
/// SCL-007 during the synthetic workflow: samples <c>pg_stat_activity</c> every 100 ms from the test's own
/// administration connection and keeps, per runtime role, the peak number of backends its login holds (the API's
/// <c>paqueteria_app</c> and <c>paqueteria_realtime</c> pools and the Worker's <c>paqueteria_worker</c> pool), and the
/// peak of their sum. Every backend counts, active or idle in its pool. The sampler's own connection (the
/// administration login) and the harness's checks are not runtime logins, so they are not counted, exactly as the
/// budget leaves the migrator, maintenance and administration out.
/// </summary>
internal sealed class ConnectionBudgetSampler : IAsyncDisposable
{
    private const string SampleSql = """
        SELECT usename::text, count(*)::integer
        FROM pg_catalog.pg_stat_activity
        WHERE usename::text = ANY(@logins)
        GROUP BY usename
        """;

    private readonly string _adminConnectionString;
    private readonly IReadOnlyDictionary<string, string> _roleByLogin;
    private readonly ConcurrentDictionary<string, int> _peaks = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private int _peakTotal;
    private int _samples;

    internal ConnectionBudgetSampler(string adminConnectionString, IReadOnlyDictionary<string, string> roleByLogin)
    {
        _adminConnectionString = new NpgsqlConnectionStringBuilder(adminConnectionString)
        {
            ApplicationName = "Paqueteria.OPS005.ConnectionBudgetSampler",
            Pooling = false,
        }.ConnectionString;
        _roleByLogin = roleByLogin;
    }

    internal void Start() => _loop = Task.Run(() => SampleAsync(_stop.Token));

    /// <summary>Stops sampling and returns the peaks per runtime role, the peak total and the number of samples.</summary>
    internal async Task<ConnectionBudgetPeaks> StopAsync()
    {
        await _stop.CancelAsync();
        if (_loop is not null)
        {
            await _loop;
        }

        return new ConnectionBudgetPeaks(
            _roleByLogin.Values.ToDictionary(
                role => role,
                role => _roleByLogin.Where(pair => pair.Value == role).Sum(pair => _peaks.GetValueOrDefault(pair.Key)),
                StringComparer.Ordinal),
            Volatile.Read(ref _peakTotal),
            Volatile.Read(ref _samples));
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_loop is not null)
        {
            try
            {
                await _loop;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _stop.Dispose();
    }

    private async Task SampleAsync(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(SampleSql, connection);
        command.Parameters.Add(new NpgsqlParameter<string[]>("logins", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            TypedValue = _roleByLogin.Keys.ToArray(),
        });
        while (!cancellationToken.IsCancellationRequested)
        {
            var total = 0;
            await using (var reader = await command.ExecuteReaderAsync(CancellationToken.None))
            {
                while (await reader.ReadAsync(CancellationToken.None))
                {
                    var count = reader.GetInt32(1);
                    total += count;
                    _peaks.AddOrUpdate(reader.GetString(0), count, (_, peak) => Math.Max(peak, count));
                }
            }

            InterlockedMax(ref _peakTotal, total);
            Interlocked.Increment(ref _samples);
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static void InterlockedMax(ref int target, int value)
    {
        var current = Volatile.Read(ref target);
        while (value > current)
        {
            var observed = Interlocked.CompareExchange(ref target, value, current);
            if (observed == current)
            {
                return;
            }

            current = observed;
        }
    }
}

internal sealed record ConnectionBudgetPeaks(
    IReadOnlyDictionary<string, int> PeakByRole,
    int PeakTotal,
    int Samples);
