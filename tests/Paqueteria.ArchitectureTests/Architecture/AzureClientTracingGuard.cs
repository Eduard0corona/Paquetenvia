using System.Text.RegularExpressions;

namespace Paqueteria.ArchitectureTests.Architecture;

/// <summary>One C# source file as the guard reads it: its repository path and its code without full-line comments.</summary>
internal sealed record GuardedSource(string Path, string Code);

/// <summary>
/// OBS-003: Azure.Core adds <c>traceparent</c> from <c>Activity.Current</c> in its own pipeline, so every Azure SDK
/// client and credential must be built with options passed through
/// <c>AzureClientTracing.WithoutDistributedTracing()</c>, whose transport removes the trace context headers. The test
/// project does not reference Roslyn, so this guard reads the source one construction at a time:
/// <list type="bullet">
/// <item>a file that uses an Azure SDK namespace (any <c>Azure.*</c> but <c>Azure.Core</c>, or
/// <c>Microsoft.Extensions.Azure</c>) or a guarded extension is in scope and must be reviewed, and no global using
/// may hide such a namespace from that review;</item>
/// <item>in a file in scope, every construction of a client, a credential or a key resolver, explicit
/// (<c>new SecretClient(</c>) or target-typed (<c>SecretClient client = new(</c>), must pass options that go through
/// the wrapper: directly, through a helper whose every return goes through it, or through a variable only ever
/// assigned that way; a target-typed <c>new(</c> whose type the source does not name is refused;</item>
/// <item>anywhere, <c>ProtectKeysWithAzureKeyVault</c>, <c>PersistKeysToAzureBlobStorage</c>, <c>AddAzureClients</c>,
/// <c>AddAzureKeyVault</c> and <c>HttpPipelineBuilder.Build</c>, which build their own clients, must be routed through
/// the wrapper.</item>
/// </list>
/// </summary>
internal static partial class AzureClientTracingGuard
{
    internal const string Wrapper = ".WithoutDistributedTracing()";

    /// <summary>The reviewed key resolver whose Key Vault resolver is built with wrapped options.</summary>
    internal const string ReviewedKeyResolver = "DataProtectionKeyEncryptionKeyResolver";

    private static readonly HashSet<string> NonAzureConstructions = new(StringComparer.Ordinal)
    {
        // System.Net.Http: its handler injects through the process propagator, which injects nothing (OBS-003).
        "HttpClient",
    };

    private static readonly HashSet<string> NotATypeName = new(StringComparer.Ordinal)
    {
        "return", "await", "yield", "throw", "else", "case", "in", "out", "ref", "is", "as", "new", "var", "when",
        "and", "or", "not", "using",
    };

    /// <summary>The paths in scope: they use an Azure SDK namespace or call a guarded extension.</summary>
    internal static IReadOnlyList<string> InScope(IEnumerable<GuardedSource> sources) =>
        sources.Where(IsInScope).Select(source => source.Path).Order(StringComparer.Ordinal).ToArray();

    /// <summary>Every way <paramref name="sources"/> could send an Azure request with trace context.</summary>
    internal static IReadOnlyList<string> Violations(
        IReadOnlyCollection<GuardedSource> sources,
        IReadOnlyCollection<string> reviewed)
    {
        var violations = new List<string>();
        var helpers = WrapperHelpers(sources);
        foreach (var source in sources)
        {
            foreach (Match match in GlobalAzureUsingPattern().Matches(source.Code))
            {
                violations.Add($"{At(source, match.Index)}: '{match.Value.Trim()}' hides Azure SDK use from the review");
            }

            if (IsInScope(source))
            {
                if (!reviewed.Contains(source.Path))
                {
                    violations.Add($"{source.Path}: uses {ScopeReason(source)} and is not a reviewed Azure client file");
                }

                violations.AddRange(ConstructionViolations(source, helpers));
            }

            violations.AddRange(GuardedCallViolations(source, helpers));
        }

        return violations;
    }

    /// <summary>A project file must not import an Azure SDK namespace for every file of the project.</summary>
    internal static IReadOnlyList<string> ProjectViolations(string path, string project) =>
        ProjectAzureUsingPattern().Matches(project)
            .Select(match => $"{path}: '{match.Value}' hides Azure SDK use from the review")
            .ToArray();

    /// <summary>Lines that set an activity's display name, on any receiver.</summary>
    internal static IReadOnlyList<int> DisplayNameAssignments(string code) =>
        DisplayNameAssignmentPattern().Matches(code).Select(match => LineOf(code, match.Index)).ToArray();

