using System.Collections.Concurrent;
using Npgsql;
using NpgsqlTypes;

namespace Paqueteria.IntegrationTests.Operations;

/// <summary>
/// SCL-007: samples <c>pg_stat_activity</c> while the OPS-001 synthetic workflow runs and keeps the peak number of
/// backends of each runtime pool the scenario composes (the API's <c>paqueteria_app</c> and <c>paqueteria_realtime</c>
/// pools and the proof worker's pool), told apart by the application name
/// <see cref="Ops001DeliverySimulationFixture"/> gives each pool. The harness's own checks and administration
/// connections carry no such name and are not counted, exactly as the budget leaves them out.
/// </summary>
internal sealed class Ops001ConnectionSampler : IAsyncDisposable
{
    internal const string ApplicationNamePrefix = "Paqueteria.OPS001.Pool.";
    internal const string ApplicationPool = ApplicationNamePrefix + "Application";
    internal const string RealtimePool = ApplicationNamePrefix + "Realtime";
    internal const string ProofWorkerPool = ApplicationNamePrefix + "ProofWorker";

    private const string SampleSql = """
        SELECT application_name, count(*)::integer
        FROM pg_catalog.pg_stat_activity
        WHERE starts_with(application_name, @prefix)
        GROUP BY application_name
        """;

    private readonly string _adminConnectionString;
    private readonly ConcurrentDictionary<string, int> _peaks = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private int _peakTotal;
    private int _samples;

    internal Ops001ConnectionSampler(string adminConnectionString)
    {
        _adminConnectionString = new NpgsqlConnectionStringBuilder(adminConnectionString)
        {
            ApplicationName = "Paqueteria.OPS001.ConnectionSampler",
            Pooling = false,
        }.ConnectionString;
    }

    internal void Start() => _loop = Task.Run(() => SampleAsync(_stop.Token));

    internal async Task<Ops001ConnectionPeaks> StopAsync()
    {
        await _stop.CancelAsync();
        if (_loop is not null)
        {
            await _loop;
        }

        return new Ops001ConnectionPeaks(
            Peak(ApplicationPool),
            Peak(RealtimePool),
            Peak(ProofWorkerPool),
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

    private int Peak(string applicationName) => _peaks.TryGetValue(applicationName, out var peak) ? peak : 0;

    private async Task SampleAsync(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(SampleSql, connection);
        command.Parameters.Add(new NpgsqlParameter<string>("prefix", NpgsqlDbType.Text)
        {
            TypedValue = ApplicationNamePrefix,
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

internal sealed record Ops001ConnectionPeaks(
    int Application,
    int Realtime,
    int ProofWorker,
    int Total,
    int Samples);
