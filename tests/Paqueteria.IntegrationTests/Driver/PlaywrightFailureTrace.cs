using System.Runtime.CompilerServices;
using Microsoft.Playwright;

namespace Paqueteria.IntegrationTests.Driver;

/// <summary>
/// A Playwright trace of one browser context (screenshots, DOM snapshots and the network log) that is kept only when
/// the test fails. The test calls <see cref="Passed"/> as its last step, also before an early return; a test that never
/// gets there (an assertion or a timeout threw) leaves
/// <c>TestResults/driver-stops-pwa/playwright-traces/&lt;class&gt;.&lt;test&gt;.zip</c> under the repository root, open
/// it with <c>pwsh bin/Release/net10.0/playwright.ps1 show-trace &lt;zip&gt;</c> or at trace.playwright.dev.
/// </summary>
/// <remarks>
/// <para>
/// The driver PWA job runs its <c>dotnet test</c> steps from the repository root with
/// <c>--results-directory ./TestResults/driver-stops-pwa</c> and, when a step fails, uploads that directory as the
/// <c>driver-stops-pwa-results</c> artifact (pr-validation.yml and ci.yml alike), so the trace travels with the TRX.
/// A passing run writes nothing there.
/// </para>
/// <para>
/// Only the BFF suites record one (<see cref="DriverBffSession"/>): every byte their pages show or exchange is synthetic
/// (stops, names, the session answer and its CSRF token are test constants fulfilled in the browser, no cookie is set
/// and nothing reaches a real service); the trace keeps no source files.
/// </para>
/// </remarks>
internal sealed class PlaywrightFailureTrace : IAsyncDisposable
{
    private readonly IBrowserContext _context;
    private readonly string _path;
    private bool _passed;

    private PlaywrightFailureTrace(IBrowserContext context, string path)
    {
        _context = context;
        _path = path;
    }

    internal static string TraceDirectory { get; } = Path.Combine(
        DriverStopsNextServer.FindRepositoryRoot(),
        "TestResults",
        "driver-stops-pwa",
        "playwright-traces");

    /// <param name="context">A context no page has been opened in yet.</param>
    /// <param name="variant">Tells the cases of a theory apart in the file name.</param>
    internal static async Task<PlaywrightFailureTrace> StartAsync(
        IBrowserContext context,
        string? variant = null,
        [CallerFilePath] string testFile = "",
        [CallerMemberName] string test = "")
    {
        var name = $"{Path.GetFileNameWithoutExtension(testFile)}.{test}{(variant is null ? "" : $"({variant})")}";
        await context.Tracing.StartAsync(new TracingStartOptions
        {
            Title = name,
            Screenshots = true,
            Snapshots = true,
            Sources = false,
        });
        return new PlaywrightFailureTrace(context, Path.Combine(TraceDirectory, $"{name}.zip"));
    }

    /// <summary>The test reached its end: the trace is discarded.</summary>
    internal void Passed() => _passed = true;

    public async ValueTask DisposeAsync()
    {
        if (_passed)
        {
            await _context.Tracing.StopAsync();
            return;
        }

        try
        {
            Directory.CreateDirectory(TraceDirectory);
            await _context.Tracing.StopAsync(new TracingStopOptions { Path = _path });
        }
        catch (Exception)
        {
            // The test's own failure is the one to report; a browser that is already gone has no trace to give.
        }
    }
}
