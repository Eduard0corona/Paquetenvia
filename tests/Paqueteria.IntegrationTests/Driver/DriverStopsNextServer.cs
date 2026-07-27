using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Paqueteria.IntegrationTests.Driver;

internal sealed class DriverStopsNextServer : IAsyncDisposable
{
    private static readonly SemaphoreSlim ServerGate = new(1, 1);
    private readonly Process _process;
    private readonly StringBuilder _output;
    private readonly FileStream _crossProcessLease;

    private DriverStopsNextServer(
        Process process,
        Uri baseAddress,
        StringBuilder output,
        FileStream crossProcessLease)
    {
        _process = process;
        BaseAddress = baseAddress;
        _output = output;
        _crossProcessLease = crossProcessLease;
    }

    internal Uri BaseAddress { get; }

    internal bool OutputContains(string value)
    {
        lock (_output)
        {
            return _output.ToString().Contains(value, StringComparison.Ordinal);
        }
    }

    internal static int ReservePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    internal static async Task<DriverStopsNextServer> StartAsync(
        string? apiBaseUrl = null,
        int? requestedPort = null)
    {
        await ServerGate.WaitAsync();
        FileStream? crossProcessLease = null;
        try
        {
            crossProcessLease = await AcquireCrossProcessLeaseAsync();
            return await StartOwnedAsync(
                apiBaseUrl,
                requestedPort,
                crossProcessLease);
        }
        catch
        {
            crossProcessLease?.Dispose();
            ServerGate.Release();
            throw;
        }
    }

    private static async Task<DriverStopsNextServer> StartOwnedAsync(
        string? apiBaseUrl,
        int? requestedPort,
        FileStream crossProcessLease)
    {
        var root = FindRepositoryRoot();
        var web = Path.Combine(root, "apps", "web");
        var port = requestedPort ?? ReservePort();
        var output = new StringBuilder();
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows()
                    ? FindExecutableOnPath("node.exe")
                    : FindExecutableOnPath("node"),
                Arguments =
                    $"\"{FindNextScript(web)}\" dev --hostname 127.0.0.1 --port {port}",
                WorkingDirectory = web,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            },
            EnableRaisingEvents = true,
        };
        if (!string.IsNullOrWhiteSpace(apiBaseUrl))
        {
            process.StartInfo.Environment["NEXT_PUBLIC_API_BASE_URL"] =
                apiBaseUrl.TrimEnd('/');
        }
        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is not null) output.AppendLine(args.Data);
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is not null) output.AppendLine(args.Data);
        };
        if (!process.Start())
        {
            throw new InvalidOperationException("The Next.js development server did not start.");
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var address = new Uri($"http://127.0.0.1:{port}", UriKind.Absolute);
        using var client = new HttpClient { BaseAddress = address };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try
        {
            while (!timeout.IsCancellationRequested)
            {
                if (process.HasExited)
                {
                    throw new InvalidOperationException(
                        $"The Next.js process exited early.{Environment.NewLine}{output}");
                }
                try
                {
                    using var response = await client.GetAsync(
                        "/driver/stops",
                        timeout.Token);
                    if (response.IsSuccessStatusCode)
                    {
                        return new DriverStopsNextServer(
                            process,
                            address,
                            output,
                            crossProcessLease);
                    }
                }
                catch (HttpRequestException)
                {
                }
                await Task.Delay(200, timeout.Token);
            }
        }
        catch
        {
            TryTerminate(process);
            process.Dispose();
            throw;
        }

        TryTerminate(process);
        process.Dispose();
        throw new TimeoutException(
            $"The Next.js development server did not become ready.{Environment.NewLine}{output}");
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            TryTerminate(_process);
            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10))
                .ConfigureAwait(false);
        }
        finally
        {
            _process.Dispose();
            _crossProcessLease.Dispose();
            ServerGate.Release();
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "Paqueteria.sln")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName
            ?? throw new InvalidOperationException("Repository root was not found.");
    }

    private static string FindNextScript(string web)
    {
        var script = Path.Combine(
            web,
            "node_modules",
            "next",
            "dist",
            "bin",
            "next");
        return File.Exists(script)
            ? script
            : throw new FileNotFoundException(
                "The local Next.js CLI was not found. Run pnpm install first.",
                script);
    }

    private static string FindExecutableOnPath(string fileName)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim('"'), fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        throw new FileNotFoundException($"{fileName} was not found on PATH.");
    }

    private static async Task<FileStream> AcquireCrossProcessLeaseAsync()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "paquetenvia-driver-next-server.lock");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        while (true)
        {
            try
            {
                return new FileStream(
                    path,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.None);
            }
            catch (IOException) when (!timeout.IsCancellationRequested)
            {
                await Task.Delay(200, timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                throw new TimeoutException(
                    "Timed out waiting for the isolated Next.js test server lease.");
            }
        }
    }

    private static void TryTerminate(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }
    }
}

public sealed class DriverStopsNextServerFixture : IAsyncLifetime
{
    private DriverStopsNextServer? _server;

    internal Uri BaseAddress =>
        _server?.BaseAddress
        ?? throw new InvalidOperationException("Next.js is not running.");

    public async Task InitializeAsync() =>
        _server = await DriverStopsNextServer.StartAsync();

    public async Task DisposeAsync()
    {
        if (_server is not null)
        {
            await _server.DisposeAsync();
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DriverStopsPwaCollection :
    ICollectionFixture<DriverStopsNextServerFixture>
{
    public const string Name = "DriverStopsPwa";
}
