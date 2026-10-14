using Routing.Infrastructure.Routes;

namespace Paqueteria.UnitTests.Routing;

/// <summary>The Routing reader behind addRouteStop.</summary>
public sealed class RoutingPackageDimensionsReaderTests : PackageDimensionsReaderContract
{
    protected override bool TryRead(string json, out int? length, out int? width, out int? height) =>
        PostgreSqlRouteService.TryReadDimensions(json, out length, out width, out height);
}
