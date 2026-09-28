using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Paqueteria.IntegrationTests.Hosting;

/// <summary>
/// A <see cref="WebApplicationFactory{TEntryPoint}"/> whose startup failures are always the host's own
/// startup error, for tests that assert why a minimal-hosting app refuses to start.
/// </summary>
/// <remarks>
/// For a minimal-hosting app the factory runs the entry point on its own thread and lets its
/// <c>app.Run()</c> start the host. When that start fails (for example an
/// <c>OptionsValidationException</c> from <c>ValidateOnStart</c>), <c>Run</c> disposes the host before
/// rethrowing. The factory waits for the same start from the test thread through its deferred host,
/// which first resolves <see cref="IHostApplicationLifetime"/> from the host's services and only then
/// awaits the entry point's result. When the entry-point thread has already failed and disposed the
/// host, that resolution throws <see cref="ObjectDisposedException"/> and hides the startup error.
/// This factory records the exception the generic host logs when it fails to start, which happens
/// before the disposal, and rethrows it only in that case.
/// </remarks>
public abstract class StartupFailureSurfacingWebApplicationFactory<TEntryPoint> : WebApplicationFactory<TEntryPoint>
    where TEntryPoint : class
{
    private readonly HostStartupFailureRecorder _startupFailures = new();

    /// <summary>Configures the app under test; replaces <see cref="ConfigureWebHost"/>.</summary>
    protected abstract void ConfigureHost(IWebHostBuilder builder);

    protected sealed override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ConfigureHost(builder);

        // Test services are applied after the app's own composition, so the app's
        // Logging.ClearProviders() cannot remove the recorder.
        builder.ConfigureTestServices(services => services.AddSingleton<ILoggerProvider>(_startupFailures));
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        try
        {
            return base.CreateHost(builder);
        }
        catch (ObjectDisposedException) when (_startupFailures.First is { } startupFailure)
        {
            ExceptionDispatchInfo.Throw(startupFailure);
            throw;
        }
    }

    /// <summary>Records the exceptions the generic host logs at error level, such as a failed start.</summary>
    private sealed class HostStartupFailureRecorder : ILoggerProvider
    {
        private const string HostCategory = "Microsoft.Extensions.Hosting.Internal.Host";
        private readonly ConcurrentQueue<Exception> _failures = new();

        public Exception? First => _failures.TryPeek(out var failure) ? failure : null;

        public ILogger CreateLogger(string categoryName) =>
            string.Equals(categoryName, HostCategory, StringComparison.Ordinal)
                ? new Recorder(_failures)
                : NullLogger.Instance;

        public void Dispose()
        {
        }

        private sealed class Recorder(ConcurrentQueue<Exception> failures) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel >= LogLevel.Error && exception is not null)
                {
                    failures.Enqueue(exception);
                }
            }
        }
    }
}
