using System.Text.RegularExpressions;

namespace Paqueteria.ArchitectureTests.Architecture;

/// <summary>
/// SEC-004: the rate-limiter API that only the shared registry
/// (<c>src/BuildingBlocks/Paqueteria.Infrastructure/Security/RateLimiting/</c>) may use. The project does not reference
/// Roslyn, so the guard reads the source with regular expressions over code without comments. It refuses the namespaces
/// themselves (any using, alias using, global using or fully qualified name), so a module cannot reach the limiter
/// types without naming one of them, and it refuses project-wide <c>&lt;Using&gt;</c> items of those namespaces.
/// <c>RateLimitingGuardTests</c> shows each known bypass is refused. The integration tests also check the composed
/// <c>RateLimiterOptions</c> at run time (writer, policies, no global limiter).
/// </summary>
internal static partial class RateLimitingGuard
{
    internal const string RegistryFolder = "src/BuildingBlocks/Paqueteria.Infrastructure/Security/RateLimiting/";
    internal const string ApiProgram = "src/Paqueteria.Api/Program.cs";

    /// <summary>Patterns refused outside <see cref="RegistryFolder"/>, with what each catches.</summary>
    private static readonly (Regex Pattern, string Reason)[] RegistryOnly =
    [
        (new(@"\bMicrosoft\s*\.\s*AspNetCore\s*\.\s*RateLimiting\b"), "the Microsoft.AspNetCore.RateLimiting namespace"),
        (new(@"\bSystem\s*\.\s*Threading\s*\.\s*RateLimiting\b"), "the System.Threading.RateLimiting namespace"),
        (new(@"\bAddRateLimiter\s*\("), "AddRateLimiter"),
        (new(@"\bOnRejected\s*\+?="), "an OnRejected assignment"),
        (new(@"RateLimiterOptions\b"), "RateLimiterOptions or a limiter's options"),
        (new(@"\bRateLimitPartition\b"), "a rate-limit partition"),
        (new(@"\bRejectionStatusCode\b"), "the rejection status code"),
        (new(@"\bGlobalLimiter\b"), "a global limiter"),
        (new(@"\bPartitionedRateLimiter\b"), "a partitioned limiter"),
        (new(@"\bIRateLimiterPolicy\b"), "a limiter policy type"),
        (new(@":\s*(?:global::)?(?:\w+\s*\.\s*)*\w*(?:RateLimiter|ConcurrencyLimiter)\b"), "a RateLimiter subclass"),
        (new(@"\bnew\s+(?:global::)?(?:\w+\s*\.\s*)*\w*(?:RateLimiter|ConcurrencyLimiter)\s*[(<]"), "a limiter instance"),
        (new(@"\bAdd(?:FixedWindow|SlidingWindow|TokenBucket|Concurrency)Limiter\s*[(<]"), "a limiter extension"),
        (new(@"\b(?:Enable|Disable)RateLimiting"), "a limiter attribute or extension"),
    ];

    private static readonly Regex UseRateLimiter = new(@"\bUseRateLimiter\s*\(");

    private static readonly Regex ProjectUsing = new(
        @"<\s*Using\b[^>]*\b(?:Include|Update)\s*=\s*""[^""]*RateLimiting[^""]*""",
        RegexOptions.IgnoreCase);

    /// <summary>The refused uses in one C# source file (its repository-relative path and its text).</summary>
    internal static IReadOnlyList<string> CodeViolations(string path, string source)
    {
        var code = WithoutComments(source);
        var violations = new List<string>();
        if (!path.StartsWith(RegistryFolder, StringComparison.Ordinal))
        {
            violations.AddRange(RegistryOnly
                .Where(rule => rule.Pattern.IsMatch(code))
                .Select(rule => $"{path}: {rule.Reason}"));
        }

        if (path != ApiProgram && UseRateLimiter.IsMatch(code))
        {
            violations.Add($"{path}: UseRateLimiter outside the API host");
        }

        return violations;
    }

    /// <summary>The refused project-wide usings in one project or props file.</summary>
    internal static IReadOnlyList<string> ProjectViolations(string path, string text) =>
        ProjectUsing.IsMatch(WithoutXmlComments(text))
            ? [$"{path}: a project-wide <Using> of a rate limiting namespace"]
            : [];

    internal static string WithoutComments(string source) =>
        LineCommentPattern().Replace(BlockCommentPattern().Replace(source, string.Empty), string.Empty);

    private static string WithoutXmlComments(string text) => XmlCommentPattern().Replace(text, string.Empty);

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex BlockCommentPattern();

    [GeneratedRegex(@"(?m)^\s*//.*$")]
    private static partial Regex LineCommentPattern();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex XmlCommentPattern();
}
