using YamlDotNet.RepresentationModel;

namespace Paqueteria.ContractTests.Support;

internal static class YamlNodes
{
    public static YamlNode Required(this YamlMappingNode mapping, string key)
    {
        Assert.True(mapping.Children.TryGetValue(new YamlScalarNode(key), out var value), $"Missing YAML key '{key}'.");
        return value!;
    }

    public static string Scalar(this YamlMappingNode mapping, string key) =>
        Assert.IsType<YamlScalarNode>(mapping.Required(key)).Value!;

    public static YamlMappingNode Mapping(this YamlMappingNode mapping, string key) =>
        Assert.IsType<YamlMappingNode>(mapping.Required(key));

    public static YamlSequenceNode Sequence(this YamlMappingNode mapping, string key) =>
        Assert.IsType<YamlSequenceNode>(mapping.Required(key));

    public static YamlMappingNode LoadMapping(string path)
    {
        using var reader = File.OpenText(path);
        var stream = new YamlStream();
        stream.Load(reader);
        Assert.Single(stream.Documents);
        return Assert.IsType<YamlMappingNode>(stream.Documents[0].RootNode);
    }

    public static IEnumerable<YamlNode> DescendantsAndSelf(YamlNode node)
    {
        yield return node;
        switch (node)
        {
            case YamlMappingNode mapping:
                foreach (var child in mapping.Children)
                {
                    foreach (var descendant in DescendantsAndSelf(child.Key))
                    {
                        yield return descendant;
                    }

                    foreach (var descendant in DescendantsAndSelf(child.Value))
                    {
                        yield return descendant;
                    }
                }

                break;
            case YamlSequenceNode sequence:
                foreach (var child in sequence.Children)
                {
                    foreach (var descendant in DescendantsAndSelf(child))
                    {
                        yield return descendant;
                    }
                }

                break;
        }
    }
}