    private static bool IsInScope(GuardedSource source) =>
        AzureNamespacePattern().IsMatch(source.Code) || GuardedCallPattern().IsMatch(source.Code);

    private static string ScopeReason(GuardedSource source) =>
        AzureNamespacePattern().Match(source.Code) is { Success: true } usage
            ? usage.Groups["ns"].Value
            : GuardedCallPattern().Match(source.Code).Groups["call"].Value;

    private static IEnumerable<string> ConstructionViolations(GuardedSource source, WrapperHelperIndex helpers)
    {
        var code = source.Code;
        foreach (Match match in ExplicitConstructionPattern().Matches(code))
        {
            var type = SimpleName(match.Groups["type"].Value);
            if (!IsAzureConstruction(type))
            {
                continue;
            }

            var arguments = Enclosed(code, match.Groups["open"].Index);
            if (!IsWrappedRoute(source, StripNestedConstructions(arguments), helpers))
            {
                yield return $"{At(source, match.Index)}: new {type}(...) is not built with options passed through {Wrapper}";
            }
        }

        foreach (Match match in TargetTypedConstructionPattern().Matches(code))
        {
            var prefix = code[Math.Max(0, match.Index - 400)..match.Index];
            if (IsNewConstraint(prefix))
            {
                continue;
            }

            var type = TargetType(code, prefix);
            if (type is null)
            {
                yield return $"{At(source, match.Index)}: a target-typed new(...) in an Azure client file must name its type";
                continue;
            }

            if (!IsAzureConstruction(type))
            {
                continue;
            }

            var arguments = Enclosed(code, match.Index + match.Length - 1);
            if (!IsWrappedRoute(source, StripNestedConstructions(arguments), helpers))
            {
                yield return $"{At(source, match.Index)}: new(...) of {type} is not built with options passed through {Wrapper}";
            }
        }
    }

    private static IEnumerable<string> GuardedCallViolations(GuardedSource source, WrapperHelperIndex helpers)
    {
        var code = source.Code;
        foreach (Match match in GuardedCallPattern().Matches(code))
        {
            var call = WhitespacePattern().Replace(match.Groups["call"].Value, string.Empty);
            var arguments = Enclosed(code, match.Index + match.Length - 1);
            var problem = call switch
            {
                "ProtectKeysWithAzureKeyVault" when !arguments.Contains(ReviewedKeyResolver, StringComparison.Ordinal) =>
                    $"must take the reviewed {ReviewedKeyResolver}; its credential overloads build a KeyResolver without the wrapper",
                "AddAzureClients" when !arguments.Contains(Wrapper, StringComparison.Ordinal) ||
                                       !UseCredentialPattern().IsMatch(arguments) =>
                    $"must apply {Wrapper} to its defaults and use a reviewed credential; otherwise it builds unwrapped clients",
                "PersistKeysToAzureBlobStorage" or "AddAzureKeyVault" or "HttpPipelineBuilder.Build"
                    when !IsWrappedRoute(source, arguments, helpers) =>
                    $"must receive a client or options built through {Wrapper}; its other overloads build their own",
                _ => null,
            };
            if (problem is not null)
            {
                yield return $"{At(source, match.Index)}: {call}(...) {problem}";
            }
        }
    }

    /// <summary>True when the options of a construction or call go through the wrapper.</summary>
    private static bool IsWrappedRoute(GuardedSource source, string arguments, WrapperHelperIndex helpers) =>
        IsDirectRoute(source, arguments, helpers) ||
        IdentifierPattern().Matches(arguments)
            .Select(match => match.Value)
            .Distinct(StringComparer.Ordinal)
            .Any(identifier => IsWrappedVariable(source, identifier, helpers));

    private static bool IsDirectRoute(GuardedSource source, string text, WrapperHelperIndex helpers) =>
        text.Contains(Wrapper, StringComparison.Ordinal) || helpers.IsCalledIn(source, text);

    /// <summary>A variable, field or property that is assigned at least once, and only ever through the wrapper.</summary>
    private static bool IsWrappedVariable(GuardedSource source, string identifier, WrapperHelperIndex helpers)
    {
        var assignments = Regex.Matches(
                source.Code,
                $@"(?<![\w.])(?:this\.)?{Regex.Escape(identifier)}\s*(?:\{{[^{{}}]*\}}\s*)?=(?![=>])",
                RegexOptions.CultureInvariant)
            .Select(match => Statement(source.Code, match.Index + match.Length))
            .ToArray();
        return assignments.Length > 0 &&
               assignments.All(value => IsDirectRoute(source, StripNestedConstructions(value), helpers));
    }

