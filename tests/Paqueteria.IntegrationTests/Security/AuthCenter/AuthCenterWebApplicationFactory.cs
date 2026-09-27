using System.Collections.Concurrent;
using Identity.Application.Bootstrap;
using Identity.Endpoints.AuthCenter;
using Identity.Infrastructure.Mock;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Paqueteria.IntegrationTests.Security.AuthCenter;

internal sealed class AuthCenterWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly IReadOnlyDictionary<string, string?> _overrides;
    private readonly bool _ownsAuthCenter;

    public AuthCenterWebApplicationFactory(IReadOnlyDictionary<string, string?>? overrides = null)
        : this(overrides, sharedAuthCenter: null)
    {
    }

    /// <summary>
    /// A shared <paramref name="sharedAuthCenter"/> lets several API instances trust the same fake IdP,
    /// as replicas behind one ingress do; the factory that created it disposes it.
    /// </summary>
    public AuthCenterWebApplicationFactory(
        IReadOnlyDictionary<string, string?>? overrides,
        FakeAuthCenterServer? sharedAuthCenter)
    {
        _overrides = overrides ?? new Dictionary<string, string?>();
        _ownsAuthCenter = sharedAuthCenter is null;
        AuthCenter = sharedAuthCenter ?? new FakeAuthCenterServer();
    }

    public FakeAuthCenterServer AuthCenter { get; }

    public SwitchableIdentityContextResolver Resolver { get; } = new();

    public HttpClient CreateBrowser() => CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        HandleCookies = true,
        BaseAddress = new Uri(FakeAuthCenterServer.PublicOrigin),
    });

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration(configuration =>
        {
            var values = new Dictionary<string, string?>
            {
                ["Authentication:Provider"] = "AuthCenter",
                ["IdentityBootstrap:Provider"] = "Mock",
                ["AuthCenter:Authority"] = FakeAuthCenterServer.Authority,
                ["AuthCenter:Issuer"] = FakeAuthCenterServer.Issuer,
                ["AuthCenter:ClientId"] = FakeAuthCenterServer.ClientId,
                ["AuthCenter:ClientSecret"] = FakeAuthCenterServer.ClientSecret,
                ["AuthCenter:PublicOrigin"] = FakeAuthCenterServer.PublicOrigin,
                // These tests run without PostgreSQL; the PostgreSQL store has its own suite.
                ["AuthCenter:SessionStore"] = "Memory",
            };
            foreach (var (key, value) in _overrides)
            {
                values[key] = value;
            }

            configuration.AddInMemoryCollection(values);
        });
        builder.ConfigureTestServices(services =>
        {
            services.Configure<OpenIdConnectOptions>(
                AuthCenterDefaults.OpenIdConnectScheme,
                options => options.BackchannelHttpHandler = AuthCenter);
            services.RemoveAll<IIdentityContextResolver>();
            services.AddSingleton<IIdentityContextResolver>(Resolver);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && _ownsAuthCenter)
        {
            AuthCenter.Dispose();
        }
    }
}

/// <summary>Mock resolver whose subjects can be suspended at runtime to prove per-request re-resolution.</summary>
internal sealed class SwitchableIdentityContextResolver : IIdentityContextResolver
{
    private readonly MockIdentityContextResolver _inner = new();

    public ConcurrentDictionary<string, bool> Suspended { get; } = new(StringComparer.Ordinal);

    public ConcurrentQueue<string> ResolvedSubjects { get; } = new();

    public ValueTask<IdentityContextResolution> ResolveAsync(string identitySubject, CancellationToken cancellationToken)
    {
        ResolvedSubjects.Enqueue(identitySubject);
        return Suspended.ContainsKey(identitySubject)
            ? ValueTask.FromResult(IdentityContextResolution.NoAuthorizedContext)
            : _inner.ResolveAsync(identitySubject, cancellationToken);
    }
}
