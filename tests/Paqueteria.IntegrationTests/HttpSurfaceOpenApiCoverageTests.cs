using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Paqueteria.IntegrationTests;

/// <summary>
/// Every versioned HTTP operation the assembled application actually routes must be declared in
/// AI-05.
/// </summary>
/// <remarks>
/// The per-module contract tests read one endpoint source file each, so a module that adds a route
/// in a new file — which is exactly how CSV-001 arrived — is invisible to all of them. This test
/// reads the composed <see cref="EndpointDataSource"/> instead, so the surface it checks is the one
/// the host serves, whichever file declared it. It is deliberately one-directional: AI-05 may
/// contract operations that are not implemented yet, but nothing may be served that AI-05 does not
/// contract.
/// </remarks>
public sealed class HttpSurfaceOpenApiCoverageTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private const string VersionedPrefix = "/api/v1";

    private static readonly HashSet<string> HttpMethodKeys = new(StringComparer.Ordinal)
    {
        "get", "put", "post", "delete", "options", "head", "patch", "trace",
    };

    /// <summary>
    /// Operations served today that AI-05 does not contract. The reporting dashboard predates this
    /// test and is declared additive by OPS-002; closing that gap is a normative change of its own,
    /// so it is named here rather than waived silently. Nothing may be added to this list without
    /// the same kind of decision.
    /// </summary>
    private static readonly string[] KnownUndeclaredOperations =
    [
        "GET /operations/dashboard",
    ];

    [Fact]
    public void Every_routed_v1_operation_is_declared_in_AI05()
    {
        var contracted = ReadContractedOperations();
        var served = ReadServedOperations();

        // Guards against a vacuous pass if either side stops being read at all.
        Assert.Equal(31, contracted.Paths.Count);
        Assert.True(served.Count >= 20, $"Only {served.Count} versioned operations were routed.");
        Assert.Contains("POST /orders/csv/preview", served);
        Assert.Contains("POST /orders/csv/commit", served);

        var undeclared = served.Except(contracted.Operations, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(KnownUndeclaredOperations, undeclared);

        // The waivers are waivers, not a way to drop an operation off the served surface entirely.
        Assert.All(KnownUndeclaredOperations, operation => Assert.Contains(operation, served));
    }

    /// <summary>
    /// The operations the host routes, as "METHOD /path" with the version prefix removed and every
    /// route parameter reduced to <c>{}</c>, so ASP.NET route constraints and AI-05 parameter names
    /// compare equal. Endpoints excluded from the API description are the test-only ones.
    /// </summary>
    private HashSet<string> ReadServedOperations()
    {
        var operations = new HashSet<string>(StringComparer.Ordinal);
        foreach (var endpoint in factory.Services.GetRequiredService<EndpointDataSource>().Endpoints)
        {
            if (endpoint is not RouteEndpoint route ||
                endpoint.Metadata.GetMetadata<IExcludeFromDescriptionMetadata>()?.ExcludeFromDescription == true)
            {
                continue;
            }

            var template = "/" + route.RoutePattern.RawText?.TrimStart('/');
            if (!template.StartsWith(VersionedPrefix + "/", StringComparison.Ordinal))
            {
                continue;
            }

            var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [];
            foreach (var method in methods)
            {
                operations.Add($"{method.ToUpperInvariant()} {Normalize(template[VersionedPrefix.Length..])}");
            }
        }

        return operations;
    }

    /// <summary>
    /// The operations AI-05 declares. The document is read line by line rather than through a YAML
    /// object model because this project carries no YAML reader, and the shape being read — the
    /// path keys of <c>paths:</c> and the method keys under them — is fixed by the contract tests
    /// that also assert the document parses and how many paths it has.
    /// </summary>
    private static (HashSet<string> Paths, HashSet<string> Operations) ReadContractedOperations()
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var operations = new HashSet<string>(StringComparer.Ordinal);
        var inPaths = false;
        string? currentPath = null;

        foreach (var raw in File.ReadLines(ContractPath()))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            if (line[0] != ' ')
            {
                inPaths = line.StartsWith("paths:", StringComparison.Ordinal);
                currentPath = null;
                continue;
            }

            if (!inPaths)
            {
                continue;
            }

            if (line.StartsWith("  /", StringComparison.Ordinal) && line.EndsWith(':'))
            {
                currentPath = line[2..^1];
                paths.Add(currentPath);
                continue;
            }

            if (currentPath is null ||
                line.Length < 6 ||
                !line.StartsWith("    ", StringComparison.Ordinal) ||
                line[4] == ' ' ||
                !line.EndsWith(':'))
            {
                continue;
            }

            var key = line[4..^1];
            if (HttpMethodKeys.Contains(key))
            {
                operations.Add($"{key.ToUpperInvariant()} {Normalize(currentPath)}");
            }
        }

        return (paths, operations);
    }

    private static string Normalize(string template)
    {
        var normalized = new System.Text.StringBuilder(template.Length);
        var depth = 0;
        foreach (var character in template)
        {
            if (character == '{')
            {
                if (depth++ == 0)
                {
                    normalized.Append("{}");
                }
            }
            else if (character == '}')
            {
                depth--;
            }
            else if (depth == 0)
            {
                normalized.Append(character);
            }
        }

        return normalized.ToString();
    }

    private static string ContractPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName, "docs", "normative", "v0.6", "contracts", "AI-05_OPENAPI.yaml");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("docs/normative/v0.6/contracts/AI-05_OPENAPI.yaml");
    }
}
