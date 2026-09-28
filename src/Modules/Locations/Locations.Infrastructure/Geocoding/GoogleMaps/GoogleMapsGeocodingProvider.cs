using System.Net;
using System.Text;
using System.Text.Json;
using Locations.Application.Geocoding;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Locations.Infrastructure.Geocoding.GoogleMaps;

/// <summary>
/// GATE-003-PROVIDER-GOOGLE: <see cref="IGeocodingProvider"/> backed by the Google Maps Platform
/// Geocoding API (plain <see cref="HttpClient"/> and System.Text.Json, no provider SDK).
/// <para>
/// The client pin stays mandatory (AI-05 <c>CreateLocationRequest</c>) and is validated first. The
/// provider is asked to geocode <c>address_text</c>; its coordinates replace the pin only when the
/// answer is a single, non-partial <c>ROOFTOP</c> or <c>RANGE_INTERPOLATED</c> result. Every other
/// outcome (no match, ambiguous or imprecise match, timeout, HTTP or provider error, open circuit,
/// saturated bulkhead) degrades to the GATE-003 <c>work_allowed</c> behaviour, the manual pin, exactly
/// as <see cref="ManualGeocodingProvider"/> returns it. Caller cancellation is never swallowed.
/// </para>
/// <para>
/// The stored <c>address_summary</c> is always the normalized client summary: the provider's
/// <c>formatted_address</c> is a full street address and is never used. Neither the API key nor the
/// address, the request URI, the response body or the provider's <c>error_message</c> is ever logged;
/// only an outcome code is.
/// </para>
/// </summary>
public sealed partial class GoogleMapsGeocodingProvider : IGeocodingProvider, IDisposable
{
    public const string HttpClientName = "Locations.GoogleMaps.Geocoding";
    public const string ProviderMode = "GOOGLE_MAPS";
    internal const int MaxResponseBytes = 256 * 1024;

    private static readonly HashSet<string> PreciseLocationTypes = new(StringComparer.Ordinal)
    {
        "ROOFTOP",
        "RANGE_INTERPOLATED",
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly GoogleMapsGeocodingOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<GoogleMapsGeocodingProvider> _logger;
    private readonly SemaphoreSlim _bulkhead;
    private readonly Func<int, TimeSpan> _jitter;

    public GoogleMapsGeocodingProvider(
        IHttpClientFactory httpClientFactory,
        IOptions<LocationsOptions> options,
        TimeProvider timeProvider,
        ILogger<GoogleMapsGeocodingProvider> logger)
        : this(httpClientFactory, options, timeProvider, logger, jitter: null)
    {
    }

    /// <summary>Test seam: <paramref name="jitter"/> replaces the random part of the backoff.</summary>
    internal GoogleMapsGeocodingProvider(
        IHttpClientFactory httpClientFactory,
        IOptions<LocationsOptions> options,
        TimeProvider timeProvider,
        ILogger<GoogleMapsGeocodingProvider> logger,
        Func<int, TimeSpan>? jitter)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(options);
        _httpClientFactory = httpClientFactory;
        _options = options.Value.GoogleMaps;
        if (!GoogleMapsGeocodingOptions.IsValid(_options))
        {
            throw new InvalidOperationException("Locations:GoogleMaps is not configured correctly.");
        }

        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _bulkhead = new SemaphoreSlim(_options.MaxConcurrentRequests, _options.MaxConcurrentRequests);
        _jitter = jitter ?? (maxMilliseconds => TimeSpan.FromMilliseconds(Random.Shared.Next(0, maxMilliseconds + 1)));
        CircuitBreaker = new GoogleMapsCircuitBreaker(
            timeProvider,
            _options.CircuitBreakerFailureThreshold,
            _options.BreakDuration);
    }

    internal GoogleMapsCircuitBreaker CircuitBreaker { get; }

    /// <summary>Outcome codes; the only request-derived data that reaches a log line.</summary>
    internal enum Outcome
    {
        Resolved,
        NoAddress,
        ZeroResults,
        Ambiguous,
        Imprecise,
        CircuitOpen,
        ConcurrencyLimit,
        Timeout,
        NetworkError,
        HttpError,
        RateLimited,
        ProviderError,
        Denied,
        InvalidRequest,
        MalformedResponse,
    }

