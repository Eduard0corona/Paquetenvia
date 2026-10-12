using Azure.Core;
using Azure.Core.Pipeline;

namespace Paqueteria.Infrastructure.Cloud;

/// <summary>
/// OBS-003: no request to Azure carries trace context. Azure.Core adds <c>traceparent</c> and <c>tracestate</c>
/// (<c>Request-Id</c> for a non-W3C activity) from <c>Activity.Current</c> in its own pipeline, whatever the process
/// propagator says. <c>Diagnostics.IsDistributedTracingEnabled = false</c> does not stop it (it only stops the SDK's
/// own activities), and the managed identity credential's token requests run through a pipeline that ignores
/// custom policies; both are proved by <c>Obs003AzureClientTracingTests</c>. The transport is the one stage every
/// Azure request goes through, so every Azure client and credential options instance in the code (Key Vault secrets,
/// keys and cryptography, Blob Storage, the data protection key resolver and the managed identity credential) goes
/// through <see cref="WithoutDistributedTracing{T}"/>: SDK activities off, and a transport that removes every trace
/// context header right before the request is sent.
/// </summary>
public static class AzureClientTracing
{
    /// <summary>Header names that carry trace context in W3C Trace Context, W3C Baggage or the legacy format.</summary>
    public static readonly IReadOnlyList<string> TraceContextHeaders =
        ["traceparent", "tracestate", "baggage", "Request-Id", "Correlation-Context"];

    /// <summary>
    /// Call after any transport is set on <paramref name="options"/>: the current transport (the SDK default when
    /// none was set) is wrapped, not replaced.
    /// </summary>
    public static T WithoutDistributedTracing<T>(this T options)
        where T : ClientOptions
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Diagnostics.IsDistributedTracingEnabled = false;
        if (options.Transport is not NoTraceContextTransport)
        {
            options.Transport = new NoTraceContextTransport(options.Transport);
        }

        return options;
    }

    private sealed class NoTraceContextTransport(HttpPipelineTransport inner) : HttpPipelineTransport
    {
        public override Request CreateRequest() => inner.CreateRequest();

        public override void Process(HttpMessage message)
        {
            RemoveTraceContext(message);
            inner.Process(message);
        }

        public override ValueTask ProcessAsync(HttpMessage message)
        {
            RemoveTraceContext(message);
            return inner.ProcessAsync(message);
        }

        private static void RemoveTraceContext(HttpMessage message)
        {
            foreach (var header in TraceContextHeaders)
            {
                message.Request.Headers.Remove(header);
            }
        }
    }
}
