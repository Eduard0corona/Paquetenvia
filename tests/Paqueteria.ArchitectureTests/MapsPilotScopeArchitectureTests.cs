using System.Text.RegularExpressions;
using Paqueteria.ArchitectureTests.Architecture;

namespace Paqueteria.ArchitectureTests;

/// <summary>
/// GATE-003-MAPS-PILOT-RULES-2026-10-02 (owner: no routes/ETAs in the pilot): production code calls
/// only the Google Geocoding API. No Directions, Routes or Distance Matrix endpoint, and no routing or
/// ETA provider port, exists anywhere under <c>src</c>. The internal Routing module (manual driver
/// routes over PostgreSQL, <c>IRouteService</c>) is not a provider port and stays allowed.
/// </summary>
public sealed partial class MapsPilotScopeArchitectureTests
{
    private static readonly string[] BannedEndpointFragments =
    [
        "maps/api/directions",
        "maps/api/distancematrix",
        "routes.googleapis.com",
        "routeoptimization.googleapis.com",
        "computeRoutes",
        "computeRouteMatrix",
        "DistanceMatrix",
        "directions/json",
        "IRoutingProvider",
        "IEtaProvider",
    ];

    [Fact]
    public void Src_never_references_a_directions_routes_or_distance_matrix_endpoint_or_routing_port()
    {
        var offenders = SourceFiles()
            .SelectMany(file => BannedEndpointFragments
                .Where(fragment => file.Text.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                .Select(fragment => $"{file.Relative}: {fragment}"))
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Every_google_maps_api_path_in_src_is_the_geocoding_endpoint()
    {
        var files = SourceFiles().ToArray();
        var offenders = files
            .SelectMany(file => MapsApiPath().Matches(file.Text)
                .Select(match => match.Value)
                .Where(path => !string.Equals(path, "maps/api/geocode/json", StringComparison.Ordinal))
                .Select(path => $"{file.Relative}: {path}"))
            .ToArray();

        Assert.Empty(offenders);
        Assert.Contains(files, file => file.Text.Contains("\"maps/api/geocode/json\"", StringComparison.Ordinal));
    }

    [Fact]
    public void No_production_assembly_declares_a_routing_eta_directions_or_distance_matrix_provider_port()
    {
        var offenders = SolutionCatalog.All
            .SelectMany(component => component.Assembly.GetTypes())
            .Where(type => type.IsInterface || type.IsClass)
            .Where(type => RoutingPortName().IsMatch(type.Name))
            .Select(type => type.FullName ?? type.Name)
            .ToArray();

        Assert.Empty(offenders);

        var declarations = SourceFiles()
            .Where(file => RoutingPortDeclaration().IsMatch(file.Text))
            .Select(file => file.Relative)
            .ToArray();
        Assert.Empty(declarations);
    }

    private static IEnumerable<(string Relative, string Text)> SourceFiles()
    {
        var root = TestRepository.GetPath("src");
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.Ordinal) ||
                path.EndsWith(".json", StringComparison.Ordinal) ||
                path.EndsWith(".csproj", StringComparison.Ordinal))
            .Where(path =>
            {
                var segments = Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar);
                return !segments.Contains("bin") && !segments.Contains("obj");
            })
            .Select(path => (Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'), File.ReadAllText(path)));
    }

    [GeneratedRegex(@"maps/api/[A-Za-z0-9_/]+", RegexOptions.CultureInvariant)]
    private static partial Regex MapsApiPath();

    [GeneratedRegex(@"^I?\w*(RoutingProvider|EtaProvider|Directions\w*Provider|DistanceMatrix|RouteOptimi[sz]\w*Provider)\w*$", RegexOptions.CultureInvariant)]
    private static partial Regex RoutingPortName();

    [GeneratedRegex(@"\b(interface|class|record)\s+I?\w*(RoutingProvider|EtaProvider|Directions\w*Provider|DistanceMatrix)\w*", RegexOptions.CultureInvariant)]
    private static partial Regex RoutingPortDeclaration();
}
