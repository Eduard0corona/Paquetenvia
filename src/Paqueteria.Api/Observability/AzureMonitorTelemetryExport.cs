using System.Diagnostics;
using Azure.Core.Pipeline;
using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Paqueteria.Infrastructure.Cloud;
using Paqueteria.Infrastructure.Observability;

namespace Paqueteria.Hosting.Observability;

/// <summary>
/// OBS-003 phase 4 PR 6 (OBS-003-TELEMETRY-APPINSIGHTS-2026-10-12): traces and metrics to a workspace-based
/// Application Insights through the official Azure Monitor exporter, off by default. The API compiles this file and
/// the Worker links it (<c>Paqueteria.Worker.csproj</c>), because only the two hosts reference the OpenTelemetry
/// packages.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>With <c>TelemetryExport:Enabled</c> false (the default) nothing is registered and the OpenTelemetry SDK is never
/// loaded: no provider, no exporter, no SDK self-diagnostics and no network. Logs keep going to the console only; no log
/// exporter exists.</item>
/// <item>The exporter classes are used directly, not <c>AddAzureMonitorTraceExporter</c>, which would replace the sampler
/// with its own and add standard-metrics extraction. Live Metrics, Statsbeat, customer SDK stats (which also probe the
/// Azure instance metadata endpoint), standard metrics, performance counters, the resource metric and the local retry
/// storage are off, so the only destination is the configured Application Insights and it receives only the spans and
/// metrics below.</item>
/// <item>Sampling: <see cref="ParentBasedSampler"/> over a root filter and <see cref="TraceIdRatioBasedSampler"/> at
/// 10 %. The decision is made once per trace, from its trace id, and every child follows its parent. Only the roots
/// <see cref="TelemetryExportPolicy.IsExportedRoot"/> names can be sampled; a remote parent is never trusted.</item>
/// <item>Propagation stays a no-op: with the export on, the OpenTelemetry default propagator is set to one that does
/// nothing (with it off the SDK never replaces the API's no-op default), and the process and DI propagators of
/// <see cref="TraceCorrelation"/> are not touched either way. The PR 1 propagation-only listener stays, so the traces
/// this sampler drops still have W3C ids.</item>
/// <item>Ingestion uses the instrumentation key of the connection string (Q-PR6-2, "Llave en la conexión
/// (Recommended)"); the exporter's requests go through <see cref="AzureClientTracing.WithoutDistributedTracing{T}"/>.</item>
/// </list>
/// </remarks>
public static class AzureMonitorTelemetryExport
{
    /// <summary>
    /// Test seam: a keyed <see cref="HttpPipelineTransport"/> registered under this key replaces the exporter's network
    /// transport. Production registers none.
    /// </summary>
    public const string TransportServiceKey = "Paqueteria.TelemetryExport.Transport";

    /// <summary>Metric export interval (the exporter's own default).</summary>
    public const int MetricExportIntervalMilliseconds = 60_000;

    /// <summary>The documented switch that keeps Statsbeat, the exporter's own telemetry to Microsoft, off.</summary>
    public const string StatsbeatDisabledVariable = "APPLICATIONINSIGHTS_STATSBEAT_DISABLED";

    /// <summary>
    /// The switch that keeps customer SDK stats off (on by default since exporter 1.8.3): counts of the exporter's own
    /// items sent to the customer's resource, whose first transmission also asks the Azure instance metadata endpoint
    /// (<c>169.254.169.254</c>) for the compute type.
    /// </summary>
    public const string SdkStatsDisabledVariable = "APPLICATIONINSIGHTS_SDKSTATS_DISABLED";

    /// <summary>
    /// The exporter's switch for its resource metric (<c>_OTELRESOURCE_</c>, one extra item with the resource
    /// attributes per trace batch); off, because the role name already says which host sent each item.
    /// </summary>
    public const string ResourceMetricVariable = "OTEL_DOTNET_AZURE_MONITOR_ENABLE_RESOURCE_METRICS";

    /// <summary>
    /// Reads and validates <c>TelemetryExport</c> (the host fails to start on an invalid value) and, only when it is on,
    /// registers the OpenTelemetry tracer and meter providers with the Azure Monitor exporter.
    /// </summary>
    public static WebApplicationBuilder AddAzureMonitorTelemetryExport(
        this WebApplicationBuilder builder,
        TelemetryExportProfile profile)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(profile);
        var settings = TelemetryExportSettings.Read(builder.Configuration);
        if (!settings.Enabled)
        {
            // Off means no OpenTelemetry SDK at all; TraceCorrelation's no-op propagators already cover the process.
            return builder;
        }

        // Loading the SDK sets the OpenTelemetry default propagator (which instrumentation libraries would use) to W3C
        // trace context and baggage; replace it with one that extracts and injects nothing, as the process and hosting
        // propagators are (TraceCorrelation).
        Sdk.SetDefaultTextMapPropagator(new CompositeTextMapPropagator([]));

