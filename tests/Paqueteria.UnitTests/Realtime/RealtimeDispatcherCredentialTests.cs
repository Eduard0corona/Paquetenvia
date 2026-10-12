using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Realtime.Application.Configuration;
using Realtime.Infrastructure;
using Realtime.Infrastructure.Dispatching;

namespace Paqueteria.UnitTests.Realtime;

/// <summary>
/// RTM-API-REALTIME-DISPATCHER-ROLE-2026-10-12: with the PostgreSQL dispatcher on, the API must be given the
/// dispatchers' own login in <c>ConnectionStrings:PaqueteriaRealtime</c>; the Worker credential is neither required
/// nor accepted, and a realtime login that is the API's or the Worker's refuses the start.
/// </summary>
public sealed class RealtimeDispatcherCredentialTests
{
    private const string Realtime = "Host=127.0.0.1;Port=1;Database=paqueteria;Username=pv_pilot_realtime;Password=unused";
    private const string Api = "Host=127.0.0.1;Port=1;Database=paqueteria;Username=pv_pilot_api;Password=unused";
    private const string Worker = "Host=127.0.0.1;Port=1;Database=paqueteria;Username=pv_pilot_worker;Password=unused";
    private const string MissingMessage =
        "Realtime:OutboxDispatcher:Provider=PostgreSql requires ConnectionStrings:PaqueteriaRealtime.";
    private const string ReusedMessage =
        "ConnectionStrings:PaqueteriaRealtime must be a valid connection string whose login differs from " +
        "ConnectionStrings:Paqueteria and ConnectionStrings:PaqueteriaWorker.";

    [Fact]
    public void The_dispatchers_name_their_own_connection_and_role()
    {
        Assert.Equal("PaqueteriaRealtime", OutboxDispatcherOptions.ConnectionStringName);
        Assert.Equal("paqueteria_realtime", OutboxDispatcherOptions.DatabaseRole);
    }

    [Fact]
    public void A_dedicated_realtime_login_passes_start_validation()
    {
        var options = Resolve(new()
        {
            ["ConnectionStrings:Paqueteria"] = Api,
            ["ConnectionStrings:PaqueteriaRealtime"] = Realtime,
        });

        Assert.Equal(OutboxDispatcherProviderKind.PostgreSql, options.Provider);
    }

    [Fact]
    public void A_missing_realtime_connection_fails_start_validation_even_with_the_worker_credential()
    {
        var withoutAny = Assert.Throws<OptionsValidationException>(() => Resolve(new()
        {
            ["ConnectionStrings:Paqueteria"] = Api,
        }));
        Assert.Contains(MissingMessage, withoutAny.Failures);

        var withWorker = Assert.Throws<OptionsValidationException>(() => Resolve(new()
        {
            ["ConnectionStrings:Paqueteria"] = Api,
            ["ConnectionStrings:PaqueteriaWorker"] = Worker,
        }));
        Assert.Contains(MissingMessage, withWorker.Failures);

        var blank = Assert.Throws<OptionsValidationException>(() => Resolve(new()
        {
            ["ConnectionStrings:PaqueteriaRealtime"] = "  ",
        }));
        Assert.Contains(MissingMessage, blank.Failures);
    }

    [Theory]
    [InlineData("ConnectionStrings:Paqueteria")]
    [InlineData("ConnectionStrings:PaqueteriaWorker")]
    public void A_realtime_login_shared_with_the_api_or_the_worker_fails_start_validation(string otherKey)
    {
        var failure = Assert.Throws<OptionsValidationException>(() => Resolve(new()
        {
            ["ConnectionStrings:PaqueteriaRealtime"] = Realtime,
            [otherKey] = Realtime.Replace("Port=1", "Port=2", StringComparison.Ordinal),
        }));

        Assert.Contains(ReusedMessage, failure.Failures);
    }

    [Theory]
    [InlineData("Host=127.0.0.1;Port=1;Database=paqueteria;Password=unused")]
    [InlineData("this is not a connection string")]
    [InlineData("Host=127.0.0.1;Port=not-a-number;Username=pv_pilot_realtime")]
    public void A_realtime_connection_without_a_parseable_login_fails_start_validation(string connectionString)
    {
        var failure = Assert.Throws<OptionsValidationException>(() => Resolve(new()
        {
            ["ConnectionStrings:PaqueteriaRealtime"] = connectionString,
        }));

        Assert.Contains(ReusedMessage, failure.Failures);
    }

    [Fact]
    public void The_disabled_dispatcher_needs_no_realtime_connection()
    {
        var options = Resolve(new() { ["Realtime:OutboxDispatcher:Provider"] = "Disabled" });

        Assert.Equal(OutboxDispatcherProviderKind.Disabled, options.Provider);
    }

    [Fact]
    public async Task The_connection_factory_names_the_realtime_connection_when_it_is_missing()
    {
        await using var connections = new RealtimeDispatcherConnectionFactory(string.Empty);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => connections.OpenConnectionAsync(CancellationToken.None).AsTask());

        Assert.Contains("ConnectionStrings:PaqueteriaRealtime", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("PaqueteriaWorker", failure.Message, StringComparison.Ordinal);
    }

    private static OutboxDispatcherOptions Resolve(Dictionary<string, string?> overrides)
    {
        var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Realtime:Provider"] = "SignalR",
            ["Realtime:Backplane"] = "InProcess",
            ["Realtime:OutboxDispatcher:Provider"] = "PostgreSql",
        };
        foreach (var (key, value) in overrides)
        {
            settings[key] = value;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddRealtimeOutboxDispatchers(configuration);
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<OutboxDispatcherOptions>>().Value;
    }
}
