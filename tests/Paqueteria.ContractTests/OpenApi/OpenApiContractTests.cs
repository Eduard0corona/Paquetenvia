using Microsoft.OpenApi;
using Microsoft.OpenApi.Reader;
using Microsoft.OpenApi.YamlReader;
using Paqueteria.ContractTests.Support;
using YamlDotNet.RepresentationModel;

namespace Paqueteria.ContractTests.OpenApi;

public sealed class OpenApiContractTests
{
    [Fact]
    public async Task OpenApi_31_document_parses_without_errors_and_has_the_expected_surface()
    {
        var path = RepositoryPaths.Normative("contracts", "AI-05_OPENAPI.yaml");
        var settings = new OpenApiReaderSettings();
        settings.AddYamlReader();
        var result = await OpenApiDocument.LoadAsync(path, settings, CancellationToken.None);
        _ = Assert.IsType<OpenApiDocument>(result.Document);
        var diagnostic = Assert.IsType<OpenApiDiagnostic>(result.Diagnostic);
        Assert.Empty(diagnostic.Errors);

        var root = YamlNodes.LoadMapping(path);
        Assert.Equal("3.1.0", root.Scalar("openapi"));
        Assert.Equal("0.6.0", root.Mapping("info").Scalar("version"));
        Assert.Equal(23, root.Mapping("paths").Children.Count);
        Assert.Equal(48, root.Mapping("components").Mapping("schemas").Children.Count);
    }

    [Fact]
    public void Operation_ids_are_unique_and_all_183_internal_references_resolve()
    {
        var root = YamlNodes.LoadMapping(RepositoryPaths.Normative("contracts", "AI-05_OPENAPI.yaml"));
        var operationIds = new List<string>();
        foreach (var path in root.Mapping("paths").Children.Values.Cast<YamlMappingNode>())
        {
            foreach (var operation in path.Children)
            {
                if (operation.Key is YamlScalarNode { Value: "parameters" })
                {
                    continue;
                }

                if (operation.Value is YamlMappingNode operationMapping &&
                    operationMapping.Children.TryGetValue(new YamlScalarNode("operationId"), out var operationId))
                {
                    operationIds.Add(((YamlScalarNode)operationId).Value!);
                }
            }
        }

        Assert.Equal(operationIds.Count, operationIds.Distinct(StringComparer.Ordinal).Count());
        var references = YamlNodes.DescendantsAndSelf(root)
            .OfType<YamlMappingNode>()
            .Where(mapping => mapping.Children.TryGetValue(new YamlScalarNode("$ref"), out _))
            .Select(mapping => ((YamlScalarNode)mapping.Required("$ref")).Value!)
            .ToArray();
        Assert.Equal(183, references.Length);
        Assert.All(references, reference => Assert.NotNull(ResolveReference(root, reference)));
    }

    [Fact]
    public void Every_openapi_cents_field_is_integer_int64_and_uniform_not_found_is_non_enumerating()
    {
        var root = YamlNodes.LoadMapping(RepositoryPaths.Normative("contracts", "AI-05_OPENAPI.yaml"));
        var centsFields = YamlNodes.DescendantsAndSelf(root.Mapping("components").Mapping("schemas"))
            .OfType<YamlMappingNode>()
            .SelectMany(mapping => mapping.Children)
            .Where(child => child.Key is YamlScalarNode scalar && scalar.Value!.EndsWith("_cents", StringComparison.Ordinal))
            .Select(child => Assert.IsType<YamlMappingNode>(child.Value))
            .ToArray();
        Assert.NotEmpty(centsFields);
        Assert.All(centsFields, field =>
        {
            Assert.Equal("integer", field.Scalar("type"));
            Assert.Equal("int64", field.Scalar("format"));
        });

        var uniform = root.Mapping("components").Mapping("responses").Mapping("UniformNotFound");
        Assert.Contains("cross-tenant", uniform.Scalar("description"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("expired", uniform.Scalar("description"), StringComparison.OrdinalIgnoreCase);
        var tracking404 = root.Mapping("paths").Mapping("/tracking/{token}").Mapping("get").Mapping("responses").Mapping("404");
        Assert.Equal("#/components/responses/UniformNotFound", tracking404.Scalar("$ref"));
    }

    private static YamlNode ResolveReference(YamlNode root, string reference)
    {
        Assert.StartsWith("#/", reference, StringComparison.Ordinal);
        var current = root;
        foreach (var encodedSegment in reference[2..].Split('/'))
        {
            var segment = encodedSegment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            current = Assert.IsType<YamlMappingNode>(current).Required(segment);
        }

        return current;
    }
}
