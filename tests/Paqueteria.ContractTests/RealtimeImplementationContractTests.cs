using System.Text.Json;
using Paqueteria.ContractTests.Support;
using Realtime.Application.Clients;
using Realtime.Application.Events;
using Realtime.Application.Publishing;
using Realtime.Endpoints;
using YamlDotNet.RepresentationModel;

namespace Paqueteria.ContractTests;

public sealed class RealtimeImplementationContractTests
{
    [Fact]
    public void Hubs_paths_authentication_events_and_empty_client_methods_match_AI12()
    {
        var root = YamlNodes.LoadMapping(
            RepositoryPaths.Normative("contracts", "AI-12_SIGNALR_CONTRACT.yaml"));
        var hubs = root.Sequence("hubs").Children
            .Cast<YamlMappingNode>()
            .ToDictionary(hub => hub.Scalar("name"), StringComparer.Ordinal);

        Assert.Equal(
            ["DriverHub", "OperationsHub", "TrackingHub"],
            hubs.Keys.Order(StringComparer.Ordinal));
        AssertHub<IOperationsClient>(
            hubs["OperationsHub"],
            RealtimeEndpointDefaults.OperationsPath,
            "OIDC access token required");
        AssertHub<IDriverClient>(
            hubs["DriverHub"],
            RealtimeEndpointDefaults.DriverPath,
            "OIDC access token + eligible driver required");
        AssertHub<ITrackingClient>(
            hubs["TrackingHub"],
            RealtimeEndpointDefaults.TrackingPath,
            "short-lived scoped tracking token");
    }

    [Fact]
    public void Envelope_fields_and_event_payloads_match_AI12_exactly()
    {
        var root = YamlNodes.LoadMapping(
            RepositoryPaths.Normative("contracts", "AI-12_SIGNALR_CONTRACT.yaml"));
        Assert.Equal(
            [
                "aggregate_id",
                "aggregate_version",
                "event_id",
                "event_type",
                "occurred_at",
                "payload",
            ],
            root.Mapping("envelope").Sequence("required").Values().Order(StringComparer.Ordinal));

        var payloadTypes = new Dictionary<string, Type>(StringComparer.Ordinal)
        {
            ["OrderStatusChanged"] = typeof(OrderStatusChangedPayload),
            ["PublicOrderStatusChanged"] = typeof(PublicOrderStatusChangedPayload),
            ["OrderTimelineEventAdded"] = typeof(OrderTimelineEventAddedPayload),
            ["AssignmentChanged"] = typeof(AssignmentChangedPayload),
            ["RouteChanged"] = typeof(RouteChangedPayload),
            ["DriverLocationUpdated"] = typeof(DriverLocationUpdatedPayload),
            ["IncidentCreated"] = typeof(IncidentCreatedPayload),
            ["ExternalOfferChanged"] = typeof(ExternalOfferChangedPayload),
            ["NotificationStatusChanged"] = typeof(NotificationStatusChangedPayload),
            ["PublicEtaChanged"] = typeof(PublicEtaChangedPayload),
        };
        var events = root.Mapping("events");
        Assert.Equal(
            payloadTypes.Keys.Order(StringComparer.Ordinal),
            events.Children.Keys.Cast<YamlScalarNode>()
                .Select(key => key.Value!)
                .Order(StringComparer.Ordinal));
        foreach (var (eventName, payloadType) in payloadTypes)
        {
            Assert.Equal(
                events.Mapping(eventName).Sequence("payload").Values().Order(StringComparer.Ordinal),
                payloadType.GetProperties()
                    .Select(property => JsonNamingPolicy.SnakeCaseLower.ConvertName(property.Name))
                    .Order(StringComparer.Ordinal));
        }
    }

