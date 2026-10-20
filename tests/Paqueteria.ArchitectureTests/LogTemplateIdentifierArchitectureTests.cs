using System.Text.RegularExpressions;
using Paqueteria.ArchitectureTests.Architecture;

namespace Paqueteria.ArchitectureTests;

/// <summary>
/// OBS-003-TENANT-PSEUDONYM-2026-10-12 (AI-03 §18, AI-13 §10, AI-08 OBS-003): on logs the tenant is the HMAC pseudonym
/// of the organization and the actor only its role kind. <c>TraceCorrelatedLoggerFactory</c> adds both to every line
/// written in an organization context (an API request with a selected organization, a Worker message or photo), so no
/// log message template under <c>src/**</c>, API and Worker alike, may name the real tenant, actor or organization id,
/// nor a user, subject or driver id: a driver profile is one-to-one with its user (AI-06
/// <c>drivers.driver_profiles.user_id</c> UNIQUE). Names that end in one of those ids (<c>OwnerOrgId</c>,
/// <c>DriverUserId</c>) are refused too, in any case. The technical ids of resources (an order, an assignment) stay
/// allowed, as DSP-002 logs them.
/// </summary>
public sealed partial class LogTemplateIdentifierArchitectureTests
{
    private static readonly string[] ForbiddenIdSuffixes =
        ["TenantId", "ActorId", "OrganizationId", "OrgId", "UserId", "SubjectId", "DriverId"];

    /// <summary>
    /// The seven lines that wrote the real organization id or the actor's user id until 2026-10-19; the scan must keep
    /// reading their files, so a reintroduced placeholder fails here.
    /// </summary>
    private static readonly string[] PreviouslyLeakingFiles =
    [
        "src/Modules/Dispatch/Dispatch.Infrastructure/Assignments/PostgreSqlAssignableDriversQuery.cs",
        "src/Modules/Dispatch/Dispatch.Infrastructure/Assignments/PostgreSqlAssignmentToOrderCoordinator.cs",
        "src/Modules/Dispatch/Dispatch.Infrastructure/Fleet/PostgreSqlOrganizationDriversQuery.cs",
        "src/Modules/Dispatch/Dispatch.Infrastructure/Stops/PostgreSqlDriverStopsQuery.cs",
        "src/Modules/Pricing/Pricing.Infrastructure/Quotes/PostgreSqlQuoteService.cs",
    ];

    /// <summary>Files the scan must read: API and Worker sites, a source-generated attribute, and the fixed lines.</summary>
    private static readonly string[] FilesTheScanReads =
    [
        "src/Modules/Dispatch/Dispatch.Infrastructure/Lifecycle/AssignmentLifecycleProcessor.cs",
        "src/Modules/Notifications/Notifications.Infrastructure/Dispatching/NotificationsOutboxProcessor.cs",
        "src/Modules/Identity/Identity.Endpoints/AuthCenter/AuthCenterOpenIdConnectEvents.cs",
        .. PreviouslyLeakingFiles,
    ];

