using System.Net;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;

namespace Paqueteria.Api.Http;

/// <summary>
/// Host-level HTTP hardening bound from <c>Http</c>. Every default is the closed one: no CORS
/// origin (the web reaches the API through its own origin), no trusted proxy, and a 1 MiB body.
/// </summary>
public sealed class HttpHardeningOptions
{
    public const string SectionName = "Http";
    public const long DefaultMaximumRequestBodyBytes = 1_048_576;

    public bool UseHttpsRedirection { get; set; }
    public CorsSettings Cors { get; set; } = new();
    public ForwardedHeadersSettings ForwardedHeaders { get; set; } = new();
    public HstsSettings Hsts { get; set; } = new();
    public RequestBodySettings RequestBody { get; set; } = new();

    public sealed class CorsSettings
    {
        public string[] AllowedOrigins { get; set; } = [];
        public int PreflightMaxAgeSeconds { get; set; } = 600;
    }

    public sealed class ForwardedHeadersSettings
    {
        public string[] KnownProxies { get; set; } = [];
        public string[] KnownNetworks { get; set; } = [];
        public int ForwardLimit { get; set; } = 1;
    }

    public sealed class HstsSettings
    {
        public int MaxAgeDays { get; set; } = 365;
        public bool IncludeSubDomains { get; set; } = true;
    }

    public sealed class RequestBodySettings
    {
        public long MaximumBytes { get; set; } = DefaultMaximumRequestBodyBytes;
    }
}

public static class HttpHardening
{
    /// <summary>Methods used by the REST surface. PATCH/OPTIONS/HEAD are not part of it.</summary>
    internal static readonly string[] RestMethods = ["GET", "POST", "PUT", "DELETE"];

    /// <summary>Request headers the REST surface reads; anything else fails preflight.</summary>
    internal static readonly string[] RestHeaders =
        ["Accept", "Authorization", "Content-Type", "Idempotency-Key", "X-Organization-Id"];

    public static WebApplicationBuilder AddHttpHardening(this WebApplicationBuilder builder)
    {
        var section = builder.Configuration.GetSection(HttpHardeningOptions.SectionName);
        var configured = section.Get<HttpHardeningOptions>() ?? new HttpHardeningOptions();
        Validate(configured);

        builder.Services.AddOptions<HttpHardeningOptions>()
            .Bind(section)
            .Validate(
                static options => TryValidate(options) is null,
                "Http hardening configuration is invalid.")
            .ValidateOnStart();

        // Hard ceiling for every request before routing; endpoints that own a larger
        // contractual limit (CSV-001 multipart) raise it per request before reading.
        builder.WebHost.ConfigureKestrel(kestrel =>
            kestrel.Limits.MaxRequestBodySize = configured.RequestBody.MaximumBytes);

        builder.Services.AddCors();
        builder.Services.AddOptions<CorsOptions>()
            .Configure<IOptions<HttpHardeningOptions>>((cors, http) =>
                cors.AddDefaultPolicy(policy => ConfigureRestCors(policy, http.Value.Cors)));

        builder.Services.AddOptions<ForwardedHeadersOptions>()
            .Configure<IOptions<HttpHardeningOptions>>((forwarded, http) =>
                ConfigureForwardedHeaders(forwarded, http.Value.ForwardedHeaders));

        builder.Services.AddHsts(_ => { });
        builder.Services.AddOptions<HstsOptions>()
            .Configure<IOptions<HttpHardeningOptions>>((hsts, http) =>
            {
                hsts.MaxAge = TimeSpan.FromDays(http.Value.Hsts.MaxAgeDays);
                hsts.IncludeSubDomains = http.Value.Hsts.IncludeSubDomains;
                hsts.Preload = false;
            });
        return builder;
    }

    /// <summary>
    /// Must run first: every later component (HSTS, rate limiting, logging) observes the
    /// client address and scheme resolved from the configured trusted proxies only.
    /// </summary>
    public static IApplicationBuilder UseTrustedForwardedHeaders(this IApplicationBuilder app)
    {
        // ForwardedHeadersMiddleware trusts EVERY peer when both known lists are empty, so an
        // empty configuration must not install it at all: forwarded headers are then ignored.
        var settings = app.ApplicationServices
            .GetRequiredService<IOptions<HttpHardeningOptions>>().Value.ForwardedHeaders;
        return settings.KnownProxies.Length == 0 && settings.KnownNetworks.Length == 0
            ? app
            : app.UseForwardedHeaders();
    }

