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
    /// Operations served today that AI-05 does not contract, each with the ADR that records it. The
    /// reporting dashboard predates this test and is declared additive by OPS-002 and ADR-OBS-001;
    /// closing that gap is a normative change of its own, so it is named here rather than waived
    /// silently. Its error statuses are declared in its ADR instead (owner answer of 2026-10-12,
    /// "Enmendar ADR-OBS-001 (Recommended)", OBS-001-DASHBOARD-429-2026-10-12), and the error test
    /// checks them there. Nothing may be added to this list without the same kind of decision.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> KnownUndeclaredOperations =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GET /operations/dashboard"] = "docs/adr/ADR-OBS-001-OPERATIONS-READ-MODEL.md",
        };

    [Fact]
    public void Every_routed_v1_operation_is_declared_in_AI05()
    {
        var contracted = ReadContractedOperations();
        var served = ReadServedOperations();

        // Guards against a vacuous pass if either side stops being read at all.
        Assert.Equal(72, contracted.Paths.Count);
        Assert.True(served.Count >= 20, $"Only {served.Count} versioned operations were routed.");
        Assert.Contains("POST /orders/csv/preview", served);
        Assert.Contains("POST /orders/csv/commit", served);

        var undeclared = served.Except(contracted.Operations, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(KnownUndeclaredOperations.Keys.Order(StringComparer.Ordinal), undeclared);

        // The waivers are waivers, not a way to drop an operation off the served surface entirely.
        Assert.All(KnownUndeclaredOperations.Keys, operation => Assert.Contains(operation, served));
    }

    /// <summary>
    /// AI05-DECLARE-EMITTED-ERRORS and AI05-DECLARE-400-ERRORS: every 400, 409, 429 and 503 an operation
    /// declares in its endpoint metadata is declared in AI-05, and so are the ones the pipeline emits on its
    /// behalf — 503 wherever the identity context is resolved (every authorized operation) or the
    /// PLATFORM_ADMIN tenant-activation audit is written (every tenant operation), and the tenant
    /// middleware's status for a missing or malformed X-Organization-Id header, and 429 wherever a rate-limit policy
    /// applies (SEC-004).
    /// </summary>
    [Fact]
    public void Every_error_status_the_host_emits_is_declared_in_AI05()
    {
        var declared = ReadDeclaredResponses();
        var missing = new List<string>();
        var checkedOperations = 0;
        var checkedThroughAdr = 0;
        var rateLimited = 0;
        foreach (var (operation, endpoint) in ReadServedEndpoints())
        {
            HashSet<string>? statuses;
            if (KnownUndeclaredOperations.TryGetValue(operation, out var adr))
            {
                // Not in AI-05: its error statuses are the ones its ADR declares (OBS-001-DASHBOARD-429-2026-10-12).
                statuses = ReadAdrDeclaredResponses(adr, operation);
                checkedThroughAdr++;
            }
            else
            {
                Assert.True(declared.TryGetValue(operation, out statuses), $"{operation} is not declared in AI-05.");
            }

            checkedOperations++;
            var emitted = new SortedSet<int>(endpoint.Metadata
                .GetOrderedMetadata<IProducesResponseTypeMetadata>()
                .Select(response => response.StatusCode)
                .Where(status => status is 400 or 409 or 429 or 503));
            if (endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Authorization.IAuthorizeData>() is not null)
            {
                emitted.Add(StatusCodes.Status503ServiceUnavailable);
            }

            // SEC-004: every operation under a rate-limit policy can answer 429 (the limiter writes it, not the
            // endpoint), whatever its own metadata declares.
            if (endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute>() is not null)
            {
                emitted.Add(StatusCodes.Status429TooManyRequests);
                rateLimited++;
            }

            if (endpoint.Metadata.GetMetadata<Organizations.Endpoints.Tenancy.RequiresTenantContextMetadata>() is { } tenant)
            {
                emitted.Add(StatusCodes.Status503ServiceUnavailable);
                emitted.Add(tenant.InvalidContextStatusCode);
            }

            missing.AddRange(emitted
                .Where(status => !statuses!.Contains(status.ToString(System.Globalization.CultureInfo.InvariantCulture)))
                .Select(status => $"{operation} emits {status}"));
        }

        Assert.True(checkedOperations >= 40, $"Only {checkedOperations} operations were checked.");
        // PR 2: publicTracking and publishDriverLocation; the hubs are not versioned operations (AI-12, not AI-05).
        // PR 3: the eleven versioned operations of the decided list (sign-in is outside /api/v1).
        Assert.True(rateLimited >= 13, $"Only {rateLimited} rate-limited operations were seen.");
        Assert.Equal(KnownUndeclaredOperations.Count, checkedThroughAdr);
        Assert.Empty(missing);

        // The two anonymous or specially limited cases are pinned by name.
        Assert.Superset(new HashSet<string>(["404", "429", "503"]), new HashSet<string>(declared["GET /tracking/{}"]));
        Assert.Contains("400", declared["GET /locations"]);
        Assert.Contains("400", declared["POST /locations"]);
        // The dashboard's 429 is declared through ADR-OBS-001, not waived.
        Assert.Contains(
            "429",
            ReadAdrDeclaredResponses(KnownUndeclaredOperations["GET /operations/dashboard"], "GET /operations/dashboard"));
    }

    /// <summary>
    /// The error statuses an ADR declares for an operation AI-05 does not contract: the codes on its line
    /// "Respuestas de error de `METHOD /api/v1/path`", which must appear exactly once.
    /// </summary>
    private static HashSet<string> ReadAdrDeclaredResponses(string adr, string operation)
    {
        var separator = operation.IndexOf(' ', StringComparison.Ordinal);
        var marker = $"Respuestas de error de `{operation[..separator]} {VersionedPrefix}{operation[(separator + 1)..]}`";
        var text = File.ReadAllText(Path.Combine(RepositoryRoot(), adr));
        var start = text.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{adr} declares no error responses for {operation}.");
        Assert.Equal(start, text.LastIndexOf(marker, StringComparison.Ordinal));
        var end = text.IndexOf("\n\n", start, StringComparison.Ordinal);
        var line = text[start..(end < 0 ? text.Length : end)];
        var statuses = System.Text.RegularExpressions.Regex.Matches(line, "`([1-5][0-9]{2})`")
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(statuses);
        return statuses;
    }

    /// <summary>"METHOD /path" to the status keys of its AI-05 responses mapping.</summary>
    private static Dictionary<string, HashSet<string>> ReadDeclaredResponses()
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var inPaths = false;
        string? currentPath = null;
        HashSet<string>? current = null;
        var inResponses = false;
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
                current = null;
                continue;
            }

            if (!inPaths)
            {
                continue;
            }

            if (line.StartsWith("  /", StringComparison.Ordinal) && line.EndsWith(':'))
            {
                currentPath = line[2..^1];
                current = null;
                continue;
            }

            if (currentPath is not null && line.StartsWith("    ", StringComparison.Ordinal) && line[4] != ' ' &&
                line.EndsWith(':') && HttpMethodKeys.Contains(line[4..^1]))
            {
                current = [];
                result[$"{line[4..^1].ToUpperInvariant()} {Normalize(currentPath)}"] = current;
                inResponses = false;
                continue;
            }

            if (current is null)
            {
                continue;
            }

            if (line.StartsWith("      ", StringComparison.Ordinal) && line[6] != ' ')
            {
                inResponses = line == "      responses:";
                continue;
            }

            if (inResponses && line.Length == 14 && line.StartsWith("        '", StringComparison.Ordinal) &&
                line.EndsWith("':", StringComparison.Ordinal))
            {
                current.Add(line[9..12]);
            }
        }

        return result;
    }

    private IEnumerable<(string Operation, RouteEndpoint Endpoint)> ReadServedEndpoints()
    {
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

            foreach (var method in endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [])
            {
                yield return ($"{method.ToUpperInvariant()} {Normalize(template[VersionedPrefix.Length..])}", route);
            }
        }
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

    private static string ContractPath() =>
        Path.Combine(RepositoryRoot(), "docs", "normative", "v0.6", "contracts", "AI-05_OPENAPI.yaml");

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName, "docs", "normative", "v0.6", "contracts", "AI-05_OPENAPI.yaml");
            if (File.Exists(candidate))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("docs/normative/v0.6/contracts/AI-05_OPENAPI.yaml");
    }
}
