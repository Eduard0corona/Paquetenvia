namespace Paqueteria.ArchitectureTests.Architecture;

internal static class DependencyGraph
{
    internal static string? FindCycle(IReadOnlyDictionary<string, IReadOnlyList<string>> graph)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var active = new HashSet<string>(StringComparer.Ordinal);
        var path = new List<string>();

        foreach (var node in graph.Keys.Order(StringComparer.Ordinal))
        {
            var cycle = Visit(node, graph, visited, active, path);
            if (cycle is not null)
            {
                return string.Join(" -> ", cycle);
            }
        }

        return null;
    }

    private static IReadOnlyList<string>? Visit(
        string node,
        IReadOnlyDictionary<string, IReadOnlyList<string>> graph,
        HashSet<string> visited,
        HashSet<string> active,
        List<string> path)
    {
        if (active.Contains(node))
        {
            var start = path.IndexOf(node);
            return [.. path.Skip(start), node];
        }

        if (!visited.Add(node))
        {
            return null;
        }

        active.Add(node);
        path.Add(node);

        foreach (var dependency in graph.GetValueOrDefault(node, []))
        {
            var cycle = Visit(dependency, graph, visited, active, path);
            if (cycle is not null)
            {
                return cycle;
            }
        }

        path.RemoveAt(path.Count - 1);
        active.Remove(node);
        return null;
    }
}