    public async Task<GeocodingResult> GeocodeAsync(GeocodingRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ManualGeocodingProvider.ValidateCoordinates(request.Latitude, request.Longitude);
        var summary = ManualGeocodingProvider.NormalizeSummary(request.AddressSummary);

        var (outcome, location) = await ResolveAsync(request.AddressText, cancellationToken).ConfigureAwait(false);
        if (outcome == Outcome.Resolved && location is { } resolved)
        {
            return new GeocodingResult(summary, resolved.Latitude, resolved.Longitude, ProviderMode, false);
        }

        if (CountsAsFailure(outcome) || outcome is Outcome.CircuitOpen or Outcome.ConcurrencyLimit)
        {
            LogProviderUnavailable(_logger, OutcomeCode(outcome));
        }
        else
        {
            LogManualPinKept(_logger, OutcomeCode(outcome));
        }

        return new GeocodingResult(summary, request.Latitude, request.Longitude, "MANUAL", true);
    }

    public void Dispose() => _bulkhead.Dispose();

    internal static string OutcomeCode(Outcome outcome) => outcome switch
    {
        Outcome.Resolved => "resolved",
        Outcome.NoAddress => "no_address",
        Outcome.ZeroResults => "zero_results",
        Outcome.Ambiguous => "ambiguous",
        Outcome.Imprecise => "imprecise",
        Outcome.CircuitOpen => "circuit_open",
        Outcome.ConcurrencyLimit => "concurrency_limit",
        Outcome.Timeout => "timeout",
        Outcome.NetworkError => "network_error",
        Outcome.HttpError => "http_error",
        Outcome.RateLimited => "rate_limited",
        Outcome.ProviderError => "provider_error",
        Outcome.Denied => "denied",
        Outcome.InvalidRequest => "invalid_request",
        Outcome.MalformedResponse => "malformed_response",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
    };

    private async Task<(Outcome Outcome, (double Latitude, double Longitude)? Location)> ResolveAsync(
        string addressText,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(addressText))
        {
            return (Outcome.NoAddress, null);
        }

        if (!CircuitBreaker.TryAcquire())
        {
            return (Outcome.CircuitOpen, null);
        }

        if (!_bulkhead.Wait(0, CancellationToken.None))
        {
            CircuitBreaker.Release();
            return (Outcome.ConcurrencyLimit, null);
        }

        var settled = false;
        try
        {
            var requestUri = BuildRequestUri(addressText);
            var attempt = 0;
            while (true)
            {
                attempt++;
                var (outcome, location) = await SendOnceAsync(requestUri, cancellationToken).ConfigureAwait(false);
                if (!IsTransient(outcome) || attempt > _options.MaxRetries)
                {
                    if (CountsAsFailure(outcome))
                    {
                        CircuitBreaker.RecordFailure();
                    }
                    else
                    {
                        CircuitBreaker.RecordSuccess();
                    }

                    settled = true;
                    return (outcome, location);
                }

                await Task.Delay(Backoff(attempt), _timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (!settled)
            {
                // Caller cancellation: the provider was not judged, so the circuit is not charged.
                CircuitBreaker.Release();
            }

            _bulkhead.Release();
        }
    }

    private async Task<(Outcome Outcome, (double Latitude, double Longitude)? Location)> SendOnceAsync(
        Uri requestUri,
        CancellationToken cancellationToken)
    {
        using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attemptCancellation.CancelAfter(_options.AttemptTimeout);
        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            using var response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attemptCancellation.Token)
                .ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return (Outcome.RateLimited, null);
            }

            if ((int)response.StatusCode >= 500)
            {
                return (Outcome.HttpError, null);
            }

            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            {
                return (Outcome.Denied, null);
            }

            if (!response.IsSuccessStatusCode)
            {
                return (Outcome.InvalidRequest, null);
            }

