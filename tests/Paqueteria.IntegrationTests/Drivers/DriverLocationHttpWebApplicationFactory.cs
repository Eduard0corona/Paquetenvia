using System.Collections.Concurrent;
using Drivers.Application.Locations;
using Drivers.Domain.Location;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Paqueteria.IntegrationTests.Drivers;

public sealed class DriverLocationHttpWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly int permitLimit;
    internal static readonly Guid DriverActorId =
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaa11");
    internal static readonly Guid MissingProfileEventId =
        Guid.Parse("d3000000-0000-0000-0000-000000000001");
    internal static readonly IReadOnlySet<Guid> InaccessibleProfileEventIds = new HashSet<Guid>
    {
        MissingProfileEventId,
        Guid.Parse("d3000000-0000-0000-0000-000000000002"),
        Guid.Parse("d3000000-0000-0000-0000-000000000003"),
        Guid.Parse("d3000000-0000-0000-0000-000000000004"),
        Guid.Parse("d3000000-0000-0000-0000-000000000005"),
    };
    private readonly StubService service = new();

    public DriverLocationHttpWebApplicationFactory() : this(100)
    {
    }

    internal DriverLocationHttpWebApplicationFactory(int permitLimit)
    {
        this.permitLimit = permitLimit;
    }

    internal int Invocations => service.Invocations;
    internal IReadOnlyList<Guid> LastClientEventIds => service.LastClientEventIds;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration(configuration =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Provider"] = "Mock",
                ["IdentityBootstrap:Provider"] = "Mock",
                ["Drivers:LocationIngestion:BatchPermitLimit"] = permitLimit.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                ["Drivers:LocationIngestion:WindowSeconds"] = "60",
            }));
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IDriverLocationIngestionService>();
            services.AddSingleton<IDriverLocationIngestionService>(service);
        });
    }

    private sealed class StubService : IDriverLocationIngestionService
    {
        private readonly ConcurrentDictionary<Guid, Guid> positions = new();
        private int invocations;
        private Guid[] lastClientEventIds = [];

        public int Invocations => Volatile.Read(ref invocations);
        public IReadOnlyList<Guid> LastClientEventIds => Volatile.Read(ref lastClientEventIds);

        public Task<DriverLocationBatchResult> PublishAsync(
            PublishDriverLocationBatchCommand command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref invocations);
            Volatile.Write(
                ref lastClientEventIds,
                command.Positions.Select(value => value.ClientEventId).ToArray());
            if (command.ActorId != DriverActorId)
            {
                throw new DriverLocationForbiddenException();
            }

            if (command.Positions.Any(value =>
                    InaccessibleProfileEventIds.Contains(value.ClientEventId)))
            {
                throw new DriverLocationNotFoundException();
            }

            var items = new List<DriverLocationItemResult>();
            foreach (var value in command.Positions)
            {
                var validation = DriverLocationValidationPolicy.Validate(new DriverLocationInput(
                    value.ClientEventId,
                    value.Latitude,
                    value.Longitude,
                    value.AccuracyMeters,
                    value.CapturedAt,
                    value.HeadingDegrees,
                    value.SpeedMetersPerSecond));
                if (validation.Location is not { } location)
                {
                    items.Add(new(
                        value.ClientEventId,
                        null,
                        DriverLocationItemStatus.Rejected,
                        validation.RejectionCode));
                    continue;
                }

                var created = Guid.NewGuid();
                var positionId = positions.GetOrAdd(location.ClientEventId, created);
                items.Add(new(
                    location.ClientEventId,
                    positionId,
                    positionId == created
                        ? DriverLocationItemStatus.Accepted
                        : DriverLocationItemStatus.Duplicate,
                    null));
            }

            return Task.FromResult(new DriverLocationBatchResult(items));
        }
    }
}

internal sealed class DisabledDriverLocationWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration(configuration =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Provider"] = "Mock",
                ["IdentityBootstrap:Provider"] = "Mock",
                ["Drivers:Provider"] = "Disabled",
            }));
    }
}
