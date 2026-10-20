using System.Text.RegularExpressions;
using Paqueteria.ArchitectureTests.Architecture;
using Paqueteria.Infrastructure.Database;

namespace Paqueteria.ArchitectureTests;

/// <summary>
/// AI-13 §4 and §15, AI-03 §25.9 and AI-01 §4.18; RTE-001-ROUTING-ASSIGNMENT-FLOW6-2026-10-20 (project owner, G1:
/// "Registrarlo como flujo 6 (Recommended)"; ADR-RTE-001-ROUTE-ASSIGNMENT-FLOW6). Every runtime SQL statement under
/// <c>src/</c> (migrations and the database baseline excluded) that inserts, updates, deletes or merges into the
/// schema of another module is listed in <see cref="Catalog"/> with the AI-13 §4 flow (its AI-04
/// <c>transaction_coordinator_allowlist</c> entry) or the ADR that allows it. A new site, a removed site or a site
/// without an allowed flow fails. The shared schemas (<c>platform</c>, <c>security</c>, <c>extensions</c>) are not
/// module schemas: outbox, audit and idempotency rows are written in the module's own transaction (AI-03 §25.1).
/// How to add a site: <c>docs/development/rte-001-routing-assignment-flow6.md</c>.
/// </summary>
public sealed partial class CrossSchemaWriteCatalogTests
{
    // The six AI-13 §4 flows, in order, named as AI-04 transaction_coordinator_allowlist names them.
    private const string QuoteSnapshotToOrder = "quote_snapshot_to_order";
    private const string AssignmentToOrderStatusEvent = "assignment_to_order_status_event";
    private const string ProofCustodyToOrderTransition = "proof_custody_to_order_transition";
    private const string CodReconciliationToClose = "cod_reconciliation_to_close";
    private const string ExternalOfferAcceptanceToAssignment = "external_offer_acceptance_to_assignment";
    private const string RouteStopToAssignmentRouteId = "route_stop_to_assignment_route_id";

    private static readonly string[] Flows =
    [
        QuoteSnapshotToOrder,
        AssignmentToOrderStatusEvent,
        ProofCustodyToOrderTransition,
        CodReconciliationToClose,
        ExternalOfferAcceptanceToAssignment,
        RouteStopToAssignmentRouteId,
    ];