    [Fact]
    public void No_log_template_names_a_tenant_actor_organization_user_subject_or_driver_id()
    {
        var index = new SourceIndex(SourceFiles());
        var templates = index.Paths.SelectMany(path => LogTemplates(path, index)).ToArray();

        var leaks = templates
            .SelectMany(template => ForbiddenNames(template.Literal)
                .Select(name => $"{template.Path}: {{{name}}} in {template.Literal}"))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.True(
            leaks.Length == 0,
            "Log templates name a real tenant, actor, organization, user, subject or driver id "
            + "(OBS-003-TENANT-PSEUDONYM-2026-10-12, AI-03 §18, AI-13 §10). In an organization context "
            + "TraceCorrelatedLoggerFactory already adds State.OrganizationPseudonym and State.ActorRoleKind; "
            + "remove the placeholder:\n" + string.Join('\n', leaks));

        // The scan reaches the API and Worker log sites, among them the previously leaking files, and it reads the
        // DSP-002 result line, whose order and assignment ids are technical resource ids and stay.
        foreach (var path in FilesTheScanReads)
        {
            Assert.True(templates.Any(template => template.Path == path), $"The scan read no log template in {path}.");
        }

        Assert.Contains(
            templates,
            template => template.Path == PreviouslyLeakingFiles[1] &&
                        template.Literal.Contains("{OrderId}", StringComparison.Ordinal) &&
                        template.Literal.Contains("{AssignmentId}", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(@"logger.LogInformation(""listed; tenant {TenantId}; count {Count}"", org, n);", "TenantId")]
    [InlineData(@"_logger.LogWarning(exception, ""actor {ActorId}"", a);", "ActorId")]
    [InlineData(@"logger.Log(LogLevel.Information, Events.Result, ""org {OrganizationId}"", o);", "OrganizationId")]
    [InlineData(@"logger.LogError(""user {userId:D}"", u);", "userId")]
    [InlineData(@"logger.LogDebug(""subject {@SubjectId}"", s);", "SubjectId")]
    [InlineData(@"logger.LogInformation(""driver {DriverId,10}"", d);", "DriverId")]
    [InlineData(@"logger.LogInformation(""owner {OwnerOrgId}"", o);", "OwnerOrgId")]
    [InlineData(@"logger.LogInformation(""first part; "" + ""tenant {TenantId}"", t);", "TenantId")]
    [InlineData(@"logger.LogInformation(@""tenant {TenantId}"", t);", "TenantId")]
    [InlineData("logger.LogInformation(\"\"\"tenant {TenantId}\"\"\", t);", "TenantId")]
    [InlineData("logger.LogInformation(\n    \"\"\"\n    tenant {TenantId}\n    \"\"\",\n    t);", "TenantId")]
    [InlineData(@"logger.LogInformation($""tenant {command.OrganizationId}"");", "OrganizationId")]
    [InlineData(@"logger.LogInformation($@""actor {query.ActorId:D}"");", "ActorId")]
    [InlineData("logger.LogInformation($$\"\"\"{literal} tenant {{command.TenantId}}\"\"\");", "TenantId")]
    [InlineData(@"logger.LogWarning(
        ""rejected ({FailureKind}); tenant {TenantId}"",
        kind,
        tenant);", "TenantId")]
    [InlineData(@"[LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = ""tenant {TenantId} ({FailureKind})."")]", "TenantId")]
    [InlineData(@"[LoggerMessage(1, LogLevel.Warning, ""actor {ActorId}"")]", "ActorId")]
    [InlineData(@"static readonly Action<ILogger, Guid, Exception?> L = LoggerMessage.Define<Guid>(LogLevel.Information, new EventId(1), ""org {OrganizationId}"");", "OrganizationId")]
    [InlineData(@"using var scope = logger.BeginScope(""tenant {TenantId}"", t);", "TenantId")]
    [InlineData(@"private const string Template = ""user {UserId}""; void M() => logger.LogInformation(Template, u);", "UserId")]
    [InlineData(@"static class T { public const string M = ""driver {DriverId}""; } void F() => logger.LogInformation(T.M, d);", "DriverId")]
    [InlineData(@"logger.LogInformation(""see https://example.test/a {Count}"", n); logger.LogInformation(""tenant {TenantId}"", t);", "TenantId")]
    [InlineData(@"var q = '""'; logger.LogInformation(""tenant {TenantId}"", t);", "TenantId")]
    public void The_scan_reads_every_way_a_log_template_is_written(string code, string expected)
    {
        Assert.Contains(expected, FixtureLeaks(code));
    }

    /// <summary>
    /// Route templates, redacted <c>ToString</c> overrides, SQL text, comments and escaped braces are not log
    /// templates, and order and assignment ids are allowed, so the scan reports none of them.
    /// </summary>
    [Theory]
    [InlineData(@"endpoints.MapPost(""/api/v1/organizations/{organizationId}/pending-memberships"", CreateAsync);")]
    [InlineData(@"public override string ToString() => $""Options {{ OrganizationId = {OrganizationId}, Phone = [redacted] }}"";")]
    [InlineData("var sql = \"\"\"SELECT 1 WHERE org_id = '{OrganizationId}'\"\"\"; logger.LogInformation(\"count {Count}\", n);")]
    [InlineData(@"logger.LogInformation(""order {OrderId}; assignment {AssignmentId}; result {Result}"", o, a, r);")]
    [InlineData(@"// logger.LogInformation(""tenant {TenantId}"", t);
        logger.LogInformation(""count {Count}"", n);")]
    [InlineData(@"/* logger.LogInformation(""tenant {TenantId}"", t); */ logger.LogInformation(""count {Count}"", n);")]
    [InlineData(@"var text = ""logger.LogInformation(\""tenant {TenantId}\"", t)""; logger.LogInformation(""count {Count}"", n);")]
    [InlineData(@"var text = @""a """"quoted"""" {TenantId}""; logger.LogInformation(""count {Count}"", n);")]
    [InlineData(@"logger.LogInformation(""literal {{TenantId}} braces; count {Count}"", n);")]
    public void The_scan_ignores_what_is_not_a_forbidden_log_template_placeholder(string code)
    {
        Assert.Empty(FixtureLeaks(code));
    }

    private static string[] FixtureLeaks(string code)
    {
        var index = new SourceIndex([("fixture.cs", code)]);
        return LogTemplates("fixture.cs", index).SelectMany(template => ForbiddenNames(template.Literal)).ToArray();
    }

    /// <summary>
    /// The template literals of every log site of a file: the string literals inside the arguments of each
    /// <c>.Log*(</c>, <c>.BeginScope(</c> and <c>LoggerMessage.Define*(</c> call and each <c>[LoggerMessage(</c>
    /// attribute, plus the literals of the string constants those arguments name (of the file, or
    /// <c>Type.Constant</c> of the repository).
    /// </summary>
    private static IEnumerable<(string Path, string Literal)> LogTemplates(string path, SourceIndex index)
    {
        var (masked, literals) = index.Lexed[path];
        foreach (Match site in LogSitePattern().Matches(masked))
        {
            var open = site.Index + site.Length - 1;
            var close = ClosingParenthesis(masked, open);
            foreach (var literal in literals.Where(literal => literal.Start > open && literal.Start < close))
            {
                yield return (path, literal.Text);
            }

            foreach (Match reference in ConstantReferencePattern().Matches(masked[(open + 1)..close]))
            {
                var sources = reference.Groups["type"].Success
                    ? index.Declaring(reference.Groups["type"].Value)
                    : [path];
                foreach (var constant in sources.SelectMany(source => index.Constants(source, reference.Groups["name"].Value)))
                {
                    yield return (path, constant);
                }
            }
        }
    }

    /// <summary>Every file lexed once, with its string constants and the types it declares.</summary>
    private sealed class SourceIndex
    {
        private readonly Dictionary<string, Dictionary<string, List<string>>> _constants = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<string>> _declarations = new(StringComparer.Ordinal);

        internal SourceIndex(IEnumerable<(string Path, string Text)> files)
        {
            foreach (var (path, text) in files)
            {
                var lexed = Lex(text);
                Lexed[path] = lexed;
                _constants[path] = StringConstants(lexed.Masked, lexed.Literals);
                foreach (Match declaration in TypeDeclarationPattern().Matches(lexed.Masked))
                {
                    var type = declaration.Groups["name"].Value;
                    if (!_declarations.TryGetValue(type, out var paths))
                    {
                        _declarations[type] = paths = [];
                    }

                    paths.Add(path);
                }
            }
        }

        internal Dictionary<string, (string Masked, List<Literal> Literals)> Lexed { get; } = new(StringComparer.Ordinal);

        internal IEnumerable<string> Paths => Lexed.Keys;

        internal IEnumerable<string> Declaring(string type) =>
            _declarations.TryGetValue(type, out var paths) ? paths : [];

        internal IEnumerable<string> Constants(string path, string name) =>
            _constants[path].TryGetValue(name, out var literals) ? literals : [];
    }

    /// <summary>The placeholder or interpolated names of one literal that name a forbidden id.</summary>
    private static IEnumerable<string> ForbiddenNames(string literal)
    {
        var names = IsInterpolated(literal)
            ? InterpolationHoles(literal).SelectMany(hole => IdentifierPattern().Matches(hole).Select(match => match.Value))
            : PlaceholderPattern().Matches(literal).Select(match => match.Groups["name"].Value);
        return names.Where(name => ForbiddenIdSuffixes.Any(suffix => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>The literals of each string constant (<c>const</c> or <c>static readonly</c>) of a file, by name.</summary>
    private static Dictionary<string, List<string>> StringConstants(string masked, List<Literal> literals)
    {
        var constants = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (Match declaration in StringConstantPattern().Matches(masked))
        {
            var start = declaration.Index + declaration.Length;
            var end = masked.IndexOf(';', start);
            end = end < 0 ? masked.Length : end;
            var name = declaration.Groups["name"].Value;
            if (!constants.TryGetValue(name, out var values))
            {
                constants[name] = values = [];
            }

            values.AddRange(literals.Where(literal => literal.Start >= start && literal.Start < end).Select(literal => literal.Text));
        }

        return constants;
    }

    private static int ClosingParenthesis(string masked, int open)
    {
        var depth = 0;
        for (var index = open; index < masked.Length; index++)
        {
            depth += masked[index] switch
            {
                '(' => 1,
                ')' => -1,
                _ => 0,
            };
            if (depth == 0)
            {
                return index;
            }
        }

        return masked.Length;
    }

    private sealed record Literal(int Start, string Text);

    /// <summary>
    /// Splits C# source into its string literals (regular, verbatim, raw, and their interpolated forms) and a masked
    /// copy of the code where comments, character literals and string literals are blanked, so call sites and
    /// parentheses are only matched in code.
    /// </summary>
    private static (string Masked, List<Literal> Literals) Lex(string code)
    {
        var masked = code.ToCharArray();
        var literals = new List<Literal>();
        var index = 0;
        while (index < code.Length)
        {
            var end = index;
            if (code[index] == '/' && At(code, index + 1) == '/')
            {
                end = code.IndexOf('\n', index);
                end = end < 0 ? code.Length : end;
            }
            else if (code[index] == '/' && At(code, index + 1) == '*')
            {
                end = code.IndexOf("*/", index + 2, StringComparison.Ordinal);
                end = end < 0 ? code.Length : end + 2;
            }
            else if (code[index] == '\'')
            {
                end = EndOfCharacter(code, index);
            }
            else if (IsStringStart(code, index))
            {
                end = EndOfString(code, index);
                literals.Add(new Literal(index, code[index..end]));
            }

            if (end == index)
            {
                index++;
                continue;
            }

            Array.Fill(masked, ' ', index, end - index);
            index = end;
        }

        return (new string(masked), literals);
    }

    private static char At(string text, int index) => index < text.Length ? text[index] : '\0';

    /// <summary>A quote, or the '@' and '$' prefixes of a literal (an '@' identifier is never followed by a quote).</summary>
    private static bool IsStringStart(string code, int index)
    {
        while (At(code, index) is '@' or '$')
        {
            index++;
        }

        return At(code, index) == '"';
    }

    private static int EndOfCharacter(string code, int index)
    {
        var position = index + 1;
        while (position < code.Length && code[position] != '\'' && code[position] != '\n')
        {
            position += code[position] == '\\' ? 2 : 1;
        }

        return Math.Min(position + 1, code.Length);
    }

    private static (int Dollars, bool Verbatim, int Quotes, int Body) Prefix(string literal, int index)
    {
        var dollars = 0;
        var verbatim = false;
        while (At(literal, index) is '@' or '$')
        {
            dollars += literal[index] == '$' ? 1 : 0;
            verbatim |= literal[index] == '@';
            index++;
        }

        var quotes = 0;
        while (At(literal, index + quotes) == '"')
        {
            quotes++;
        }

        // A raw literal opens with three or more quotes; "" is the empty regular string and @"" the empty verbatim one.
        var raw = !verbatim && quotes >= 3;
        return (dollars, verbatim, raw ? quotes : 1, index + (raw ? quotes : 1));
    }

    private static int EndOfString(string code, int index)
    {
        var (dollars, verbatim, quotes, position) = Prefix(code, index);
        if (quotes >= 3)
        {
            var close = code.IndexOf(new string('"', quotes), position, StringComparison.Ordinal);
            return close < 0 ? code.Length : close + quotes;
        }

        var depth = 0;
        while (position < code.Length)
        {
            var character = code[position];
            if (depth > 0)
            {
                if (character == '\'')
                {
                    position = EndOfCharacter(code, position);
                    continue;
                }

                if (IsStringStart(code, position))
                {
                    position = EndOfString(code, position);
                    continue;
                }

                depth += character switch
                {
                    '{' => 1,
                    '}' => -1,
                    _ => 0,
                };
                position++;
                continue;
            }

            if (!verbatim && character == '\\')
            {
                position += 2;
            }
            else if (character == '"' && verbatim && At(code, position + 1) == '"')
            {
                position += 2;
            }
            else if (character == '"')
            {
                return position + 1;
            }
            else if (dollars > 0 && character is '{' or '}' && At(code, position + 1) == character)
            {
                position += 2;
            }
            else if (dollars > 0 && character == '{')
            {
                depth = 1;
                position++;
            }
            else if (!verbatim && character == '\n')
            {
                return position;
            }
            else
            {
                position++;
            }
        }

        return code.Length;
    }

    private static bool IsInterpolated(string literal) => Prefix(literal, 0).Dollars > 0;

    /// <summary>The expressions of an interpolated literal's holes (a raw one with n dollars opens a hole with n braces).</summary>
    private static IEnumerable<string> InterpolationHoles(string literal)
    {
        var (dollars, _, quotes, position) = Prefix(literal, 0);
        var opening = quotes >= 3 ? dollars : 1;
        var end = literal.Length - quotes;
        while (position < end)
        {
            if (literal[position] != '{')
            {
                position++;
                continue;
            }

            var run = 0;
            while (At(literal, position + run) == '{')
            {
                run++;
            }

            // Fewer braces than a hole needs are literal text; in a regular or verbatim literal "{{" is an escape.
            if (run < opening || (quotes < 3 && run % 2 == 0))
            {
                position += run;
                continue;
            }

            var start = position + run;
            var depth = 1;
            var cursor = start;
            while (cursor < end && depth > 0)
            {
                depth += literal[cursor] switch
                {
                    '{' => 1,
                    '}' => -1,
                    _ => 0,
                };
                cursor++;
            }

            yield return literal[start..Math.Max(start, cursor - 1)];
            position = cursor;
        }
    }

    private static IEnumerable<(string Path, string Text)> SourceFiles() =>
        Directory.EnumerateFiles(TestRepository.GetPath("src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                           !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(path => (Path.GetRelativePath(TestRepository.Root, path).Replace('\\', '/'), File.ReadAllText(path)));

    [GeneratedRegex(@"\.\s*(?:Log\w*|BeginScope|Define(?:Scope)?)\s*(?:<[^<>()]*>)?\s*\(|\bLoggerMessage(?:Attribute)?\s*\(")]
    private static partial Regex LogSitePattern();

    [GeneratedRegex(@"(?<![\w.])(?:(?<type>[A-Za-z_]\w*)\s*\.\s*)?(?<name>[A-Za-z_]\w*)\b(?!\s*[.(<])")]
    private static partial Regex ConstantReferencePattern();

    [GeneratedRegex(@"\b(?:const|static\s+readonly)\s+string\s+(?<name>[A-Za-z_]\w*)\s*=")]
    private static partial Regex StringConstantPattern();

    [GeneratedRegex(@"\b(?:class|struct|record|interface)\s+(?<name>[A-Za-z_]\w*)")]
    private static partial Regex TypeDeclarationPattern();

    [GeneratedRegex(@"(?<!\{)\{(?!\{)\s*[@$]?(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*(?:,\s*-?\d+\s*)?(?::[^{}]*)?\}")]
    private static partial Regex PlaceholderPattern();

    [GeneratedRegex(@"[A-Za-z_][A-Za-z0-9_]*")]
    private static partial Regex IdentifierPattern();
}
