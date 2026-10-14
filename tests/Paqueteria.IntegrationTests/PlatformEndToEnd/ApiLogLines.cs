using System.Text.Json;

namespace Paqueteria.IntegrationTests.PlatformEndToEnd;

/// <summary>Reads the API's JSON console lines (<c>JsonConsoleCapture</c>) as Log Analytics would.</summary>
internal static class ApiLogLines
{
    /// <summary>
    /// The ASP.NET Core hosting "Request starting" lines (category <c>Microsoft.AspNetCore.Hosting.Diagnostics</c>,
    /// event 1): method, path and content type of each request the API received.
    /// </summary>
    internal static IEnumerable<HostingRequest> HostingRequests(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.TryGetProperty("Category", out var category) &&
                category.GetString() == "Microsoft.AspNetCore.Hosting.Diagnostics" &&
                root.TryGetProperty("EventId", out var eventId) &&
                eventId.ValueKind == JsonValueKind.Number &&
                eventId.GetInt32() == 1 &&
                root.TryGetProperty("State", out var state) &&
                state.ValueKind == JsonValueKind.Object)
            {
                yield return new HostingRequest(Text(state, "Method"), Text(state, "Path"), Text(state, "ContentType"));
            }
        }
    }

    private static string Text(JsonElement state, string name) =>
        state.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}

internal sealed record HostingRequest(string Method, string Path, string ContentType);