    /// <summary>A cross-module write outside the AI-13 §4 flows that an ADR allows, and that ADR's file.</summary>
    private static readonly IReadOnlyDictionary<string, string> Adrs = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // AI-01 §4.4: provisioning creates the first user, organization, membership and audit in one transaction.
        ["ADR-031"] = "docs/normative/v0.6/docs/adr/ADR-031_RLS_PROVISIONING.md",
    };

    private const string QuoteCoordinator =
        "src/Modules/Orders/Orders.Infrastructure/Orders/QuoteSnapshotToOrderCoordinator.cs";
    private const string AssignmentCoordinator =
        "src/Modules/Dispatch/Dispatch.Infrastructure/Assignments/PostgreSqlAssignmentToOrderCoordinator.cs";
    private const string ExternalOfferService =
        "src/Modules/Dispatch/Dispatch.Infrastructure/ExternalOffers/PostgreSqlExternalOfferService.cs";
    private const string RoutePersistence =
        "src/Modules/Routing/Routing.Infrastructure/Routes/PostgreSqlRouteService.Persistence.cs";
    private const string InitialProvisioner =
        "src/Modules/Organizations/Organizations.Infrastructure/Provisioning/PostgreSqlInitialOrganizationProvisioner.cs";

    /// <summary>Every runtime statement that writes another module's schema: one entry per statement.</summary>
    private static readonly CrossSchemaWrite[] Catalog =
    [
        // Flow 1: the new order marks its quote USED.
        new(QuoteCoordinator, "UPDATE", "pricing.quotes", QuoteSnapshotToOrder),
        // Flow 2: the accepted assignment moves the order to ASSIGNED and appends its order event.
        new(AssignmentCoordinator, "UPDATE", "orders.orders", AssignmentToOrderStatusEvent),
        new(AssignmentCoordinator, "INSERT INTO", "orders.order_events", AssignmentToOrderStatusEvent),
        // Flow 5: the accepted external offer creates its assignment and, in the same transaction, the order
        // transition and order event of flow 2 (AI-08 EXT-001 "aceptación atómica").
        new(ExternalOfferService, "UPDATE", "orders.orders", ExternalOfferAcceptanceToAssignment),
        new(ExternalOfferService, "INSERT INTO", "orders.order_events", ExternalOfferAcceptanceToAssignment),
        // Flow 6: addRouteStop links the assignment to the route; removeRouteStop clears that route_id (AI-08 RTE-001
        // "route stop and assignment.route_id are updated atomically").
        new(RoutePersistence, "UPDATE", "dispatch.assignments", RouteStopToAssignmentRouteId),
        new(RoutePersistence, "UPDATE", "dispatch.assignments", RouteStopToAssignmentRouteId),
        // ADR-031: initial organization provisioning creates its first user.
        new(InitialProvisioner, "INSERT INTO", "identity.users", "ADR-031"),
    ];

    /// <summary>
    /// The own schema of each code module under <c>src/Modules</c>, through its AI-03 §8 module in
    /// <see cref="DatabaseSchemaCatalog"/>. Routing is the AI-03 §8 module Routes; Realtime owns no schema. Clients and
    /// Allies have no code module, so every write into their schemas is a cross-module write.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string?> CatalogModuleOfCodeModule =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Custody"] = "Custody",
            ["Dispatch"] = "Dispatch",
            ["Drivers"] = "Drivers",
            ["Finance"] = "Finance",
            ["Identity"] = "Identity",
            ["Incidents"] = "Incidents",
            ["Locations"] = "Locations",
            ["Notifications"] = "Notifications",
            ["Orders"] = "Orders",
            ["Organizations"] = "Organizations",
            ["Pricing"] = "Pricing",
            ["Realtime"] = null,
            ["Reporting"] = "Reporting",
            ["Routing"] = "Routes",
        };

    [Fact]
    public void Every_runtime_write_into_another_module_schema_is_catalogued_with_its_flow_or_adr()
    {
        var violations = Violations(Scan(RuntimeSources()), Catalog);

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void The_catalogue_allows_exactly_the_six_ai04_flows_and_existing_adrs()
    {
        var ai04 = File.ReadAllLines(TestRepository.GetPath("docs/normative/v0.6/specs/AI-04_DOMAIN_MODEL.yaml"));
        var start = Array.IndexOf(ai04, "transaction_coordinator_allowlist:");
        Assert.True(start >= 0, "AI-04 transaction_coordinator_allowlist is missing");
        var allowlist = ai04.Skip(start + 1).TakeWhile(line => line.StartsWith("- ", StringComparison.Ordinal))
            .Select(line => line[2..].Trim())
            .ToArray();
        Assert.Equal(Flows, allowlist);

        // AI-13 §4 numbers the same six flows.
        var ai13 = File.ReadAllText(TestRepository.GetPath("docs/normative/v0.6/specs/AI-13_DOTNET_SOLUTION_BLUEPRINT.md"));
        var flows = ai13[ai13.IndexOf("Flujos atómicos permitidos", StringComparison.Ordinal)..];
        flows = flows[..flows.IndexOf("## 5. API", StringComparison.Ordinal)];
        Assert.Equal(
            Enumerable.Range(1, Flows.Length).Select(number => $"{number}."),
            Regex.Matches(flows, @"^\d+\.", RegexOptions.Multiline).Select(match => match.Value));

        Assert.All(Adrs.Values, path => Assert.True(File.Exists(TestRepository.GetPath(path)), path));
        Assert.Empty(Flows.Intersect(Adrs.Keys, StringComparer.Ordinal));
    }

    [Fact]
    public void Every_code_module_has_its_own_schema_from_the_schema_catalog()
    {
        var modules = Directory.GetDirectories(TestRepository.GetPath("src/Modules"))
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(CatalogModuleOfCodeModule.Keys.Order(StringComparer.Ordinal), modules);
        Assert.All(
            CatalogModuleOfCodeModule.Values.OfType<string>(),
            module => Assert.Single(DatabaseSchemaCatalog.Modules, item => item.Module == module));
        Assert.Equal("routes", OwnSchemaOf("src/Modules/Routing/Routing.Infrastructure/Routes/X.cs"));
        Assert.Null(OwnSchemaOf("src/Modules/Realtime/Realtime.Infrastructure/X.cs"));
        Assert.Null(OwnSchemaOf("src/Paqueteria.Worker/X.cs"));
    }

    /// <summary>
    /// The scan reads schema-qualified targets only. An unqualified target resolves through the default search_path
    /// (<c>"$user", public</c>), which holds no module table, as long as no runtime code changes it; a target built at
    /// run time (an interpolation hole or a concatenated string) would hide the schema from the scan.
    /// </summary>
    [Fact]
    public void Runtime_sql_neither_changes_the_search_path_nor_builds_a_write_target_at_run_time()
    {
        var sources = RuntimeSources().ToArray();
        Assert.NotEmpty(sources);
        Assert.Empty(sources.Where(source => SearchPathPattern().IsMatch(WithoutCommentLines(source.Code)))
            .Select(source => source.File));
        Assert.Empty(sources.SelectMany(source =>
        {
            var code = WithoutCommentLines(source.Code);
            return DynamicTargetPattern().Matches(code).Select(match => $"{source.File}:{LineOf(code, match.Index)}");
        }));
    }

    /// <summary>The same check, over in-memory sources: each kind of drift fails with a message that names the fix.</summary>
    [Fact]
    public void The_check_fails_on_a_new_site_a_removed_site_and_a_site_without_a_flow()
    {
        var real = RuntimeSources().ToArray();
        Assert.Empty(Violations(Scan(real), Catalog));

        // A new foreign write: Notifications updating an order.
        var added = real.Append(new SourceFile(
            "src/Modules/Notifications/Notifications.Infrastructure/Fixture.cs",
            "const string Sql = \"\"\"\n    UPDATE orders.orders SET status='CANCELLED' WHERE id=@id\n    \"\"\";"));
        var newSite = Assert.Single(Violations(Scan(added), Catalog));
        Assert.Contains("Notifications.Infrastructure/Fixture.cs:2 UPDATE orders.orders", newSite, StringComparison.Ordinal);
        Assert.Contains("needs an ADR", newSite, StringComparison.Ordinal);

        // A second statement of a catalogued kind in a catalogued file is a new site too.
        var duplicated = real.Select(source => source.File == RoutePersistence
            ? source with { Code = source.Code + "\n\"UPDATE dispatch.assignments SET route_id=NULL WHERE id=@id\";" }
            : source);
        Assert.Contains("needs an ADR", Assert.Single(Violations(Scan(duplicated), Catalog)), StringComparison.Ordinal);

        // A removed site: the unlink statement is gone.
        var removed = real.Select(source => source.File == RoutePersistence
            ? source with { Code = source.Code.Replace("UPDATE dispatch.assignments SET route_id=NULL", "SELECT 1", StringComparison.Ordinal) }
            : source);
        var removedSite = Assert.Single(Violations(Scan(removed), Catalog));
        Assert.Contains("no longer found", removedSite, StringComparison.Ordinal);
        Assert.Contains("UPDATE dispatch.assignments", removedSite, StringComparison.Ordinal);

        // A catalogued site without an allowed flow or ADR.
        var unflowed = Catalog.Select(entry => entry.Target == "identity.users" ? entry with { Allowed = "provisioning" } : entry)
            .ToArray();
        var noFlow = Assert.Single(Violations(Scan(real), unflowed));
        Assert.Contains("names no AI-13 §4 flow", noFlow, StringComparison.Ordinal);
        Assert.Contains("ADR", noFlow, StringComparison.Ordinal);
    }

    [Fact]
    public void The_scan_reads_every_write_form_and_ignores_own_shared_and_commented_targets()
    {
        const string Fixture = "src/Modules/Routing/Routing.Infrastructure/Fixture.cs";
        var found = Scan(
        [
            new SourceFile(Fixture, string.Join('\n',
                "\"INSERT INTO routes.route_stops(id) VALUES (@id)\";",
                "\"UPDATE routes.routes SET version=version+1\";",
                "\"INSERT INTO platform.outbox_events(id) VALUES (@id)\";",
                "\"UPDATE platform.idempotency_keys SET status_code=200\";",
                "// UPDATE orders.orders SET status='ASSIGNED' (a comment)",
                "\"\"\"",
                "update ONLY \"dispatch\" . \"assignments\" set route_id=NULL",
                "\"\"\";",
                "\"DELETE",
                "   FROM drivers.driver_positions\";",
                "\"MERGE INTO finance.cod_transactions AS t USING x ON true\";",
                "\"insert into Clients.client_accounts(id) values (@id)\";",
                "\"SELECT 1 FROM orders.orders FOR UPDATE OF orders\";",
                "\"INSERT INTO platform.audit_logs(id) VALUES (@id) ON CONFLICT DO UPDATE SET id=@id\";")),
            new SourceFile("src/Paqueteria.Worker/Fixture.cs", "\"DELETE FROM orders.order_events\";"),
            new SourceFile("src/BuildingBlocks/Paqueteria.Infrastructure/Fixture.cs", "\"UPDATE platform.outbox_events SET x=1\";"),
        ]);

        Assert.Equal(
            [
                $"{Fixture}:7 UPDATE dispatch.assignments",
                $"{Fixture}:9 DELETE FROM drivers.driver_positions",
                $"{Fixture}:11 MERGE INTO finance.cod_transactions",
                $"{Fixture}:12 INSERT INTO clients.client_accounts",
                "src/Paqueteria.Worker/Fixture.cs:1 DELETE FROM orders.order_events",
            ],
            found.Select(write => write.ToString()));
    }

    private static List<string> Violations(IReadOnlyList<FoundWrite> found, IReadOnlyList<CrossSchemaWrite> catalog)
    {
        var violations = new List<string>();
        var expected = catalog.GroupBy(entry => entry.Key).ToDictionary(group => group.Key, group => group.Count());
        foreach (var group in found.GroupBy(write => write.Key))
        {
            var allowed = expected.GetValueOrDefault(group.Key);
            foreach (var write in group.Skip(allowed))
            {
                violations.Add(
                    $"New cross-module write {write}: it is not catalogued. A write into another module's schema " +
                    "needs an ADR (AI-01 §4.18) that adds its flow to AI-13 §4 and to the AI-04 " +
                    "transaction_coordinator_allowlist, unless an existing flow or ADR already covers it; then add it " +
                    "to CrossSchemaWriteCatalogTests.Catalog (docs/development/rte-001-routing-assignment-flow6.md).");
            }
        }

        var actual = found.GroupBy(write => write.Key).ToDictionary(group => group.Key, group => group.Count());
        foreach (var (key, count) in expected)
        {
            for (var missing = actual.GetValueOrDefault(key); missing < count; missing++)
            {
                violations.Add(
                    $"Catalogued cross-module write {key.File} {key.Statement} {key.Target} is no longer found: " +
                    "remove its entry from CrossSchemaWriteCatalogTests.Catalog and from " +
                    "docs/development/rte-001-routing-assignment-flow6.md.");
            }
        }

        foreach (var entry in catalog.Where(entry => !Flows.Contains(entry.Allowed, StringComparer.Ordinal) &&
                                                     !Adrs.ContainsKey(entry.Allowed)))
        {
            violations.Add(
                $"Catalogued cross-module write {entry.File} {entry.Statement} {entry.Target} names no AI-13 §4 flow " +
                $"or ADR ('{entry.Allowed}'): name its AI-04 transaction_coordinator_allowlist flow, or add an ADR.");
        }

        return violations;
    }

    private static List<FoundWrite> Scan(IEnumerable<SourceFile> sources)
    {
        var shared = DatabaseSchemaCatalog.SharedSchemas.ToHashSet(StringComparer.Ordinal);
        var found = new List<FoundWrite>();
        foreach (var source in sources)
        {
            var own = OwnSchemaOf(source.File);
            var code = WithoutCommentLines(source.Code);
            foreach (Match match in WritePattern().Matches(code))
            {
                var schema = Identifier(match.Groups["schema"].Value);
                if (shared.Contains(schema) || string.Equals(schema, own, StringComparison.Ordinal))
                {
                    continue;
                }

                found.Add(new FoundWrite(
                    source.File,
                    LineOf(code, match.Index),
                    string.Join(' ', match.Groups["statement"].Value.ToUpperInvariant()
                        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)),
                    $"{schema}.{Identifier(match.Groups["table"].Value)}"));
            }
        }

        return found;
    }

    private static string? OwnSchemaOf(string file)
    {
        const string modules = "src/Modules/";
        if (!file.StartsWith(modules, StringComparison.Ordinal))
        {
            return null;
        }

        var codeModule = file[modules.Length..file.IndexOf('/', modules.Length)];
        if (!CatalogModuleOfCodeModule.TryGetValue(codeModule, out var catalogModule))
        {
            throw new InvalidOperationException(
                $"Code module {codeModule} has no entry in CrossSchemaWriteCatalogTests.CatalogModuleOfCodeModule.");
        }

        return catalogModule is null
            ? null
            : DatabaseSchemaCatalog.Modules.Single(item => item.Module == catalogModule).Schema;
    }

    // Unquoted identifiers fold to lower case in PostgreSQL; the schema names are lower case.
    private static string Identifier(string value) => value.Trim('"').ToLowerInvariant();

    private static int LineOf(string code, int index) => code.AsSpan(0, index).Count('\n') + 1;

    /// <summary>The runtime C# sources under <c>src/</c>: migrations and the database baseline are not runtime SQL.</summary>
    private static IEnumerable<SourceFile> RuntimeSources()
    {
        var separator = Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(TestRepository.GetPath("src"), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{separator}bin{separator}", StringComparison.Ordinal) &&
                           !file.Contains($"{separator}obj{separator}", StringComparison.Ordinal) &&
                           !file.Contains($"{separator}Migrations{separator}", StringComparison.Ordinal) &&
                           !file.Contains($"{separator}Baseline{separator}", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(file => new SourceFile(
                Path.GetRelativePath(TestRepository.Root, file).Replace(separator, '/'),
                File.ReadAllText(file)));
    }

    // C# comment lines (// and ///) are blanked, so a comment that names a write is not one; line numbers are kept.
    private static string WithoutCommentLines(string code) => string.Join('\n', code.Split('\n').Select(line =>
        line.TrimStart().StartsWith("//", StringComparison.Ordinal) ? string.Empty : line));

    private sealed record SourceFile(string File, string Code);

    private sealed record CrossSchemaWrite(string File, string Statement, string Target, string Allowed)
    {
        public (string File, string Statement, string Target) Key => (File, Statement, Target);
    }

    private sealed record FoundWrite(string File, int Line, string Statement, string Target)
    {
        public (string File, string Statement, string Target) Key => (File, Statement, Target);

        public override string ToString() => $"{File}:{Line} {Statement} {Target}";
    }

    // INSERT INTO, UPDATE, DELETE FROM or MERGE INTO a schema-qualified table, quoted or not, with or without ONLY.
    [GeneratedRegex(
        @"\b(?<statement>INSERT\s+INTO|UPDATE|DELETE\s+FROM|MERGE\s+INTO)\s+(?:ONLY\s+)?(?<schema>""?[A-Za-z_][A-Za-z0-9_]*""?)\s*\.\s*(?<table>""?[A-Za-z_][A-Za-z0-9_]*""?)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WritePattern();

    [GeneratedRegex(@"search[\s_]*path", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SearchPathPattern();

    // A write whose target is an interpolation hole or the end of a string that is concatenated.
    [GeneratedRegex(
        @"\b(?:INSERT\s+INTO|UPDATE|DELETE\s+FROM|MERGE\s+INTO)\s+(?:ONLY\s+)?(?:\{|""\s*\+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DynamicTargetPattern();
}
