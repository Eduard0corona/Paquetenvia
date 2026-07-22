using System.Security.Cryptography;
using System.Text.Json;
using Paqueteria.ContractTests.Support;
using YamlDotNet.RepresentationModel;

namespace Paqueteria.ContractTests.Normative;

public sealed class NormativeBaselineTests
{
    [Fact]
    public void Every_normative_yaml_document_parses()
    {
        var files = Directory.GetFiles(RepositoryPaths.Normative(), "*.yaml", SearchOption.AllDirectories);
        Assert.Equal(10, files.Length);
        foreach (var file in files)
        {
            using var reader = File.OpenText(file);
            var stream = new YamlStream();
            stream.Load(reader);
            Assert.Single(stream.Documents);
            Assert.NotNull(stream.Documents[0].RootNode);
        }
    }

    [Fact]
    public void Manifest_declares_and_hashes_exactly_73_canonical_files()
    {
        var normativeRoot = RepositoryPaths.Normative();
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(normativeRoot, "MANIFEST.json")));
        var root = document.RootElement;
        Assert.Equal(73, root.GetProperty("file_count").GetInt32());
        Assert.Equal("c7681336856421487b208ea220d05017c4b8f820f1a34e1e7e838d5da09b7b96", root.GetProperty("canonical_sql_sha256").GetString());

        var declaredPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in root.GetProperty("files").EnumerateArray())
        {
            var relativePath = entry.GetProperty("path").GetString()!;
            Assert.True(declaredPaths.Add(relativePath), $"Duplicate manifest entry: {relativePath}");
            var path = Path.Combine(normativeRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), $"Missing manifest file: {relativePath}");
            Assert.Equal(entry.GetProperty("bytes").GetInt64(), new FileInfo(path).Length);
            using var stream = File.OpenRead(path);
            var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            Assert.Equal(entry.GetProperty("sha256").GetString(), hash);
        }

        Assert.Equal(73, declaredPaths.Count);
        var actualIdentityFiles = Directory.GetFiles(normativeRoot, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(normativeRoot, path).Replace(Path.DirectorySeparatorChar, '/'))
            .Where(path => path is not "MANIFEST.json")
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(declaredPaths.Order(StringComparer.Ordinal), actualIdentityFiles.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Backlog_has_unique_ids_existing_dependencies_and_an_acyclic_graph()
    {
        var root = YamlNodes.LoadMapping(RepositoryPaths.Normative("specs", "AI-08_BACKLOG.yaml"));
        var items = root.Sequence("items").Children.Cast<YamlMappingNode>().ToArray();
        Assert.Equal(56, items.Length);
        var byId = items.ToDictionary(item => item.Scalar("id"), StringComparer.Ordinal);
        Assert.Equal(items.Length, byId.Count);
        Assert.Contains("ARC-002", byId.Keys);

        var dependencies = byId.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Sequence("depends_on").Children.Cast<YamlScalarNode>().Select(node => node.Value!).ToArray(),
            StringComparer.Ordinal);
        Assert.All(dependencies.Values.SelectMany(value => value), dependency => Assert.Contains(dependency, byId.Keys));

        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in byId.Keys)
        {
            Visit(id, dependencies, visiting, visited);
        }
    }

    [Fact]
    public void Internal_and_public_status_vocabularies_are_consistent_across_yaml_openapi_and_signalr()
    {
        var product = YamlNodes.LoadMapping(RepositoryPaths.Normative("specs", "AI-02_PRODUCT_CONTRACT.yaml"));
        var domain = YamlNodes.LoadMapping(RepositoryPaths.Normative("specs", "AI-04_DOMAIN_MODEL.yaml"));
        var openApi = YamlNodes.LoadMapping(RepositoryPaths.Normative("contracts", "AI-05_OPENAPI.yaml"));
        var signalR = YamlNodes.LoadMapping(RepositoryPaths.Normative("contracts", "AI-12_SIGNALR_CONTRACT.yaml"));

        var productStatuses = Scalars(product.Sequence("authoritative_statuses"));
        var domainMapping = domain.Mapping("public_tracking_contract").Mapping("internal_to_public");
        var domainStatuses = domainMapping.Children.Keys.Cast<YamlScalarNode>().Select(node => node.Value!).ToArray();
        var schemas = openApi.Mapping("components").Mapping("schemas");
        var openApiStatuses = Scalars(schemas.Mapping("OrderStatus").Sequence("enum"));
        Assert.Equal(17, productStatuses.Length);
        Assert.Equal(productStatuses, domainStatuses);
        Assert.Equal(productStatuses, openApiStatuses);

        var domainPublic = Scalars(domain.Mapping("public_tracking_contract").Sequence("public_statuses"));
        var openApiPublic = Scalars(schemas.Mapping("PublicOrderStatus").Sequence("enum"));
        var signalRPublic = Scalars(signalR.Mapping("events").Mapping("PublicOrderStatusChanged").Sequence("public_status_enum"));
        Assert.Equal(9, domainPublic.Length);
        Assert.Equal(domainPublic, openApiPublic);
        Assert.Equal(domainPublic, signalRPublic);
        Assert.All(domainMapping.Children.Values.Cast<YamlScalarNode>(), value => Assert.Contains(value.Value!, domainPublic));
    }

    private static string[] Scalars(YamlSequenceNode sequence) =>
        sequence.Children.Cast<YamlScalarNode>().Select(node => node.Value!).ToArray();

    private static void Visit(
        string id,
        IReadOnlyDictionary<string, string[]> dependencies,
        ISet<string> visiting,
        ISet<string> visited)
    {
        if (visited.Contains(id))
        {
            return;
        }

        Assert.True(visiting.Add(id), $"Backlog cycle detected at {id}.");
        foreach (var dependency in dependencies[id])
        {
            Visit(dependency, dependencies, visiting, visited);
        }

        visiting.Remove(id);
        visited.Add(id);
    }
}