            var body = await ReadBoundedAsync(response.Content, attemptCancellation.Token).ConfigureAwait(false);
            return body is null ? (Outcome.MalformedResponse, null) : Parse(body);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return (Outcome.Timeout, null);
        }
        catch (HttpRequestException)
        {
            return (Outcome.NetworkError, null);
        }
        catch (IOException)
        {
            return (Outcome.NetworkError, null);
        }
    }

    internal static (Outcome Outcome, (double Latitude, double Longitude)? Location) Parse(byte[] body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("status", out var statusElement) ||
                statusElement.ValueKind != JsonValueKind.String)
            {
                return (Outcome.MalformedResponse, null);
            }

            switch (statusElement.GetString())
            {
                case "OK":
                    break;
                case "ZERO_RESULTS":
                    return (Outcome.ZeroResults, null);
                case "OVER_QUERY_LIMIT":
                    return (Outcome.RateLimited, null);
                case "UNKNOWN_ERROR":
                    return (Outcome.ProviderError, null);
                case "REQUEST_DENIED":
                case "OVER_DAILY_LIMIT":
                    return (Outcome.Denied, null);
                case "INVALID_REQUEST":
                    return (Outcome.InvalidRequest, null);
                default:
                    return (Outcome.MalformedResponse, null);
            }

            if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            {
                return (Outcome.MalformedResponse, null);
            }

            var count = results.GetArrayLength();
            if (count == 0)
            {
                return (Outcome.ZeroResults, null);
            }

            if (count > 1)
            {
                return (Outcome.Ambiguous, null);
            }

            var result = results[0];
            if (result.ValueKind != JsonValueKind.Object ||
                !result.TryGetProperty("geometry", out var geometry) || geometry.ValueKind != JsonValueKind.Object ||
                !geometry.TryGetProperty("location", out var location) || location.ValueKind != JsonValueKind.Object ||
                !location.TryGetProperty("lat", out var latElement) || latElement.ValueKind != JsonValueKind.Number ||
                !location.TryGetProperty("lng", out var lngElement) || lngElement.ValueKind != JsonValueKind.Number ||
                !latElement.TryGetDouble(out var latitude) || !lngElement.TryGetDouble(out var longitude))
            {
                return (Outcome.MalformedResponse, null);
            }

            if (latitude is < -90 or > 90 || longitude is < -180 or > 180 ||
                !double.IsFinite(latitude) || !double.IsFinite(longitude))
            {
                return (Outcome.MalformedResponse, null);
            }

            var partial = result.TryGetProperty("partial_match", out var partialElement) &&
                partialElement.ValueKind == JsonValueKind.True;
            var precise = geometry.TryGetProperty("location_type", out var typeElement) &&
                typeElement.ValueKind == JsonValueKind.String &&
                PreciseLocationTypes.Contains(typeElement.GetString()!);
            return partial || !precise
                ? (Outcome.Imprecise, null)
                : (Outcome.Resolved, (latitude, longitude));
        }
        catch (JsonException)
        {
            return (Outcome.MalformedResponse, null);
        }
    }

    internal Uri BuildRequestUri(string addressText)
    {
        var query = new StringBuilder("maps/api/geocode/json?address=")
            .Append(Uri.EscapeDataString(addressText.Trim()));
        if (_options.ComponentsCountry.Length > 0)
        {
            query.Append("&components=").Append(Uri.EscapeDataString("country:" + _options.ComponentsCountry));
        }

        if (_options.Region.Length > 0)
        {
            query.Append("&region=").Append(_options.Region);
        }

        query.Append("&key=").Append(Uri.EscapeDataString(_options.ApiKey));
        return new Uri(new Uri(_options.BaseUri, UriKind.Absolute), query.ToString());
    }

    private static bool IsTransient(Outcome outcome) =>
        outcome is Outcome.Timeout or Outcome.NetworkError or Outcome.HttpError or Outcome.RateLimited or Outcome.ProviderError;

    /// <summary>
    /// Provider health, not address quality: a no-match, an ambiguous or imprecise answer or an
    /// invalid request for one address is a healthy provider and never opens the circuit.
    /// </summary>
    private static bool CountsAsFailure(Outcome outcome) =>
        IsTransient(outcome) || outcome is Outcome.Denied or Outcome.MalformedResponse;

    private TimeSpan Backoff(int attempt)
    {
        var baseMilliseconds = _options.RetryBaseDelayMilliseconds * (1 << (attempt - 1));
        return TimeSpan.FromMilliseconds(baseMilliseconds) + _jitter(baseMilliseconds / 2);
    }

    private static async Task<byte[]?> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaxResponseBytes)
        {
            return null;
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxResponseBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    [LoggerMessage(EventId = 4301, Level = LogLevel.Warning,
        Message = "Google Maps geocoding is unavailable; the manual pin is used ({Outcome}).")]
    private static partial void LogProviderUnavailable(ILogger logger, string outcome);

    [LoggerMessage(EventId = 4302, Level = LogLevel.Information,
        Message = "Google Maps geocoding gave no precise single match; the manual pin is used ({Outcome}).")]
    private static partial void LogManualPinKept(ILogger logger, string outcome);
}