    public static IApplicationBuilder UseRequestBodyLimit(this IApplicationBuilder app) =>
        app.UseMiddleware<RequestBodyLimitMiddleware>();

    internal static void ConfigureRestCors(
        CorsPolicyBuilder policy,
        HttpHardeningOptions.CorsSettings settings)
    {
        if (settings.AllowedOrigins.Length == 0)
        {
            policy.SetIsOriginAllowed(static _ => false);
            return;
        }

        policy
            .WithOrigins(settings.AllowedOrigins)
            .WithMethods(RestMethods)
            .WithHeaders(RestHeaders)
            .AllowCredentials()
            .SetPreflightMaxAge(TimeSpan.FromSeconds(settings.PreflightMaxAgeSeconds));
    }

    internal static void ConfigureForwardedHeaders(
        ForwardedHeadersOptions forwarded,
        HttpHardeningOptions.ForwardedHeadersSettings settings)
    {
        forwarded.ForwardedHeaders =
            ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        forwarded.ForwardLimit = settings.ForwardLimit;
        // The framework trusts loopback by default; an empty configuration trusts nobody.
        forwarded.KnownProxies.Clear();
        forwarded.KnownIPNetworks.Clear();
        foreach (var proxy in settings.KnownProxies)
        {
            forwarded.KnownProxies.Add(IPAddress.Parse(proxy));
        }

        foreach (var network in settings.KnownNetworks)
        {
            forwarded.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
        }
    }

    private static void Validate(HttpHardeningOptions options)
    {
        if (TryValidate(options) is { } failure)
        {
            throw new InvalidOperationException($"Http hardening configuration is invalid: {failure}");
        }
    }

    private static string? TryValidate(HttpHardeningOptions options)
    {
        if (options.RequestBody.MaximumBytes is < 1024 or > 16 * 1_048_576)
        {
            return "Http:RequestBody:MaximumBytes must be between 1 KiB and 16 MiB.";
        }

        if (options.ForwardedHeaders.ForwardLimit is < 1 or > 5)
        {
            return "Http:ForwardedHeaders:ForwardLimit must be between 1 and 5.";
        }

        if (options.Hsts.MaxAgeDays is < 1 or > 730)
        {
            return "Http:Hsts:MaxAgeDays must be between 1 and 730.";
        }

        if (options.Cors.PreflightMaxAgeSeconds is < 0 or > 86_400)
        {
            return "Http:Cors:PreflightMaxAgeSeconds must be between 0 and 86400.";
        }

        foreach (var origin in options.Cors.AllowedOrigins)
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("https" or "http") ||
                !string.Equals(uri.GetLeftPart(UriPartial.Authority), origin, StringComparison.Ordinal))
            {
                return "Http:Cors:AllowedOrigins entries must be exact http(s) origins.";
            }
        }

        foreach (var proxy in options.ForwardedHeaders.KnownProxies)
        {
            if (!IPAddress.TryParse(proxy, out _))
            {
                return "Http:ForwardedHeaders:KnownProxies entries must be IP addresses.";
            }
        }

        foreach (var network in options.ForwardedHeaders.KnownNetworks)
        {
            if (!System.Net.IPNetwork.TryParse(network, out _))
            {
                return "Http:ForwardedHeaders:KnownNetworks entries must be CIDR networks.";
            }
        }

        return null;
    }
}

/// <summary>
/// Applies the host body limit to every endpoint that does not declare its own
/// <see cref="IRequestSizeLimitMetadata"/>. A declared oversized <c>Content-Length</c> is
/// rejected with 413 before authentication or binding; streamed bodies are bounded by the
/// server feature. Multipart endpoints own their contractual file limit (CSV-001 raises the
/// feature for its 1 MiB file plus envelope), so only the feature bound applies to them.
/// </summary>
internal sealed class RequestBodyLimitMiddleware(
    RequestDelegate next,
    IOptions<HttpHardeningOptions> options)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<IRequestSizeLimitMetadata>() is null)
        {
            var limit = options.Value.RequestBody.MaximumBytes;
            var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (feature is { IsReadOnly: false })
            {
                feature.MaxRequestBodySize = limit;
            }

            if (context.Request.ContentLength > limit && !IsMultipart(context.Request))
            {
                await Results.Problem(
                        statusCode: StatusCodes.Status413PayloadTooLarge,
                        title: "Payload Too Large")
                    .ExecuteAsync(context);
                return;
            }
        }

        await next(context);
    }

    private static bool IsMultipart(HttpRequest request) =>
        request.ContentType is { } contentType &&
        contentType.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase);
}