    /// <summary>The type a target-typed <c>new(</c> creates, when the statement names it; otherwise null.</summary>
    private static string? TargetType(string code, string prefix)
    {
        var declared = DeclarationTargetPattern().Match(prefix);
        if (declared.Success && !NotATypeName.Contains(declared.Groups["type"].Value))
        {
            return SimpleName(declared.Groups["type"].Value);
        }

        var property = PropertyTargetPattern().Match(prefix);
        if (property.Success && !NotATypeName.Contains(property.Groups["type"].Value))
        {
            return SimpleName(property.Groups["type"].Value);
        }

        var assigned = AssignmentTargetPattern().Match(prefix);
        if (!assigned.Success)
        {
            return null;
        }

        var name = Regex.Escape(assigned.Groups["name"].Value);
        return Regex.Matches(
                code,
                $@"(?<type>(?:global::)?[A-Za-z_][\w.]*(?:<[^;{{}}()=]*>)?\??(?:\[\])*)\s+{name}\s*(?=[;=,)]|\{{)",
                RegexOptions.CultureInvariant)
            .Select(match => match.Groups["type"].Value)
            .Where(type => !NotATypeName.Contains(type))
            .Select(SimpleName)
            .FirstOrDefault();
    }

    private static bool IsNewConstraint(string prefix)
    {
        var statement = prefix[(prefix.LastIndexOfAny([';', '{', '}']) + 1)..];
        return Regex.IsMatch(statement, @"\bwhere\s+\w+\s*:[^;{}]*[:,]\s*$", RegexOptions.CultureInvariant);
    }

    private static bool IsAzureConstruction(string type) =>
        !NonAzureConstructions.Contains(type) && AzureConstructionTypePattern().IsMatch(type);

    /// <summary><c>System.Collections.Generic.Dictionary&lt;K, V&gt;?</c> is <c>Dictionary</c>.</summary>
    private static string SimpleName(string type)
    {
        var generic = type.IndexOf('<', StringComparison.Ordinal);
        var name = (generic >= 0 ? type[..generic] : type).TrimEnd('?', '[', ']', ' ');
        name = name.Replace("global::", string.Empty, StringComparison.Ordinal);
        return name[(name.LastIndexOf('.') + 1)..].Trim();
    }

    /// <summary>The text between the bracket at <paramref name="open"/> and its match, skipping strings.</summary>
    private static string Enclosed(string code, int open)
    {
        var close = Matching(code, open);
        return close < 0 ? code[(open + 1)..] : code[(open + 1)..close];
    }

    /// <summary>The rest of the statement that starts at <paramref name="start"/>, up to its <c>;</c> at depth 0.</summary>
    private static string Statement(string code, int start)
    {
        var depth = 0;
        for (var index = start; index < code.Length; index++)
        {
            index = SkipLiteral(code, index);
            if (index >= code.Length)
            {
                break;
            }

            switch (code[index])
            {
                case '(' or '[' or '{':
                    depth++;
                    break;
                case ')' or ']' or '}':
                    if (--depth < 0)
                    {
                        return code[start..index];
                    }

                    break;
                case ';' when depth == 0:
                    return code[start..index];
            }
        }

        return code[start..];
    }