        // Read once by the exporter (it caches them) when the first exporter of this process is built, which is later.
        Environment.SetEnvironmentVariable(StatsbeatDisabledVariable, "true");
        Environment.SetEnvironmentVariable(SdkStatsDisabledVariable, "true");
        Environment.SetEnvironmentVariable(ResourceMetricVariable, "false");
        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(profile.ServiceName, autoGenerateServiceInstanceId: false))
            .WithTracing(tracing => tracing
                .AddPaqueteriaTracing(profile)
                .AddProcessor(provider => new BatchActivityExportProcessor(
                    new AzureMonitorTraceExporter(CreateExporterOptions(settings, provider)))))
            .WithMetrics(metrics => metrics
                .AddPaqueteriaMetrics(profile)
                .AddReader(provider => new PeriodicExportingMetricReader(
                    new AzureMonitorMetricExporter(CreateExporterOptions(settings, provider)),
                    MetricExportIntervalMilliseconds)
                {
                    TemporalityPreference = MetricReaderTemporalityPreference.Delta,
                }));
        return builder;
    }

    /// <summary>
    /// The trace pipeline before the exporter: the profile's sources, the sampler and the attribute allowlist. The
    /// production exporter and the tests' exporters are added after it, so they only ever see prepared spans.
    /// </summary>
    public static TracerProviderBuilder AddPaqueteriaTracing(this TracerProviderBuilder builder, TelemetryExportProfile profile)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(profile);
        foreach (var source in profile.TraceSources)
        {
            builder.AddSource(source);
        }

        return builder
            .SetSampler(CreateSampler())
            .AddProcessor(new SpanAllowlistProcessor());
    }

    /// <summary>The profile's meters, each instrument keeping only the allowlisted tag keys.</summary>
    public static MeterProviderBuilder AddPaqueteriaMetrics(this MeterProviderBuilder builder, TelemetryExportProfile profile)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(profile);
        var tagKeys = TelemetryExportPolicy.MetricTagKeys.Order(StringComparer.Ordinal).ToArray();
        return builder
            .AddMeter([.. profile.Meters])
            .AddView(_ => new MetricStreamConfiguration { TagKeys = tagKeys });
    }

    /// <summary>
    /// Parent based: a local parent's decision is kept; a remote parent (which inbound propagation never creates)
    /// is dropped whatever its flag says; a root is sampled at 10 % only when it is an exported root.
    /// </summary>
    public static Sampler CreateSampler() => new ParentBasedSampler(
        new ExportedRootSampler(new TraceIdRatioBasedSampler(TelemetryExportPolicy.TraceSamplingRatio)),
        remoteParentSampled: new AlwaysOffSampler(),
        remoteParentNotSampled: new AlwaysOffSampler(),
        localParentSampled: new AlwaysOnSampler(),
        localParentNotSampled: new AlwaysOffSampler());

    /// <summary>The exporter options, every value set here last (environment and configuration cannot change them).</summary>
    internal static AzureMonitorExporterOptions CreateExporterOptions(TelemetryExportSettings settings, IServiceProvider provider)
    {
        var options = new AzureMonitorExporterOptions
        {
            ConnectionString = settings.ConnectionString,
            Credential = null,
            SamplingRatio = (float)TelemetryExportPolicy.TraceSamplingRatio,
            TracesPerSecond = null,
            EnableLiveMetrics = false,
            EnableStandardMetrics = false,
            EnablePerformanceCounters = false,
            EnableTraceBasedLogsSampler = false,
            DisableOfflineStorage = true,
        };
        if (provider.GetKeyedService<HttpPipelineTransport>(TransportServiceKey) is { } transport)
        {
            options.Transport = transport;
        }

        return options.WithoutDistributedTracing();
    }

    /// <summary>Only the roots that may be exported reach the ratio sampler; every other root is dropped.</summary>
    public sealed class ExportedRootSampler(Sampler ratio) : Sampler
    {
        private static readonly SamplingResult Drop = new(SamplingDecision.Drop);

        public override SamplingResult ShouldSample(in SamplingParameters samplingParameters) =>
            TelemetryExportPolicy.IsExportedRoot(samplingParameters.Name, samplingParameters.Kind)
                ? ratio.ShouldSample(samplingParameters)
                : Drop;
    }

    /// <summary>
    /// Runs first when a sampled span ends: <see cref="TelemetryExportPolicy.PrepareForExport"/> keeps only the
    /// allowlisted attributes, or takes the span out of the export.
    /// </summary>
    public sealed class SpanAllowlistProcessor : BaseProcessor<Activity>
    {
        public override void OnEnd(Activity data)
        {
            ArgumentNullException.ThrowIfNull(data);
            TelemetryExportPolicy.PrepareForExport(data);
        }
    }
}