    [Fact]
    public void Publisher_and_client_surfaces_enforce_AI12_privacy_audiences()
    {
        Assert.DoesNotContain(
            typeof(IDriverClient).GetMethods(),
            method => method.Name is "DriverLocationUpdated" or "NotificationStatusChanged" or
                "OrderTimelineEventAdded");
        Assert.Equal(
            ["PublicEtaChanged", "PublicOrderStatusChanged"],
            typeof(ITrackingClient).GetMethods()
                .Select(method => method.Name)
                .Order(StringComparer.Ordinal));
        Assert.Single(
            typeof(IRealtimePublisher).GetMethods(),
            method => method.Name.Contains("DriverLocationUpdated", StringComparison.Ordinal));
        Assert.Contains(
            "Operations",
            Assert.Single(
                typeof(IRealtimePublisher).GetMethods(),
                method => method.Name.Contains("DriverLocationUpdated", StringComparison.Ordinal))
                .Name,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Security_configuration_is_in_process_closed_and_token_paths_are_narrow()
    {
        var optionsSource = File.ReadAllText(Path.Combine(
            RepositoryPaths.Root,
            "src", "Modules", "Realtime", "Realtime.Application", "Configuration", "RealtimeOptions.cs"));
        var privateTokenSource = File.ReadAllText(Path.Combine(
            RepositoryPaths.Root,
            "src", "Modules", "Realtime", "Realtime.Endpoints", "Connection",
            "RealtimePrivateAccessTokenMiddleware.cs"));
        var gateSource = File.ReadAllText(Path.Combine(
            RepositoryPaths.Root,
            "src", "Modules", "Realtime", "Realtime.Endpoints", "Connection",
            "RealtimeConnectionGateMiddleware.cs"));
        var programSource = File.ReadAllText(Path.Combine(
            RepositoryPaths.Root,
            "src", "Paqueteria.Api", "Program.cs"));

        Assert.Contains("InProcess", optionsSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Redis", optionsSource, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"access_token\"", privateTokenSource, StringComparison.Ordinal);
        Assert.Contains("RealtimeEndpointDefaults.OperationsPath", privateTokenSource, StringComparison.Ordinal);
        Assert.Contains("RealtimeEndpointDefaults.DriverPath", privateTokenSource, StringComparison.Ordinal);
        Assert.DoesNotContain("RealtimeEndpointDefaults.TrackingPath", privateTokenSource, StringComparison.Ordinal);
        Assert.Contains("RealtimeEndpointDefaults.TrackingPath", gateSource, StringComparison.Ordinal);
        Assert.Contains("UseRealtimePrivateAccessTokens();", programSource, StringComparison.Ordinal);
        Assert.Contains("UseRealtimeConnectionGate();", programSource, StringComparison.Ordinal);
        Assert.DoesNotContain("outbox", programSource, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Realtime_logging_never_formats_tokens_queries_groups_or_identifiers()
    {
        var realtimeRoot = Path.Combine(
            RepositoryPaths.Root,
            "src", "Modules", "Realtime");
        var sources = Directory.GetFiles(realtimeRoot, "*.cs", SearchOption.AllDirectories)
            .Select(File.ReadAllText)
            .ToArray();
        var logCalls = sources
            .SelectMany(source => source.Split('\n'))
            .Where(line => line.Contains("Log", StringComparison.Ordinal) &&
                line.Contains("realtime_", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(logCalls);
        Assert.All(logCalls, line => Assert.DoesNotContain('$', line));
        Assert.DoesNotContain(
            sources,
            source => source.Contains("Request.QueryString", StringComparison.Ordinal));
        Assert.DoesNotContain(
            logCalls,
            line => line.Contains("token", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("group", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("_id", StringComparison.OrdinalIgnoreCase));
    }

    private static void AssertHub<TClient>(
        YamlMappingNode hub,
        string path,
        string authentication)
    {
        Assert.Equal(path, hub.Scalar("path"));
        Assert.Equal(authentication, hub.Scalar("authentication"));
        Assert.Empty(hub.Sequence("client_methods"));
        Assert.Equal(
            hub.Sequence("server_events").Values().Order(StringComparer.Ordinal),
            typeof(TClient).GetMethods()
                .Select(method => method.Name)
                .Order(StringComparer.Ordinal));
    }
}

internal static class RealtimeYamlSequenceExtensions
{
    internal static string[] Values(this YamlSequenceNode sequence) =>
        sequence.Children.Cast<YamlScalarNode>().Select(value => value.Value!).ToArray();
}