    private static int Matching(string code, int open)
    {
        var depth = 0;
        for (var index = open; index < code.Length; index++)
        {
            index = SkipLiteral(code, index);
            if (index >= code.Length)
            {
                break;
            }

            if (code[index] is '(' or '[' or '{')
            {
                depth++;
            }
            else if (code[index] is ')' or ']' or '}' && --depth == 0)
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>
    /// When a string or character literal starts at <paramref name="index"/>, the index of its last character;
    /// otherwise <paramref name="index"/>.
    /// </summary>
    private static int SkipLiteral(string code, int index)
    {
        var start = index;
        while (start < code.Length && code[start] is '$' or '@')
        {
            start++;
        }

        if (start >= code.Length || code[start] is not ('"' or '\''))
        {
            return index;
        }

        var quote = code[start];
        if (quote == '"' && string.CompareOrdinal(code, start, "\"\"\"", 0, 3) == 0)
        {
            var end = code.IndexOf("\"\"\"", start + 3, StringComparison.Ordinal);
            return end < 0 ? code.Length : end + 2;
        }

        var verbatim = code.AsSpan(index, start - index).Contains('@');
        for (var position = start + 1; position < code.Length; position++)
        {
            if (!verbatim && code[position] == '\\')
            {
                position++;
            }
            else if (code[position] == quote)
            {
                if (verbatim && position + 1 < code.Length && code[position + 1] == quote)
                {
                    position++;
                    continue;
                }

                return position;
            }
        }

        return code.Length;
    }

    /// <summary>The text without its nested object creations, so a route inside one never counts for another.</summary>
    private static string StripNestedConstructions(string text)
    {
        var builder = new System.Text.StringBuilder(text.Length);
        var index = 0;
        foreach (Match match in AnyConstructionPattern().Matches(text))
        {
            if (match.Index < index)
            {
                continue;
            }

            builder.Append(text, index, match.Index - index);
            var open = match.Index + match.Length - 1;
            var close = Matching(text, open);
            index = close < 0 ? text.Length : close + 1;
            if (index < text.Length && close >= 0 && text[open] == '(')
            {
                // An initializer after the argument list belongs to the same creation.
                var next = index;
                while (next < text.Length && char.IsWhiteSpace(text[next]))
                {
                    next++;
                }

                if (next < text.Length && text[next] == '{')
                {
                    var end = Matching(text, next);
                    index = end < 0 ? text.Length : end + 1;
                }
            }
        }

        builder.Append(text, Math.Min(index, text.Length), text.Length - Math.Min(index, text.Length));
        return builder.ToString();
    }

    private static string At(GuardedSource source, int index) => $"{source.Path}:{LineOf(source.Code, index)}";

    private static int LineOf(string code, int index) => code.AsSpan(0, index).Count('\n') + 1;

    /// <summary>The methods, by declaring type, whose every return goes through the wrapper.</summary>
    private static WrapperHelperIndex WrapperHelpers(IEnumerable<GuardedSource> sources)
    {
        var wrapping = new List<(string Path, string? Type, string Method)>();
        var other = new List<(string Path, string? Type, string Method)>();
        foreach (var source in sources.Where(source => source.Code.Contains(Wrapper, StringComparison.Ordinal)))
        {
            foreach (Match declaration in MethodDeclarationPattern().Matches(source.Code))
            {
                var name = declaration.Groups["name"].Value;
                if (NotATypeName.Contains(name) || ControlKeywordPattern().IsMatch(name))
                {
                    continue;
                }

                var body = declaration.Groups["arrow"].Success
                    ? "return " + Statement(source.Code, declaration.Index + declaration.Length) + ";"
                    : Enclosed(source.Code, declaration.Index + declaration.Length - 1);
                var returns = ReturnPattern().Matches(body).Select(match => match.Groups["value"].Value.Trim()).ToArray();
                var type = DeclaringType(source.Code, declaration.Index);
                var target = returns.Length > 0 &&
                             returns.All(value => value.EndsWith(Wrapper, StringComparison.Ordinal))
                    ? wrapping
                    : other;
                target.Add((source.Path, type, name));
            }
        }

        return new WrapperHelperIndex(
            wrapping.Where(helper => !other.Any(item => item.Path == helper.Path && item.Method == helper.Method)).ToArray());
    }

    private static string? DeclaringType(string code, int index)
    {
        var types = TypeDeclarationPattern().Matches(code[..index]);
        return types.Count == 0 ? null : types[^1].Groups["name"].Value;
    }

    private sealed class WrapperHelperIndex(IReadOnlyList<(string Path, string? Type, string Method)> helpers)
    {
        /// <summary>
        /// An unqualified call of a helper declared in the same file, or a call qualified with the helper's type.
        /// </summary>
        internal bool IsCalledIn(GuardedSource source, string text) =>
            helpers.Any(helper =>
                (helper.Path == source.Path &&
                 Regex.IsMatch(text, $@"(?<![\w.]){Regex.Escape(helper.Method)}\s*(?:<[^<>()]*>)?\s*\(", RegexOptions.CultureInvariant)) ||
                (helper.Type is not null &&
                 Regex.IsMatch(
                     text,
                     $@"(?<![\w.]){Regex.Escape(helper.Type)}\s*\.\s*{Regex.Escape(helper.Method)}\s*(?:<[^<>()]*>)?\s*\(",
                     RegexOptions.CultureInvariant)));
    }

    [GeneratedRegex(@"(?<![\w.])(?:global::)?(?<ns>Azure\.(?!Core\b)[A-Z]\w*|Microsoft\.Extensions\.Azure)\b")]
    private static partial Regex AzureNamespacePattern();

    [GeneratedRegex(
        @"\bglobal\s+using\s+(?:static\s+)?(?:\w+\s*=\s*)?(?:global::)?(?:Azure\.(?!Core\b)[A-Z]|Microsoft\.Extensions\.Azure\b)[^;]*;")]
    private static partial Regex GlobalAzureUsingPattern();

    [GeneratedRegex(@"<Using\b[^>]*\bInclude\s*=\s*""(?:global::)?(?:Azure\.(?!Core\b)[A-Z]|Microsoft\.Extensions\.Azure\b)[^""]*""[^>]*>")]
    private static partial Regex ProjectAzureUsingPattern();

    [GeneratedRegex(
        @"(?:\b(?<call>ProtectKeysWithAzureKeyVault|PersistKeysToAzureBlobStorage|AddAzureClients|AddAzureKeyVault)|(?<call>HttpPipelineBuilder\s*\.\s*Build))\s*(?:<[^<>()]*>)?\s*\(")]
    private static partial Regex GuardedCallPattern();

    [GeneratedRegex(@"\bUseCredential\s*\(")]
    private static partial Regex UseCredentialPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();

    [GeneratedRegex(
        @"\bnew\s+(?<type>(?:global::)?(?:[A-Za-z_]\w*\s*\.\s*)*[A-Za-z_]\w*)\s*(?:<[^<>(){};]*(?:<[^<>(){};]*>[^<>(){};]*)*>)?\s*(?<open>[({])")]
    private static partial Regex ExplicitConstructionPattern();

    [GeneratedRegex(@"(?<![\w.@])new\s*\(")]
    private static partial Regex TargetTypedConstructionPattern();

    [GeneratedRegex(
        @"(?<![\w.@])new(?:\s+(?:global::)?(?:[A-Za-z_]\w*\s*\.\s*)*[A-Za-z_]\w*\s*(?:<[^<>(){};]*(?:<[^<>(){};]*>[^<>(){};]*)*>)?)?\s*(?:\[\s*\])?\s*[({]")]
    private static partial Regex AnyConstructionPattern();

    [GeneratedRegex(@"^\w*(?:Client|Credential)$|^KeyResolver$")]
    private static partial Regex AzureConstructionTypePattern();

    [GeneratedRegex(@"(?<type>(?:global::)?[A-Za-z_][\w.]*(?:<[^;{}()=]*>)?\??(?:\[\])*)\s+@?[A-Za-z_]\w*\s*=\s*$")]
    private static partial Regex DeclarationTargetPattern();

    [GeneratedRegex(@"(?<type>(?:global::)?[A-Za-z_][\w.]*(?:<[^;{}()=]*>)?\??(?:\[\])*)\s+[A-Za-z_]\w*\s*\{[^{}]*\}\s*=\s*$")]
    private static partial Regex PropertyTargetPattern();

    [GeneratedRegex(@"(?:^|[;{}(,])\s*(?:this\s*\.\s*)?(?<name>[A-Za-z_]\w*)\s*=\s*$")]
    private static partial Regex AssignmentTargetPattern();

    [GeneratedRegex(@"(?<![\w.])_?[A-Za-z]\w*\b(?!\s*[.(<])")]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex(
        @"(?<name>[A-Za-z_]\w*)\s*(?:<[^<>(){};=]*>)?\s*\((?:[^()]|\((?:[^()]|\([^()]*\))*\))*\)\s*(?:where\s+[^{;=]+?)?\s*(?:(?<arrow>=>)|\{)")]
    private static partial Regex MethodDeclarationPattern();

    [GeneratedRegex("^(?:if|for|foreach|while|switch|catch|using|lock|fixed|return|nameof|typeof|sizeof|default|checked|unchecked|when)$")]
    private static partial Regex ControlKeywordPattern();

    [GeneratedRegex(@"\breturn\s+(?<value>[^;]+);")]
    private static partial Regex ReturnPattern();

    [GeneratedRegex(@"\b(?:class|record|struct|interface)\s+(?<name>[A-Za-z_]\w*)")]
    private static partial Regex TypeDeclarationPattern();

    [GeneratedRegex(@"\.\s*DisplayName\s*(?:\?\?|\+)?=(?!=)")]
    private static partial Regex DisplayNameAssignmentPattern();
}
