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

    public AuthCenterWebApplicationFactory(IReadOnlyDictionary<string, string?>? overrides = null)
    {
        _overrides = overrides ?? new Dictionary<string, string?>();
    }

    public FakeAuthCenterServer AuthCenter { get; } = new();

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
        if (disposing)
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
