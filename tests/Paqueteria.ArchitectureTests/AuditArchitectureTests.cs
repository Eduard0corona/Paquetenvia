using Paqueteria.Application.Auditing;
using Paqueteria.ArchitectureTests.Architecture;
using Paqueteria.Infrastructure.Auditing;

namespace Paqueteria.ArchitectureTests;

public sealed class AuditArchitectureTests
{
    [Fact]
    public void General_audit_contract_is_in_application_and_PostgreSql_writer_is_in_infrastructure()
    {
        Assert.Equal("Paqueteria.Application", typeof(IAppendOnlyAuditWriter).Assembly.GetName().Name);
        Assert.Equal("Paqueteria.Application", typeof(AuditEntry).Assembly.GetName().Name);
        Assert.Equal("Paqueteria.Application", typeof(IAuditPayloadRedactor).Assembly.GetName().Name);
        Assert.Equal("Paqueteria.Infrastructure", typeof(PostgreSqlAppendOnlyAuditWriter).Assembly.GetName().Name);
        Assert.Contains(typeof(IAppendOnlyAuditWriter), typeof(PostgreSqlAppendOnlyAuditWriter).GetInterfaces());
    }

    [Fact]
    public void Productive_audit_insert_exists_only_in_the_general_writer()
    {
        var sources = Directory.GetFiles(TestRepository.GetPath("src"), "*.cs", SearchOption.AllDirectories)
            .Select(path => new { Path = path, Source = File.ReadAllText(path) })
            .Where(file => file.Source.Contains("INSERT INTO platform.audit_logs", StringComparison.Ordinal))
            .ToArray();

        // REG-001 and REG-002 are the only SQL-level exceptions: onboarding, the ALLY decision and pending
        // memberships write their audit rows for an organization the caller's tenant context cannot see yet
        // (pre-tenant, the sign-in that accepts an entry) or at all (cross-tenant), so the rows are inserted
        // inside the SECURITY DEFINER functions of the Organizations lane, in the same transaction as the
        // change they record. Nothing else may insert audit rows outside the general writer.
        var registrationLane = TestRepository.GetPath(
            "src/Modules/Organizations/Organizations.Infrastructure/Persistence/Migrations/20260927000400_AddSelfServiceRegistration.cs");
        var pendingMembershipLane = TestRepository.GetPath(
            "src/Modules/Organizations/Organizations.Infrastructure/Persistence/Migrations/20260927000500_AddPendingMemberships.cs");
        Assert.Equal(
            new[]
            {
                TestRepository.GetPath("src/BuildingBlocks/Paqueteria.Infrastructure/Auditing/PostgreSqlAppendOnlyAuditWriter.cs"),
                registrationLane,
                pendingMembershipLane,
            }.Order(StringComparer.OrdinalIgnoreCase),
            sources.Select(source => source.Path).Order(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

        var lane = sources.Single(source => string.Equals(source.Path, registrationLane, StringComparison.OrdinalIgnoreCase)).Source;
        var auditing = lane.Split("CREATE OR REPLACE FUNCTION ", StringSplitOptions.None).Skip(1)
            .Where(body => body.Contains("INSERT INTO platform.audit_logs", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(
            ["security.create_self_service_organization", "security.decide_ally_organization"],
            auditing.Select(body => body[..body.IndexOf('(', StringComparison.Ordinal)]).Order(StringComparer.Ordinal));
        Assert.All(auditing, body =>
        {
            var definition = body[..body.IndexOf("$function$;", StringComparison.Ordinal)];
            Assert.Contains("SECURITY DEFINER", definition, StringComparison.Ordinal);
            Assert.DoesNotContain("RETURNING", definition, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1, definition.Split("INSERT INTO platform.audit_logs", StringSplitOptions.None).Length - 1);
        });

        // Exactly those two in the whole file: an insert in a DO block, in the rollback or anywhere outside
        // the two functions fails here.
        Assert.Equal(2, lane.Split("INSERT INTO platform.audit_logs", StringSplitOptions.None).Length - 1);

        // REG-002: add writes ADDED or RENEWED (one insert on each path), renew, revoke and the sign-in
        // acceptance one each; five in the whole file, none outside those functions.
        var pending = sources.Single(source =>
            string.Equals(source.Path, pendingMembershipLane, StringComparison.OrdinalIgnoreCase)).Source;
        var pendingAuditing = pending.Split("CREATE OR REPLACE FUNCTION ", StringSplitOptions.None).Skip(1)
            .Where(body => body.Contains("INSERT INTO platform.audit_logs", StringComparison.Ordinal))
            .ToDictionary(body => body[..body.IndexOf('(', StringComparison.Ordinal)], StringComparer.Ordinal);
        Assert.Equal(
            [
                "security.add_pending_membership",
                "security.apply_pending_memberships",
                "security.renew_pending_membership",
                "security.revoke_pending_membership",
            ],
            pendingAuditing.Keys.Order(StringComparer.Ordinal));
        foreach (var (name, body) in pendingAuditing)
        {
            var definition = body[..body.IndexOf("$function$;", StringComparison.Ordinal)];
            Assert.Contains("SECURITY DEFINER", definition, StringComparison.Ordinal);
            Assert.DoesNotContain("RETURNING", definition, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(
                name == "security.add_pending_membership" ? 2 : 1,
                definition.Split("INSERT INTO platform.audit_logs", StringSplitOptions.None).Length - 1);
        }

        Assert.Equal(5, pending.Split("INSERT INTO platform.audit_logs", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void Audit_writer_is_parameterized_transactional_and_has_no_returning_or_logging()
    {
        var source = File.ReadAllText(TestRepository.GetPath(
            "src/BuildingBlocks/Paqueteria.Infrastructure/Auditing/PostgreSqlAppendOnlyAuditWriter.cs"));

        Assert.Contains("DbConnection connection", source, StringComparison.Ordinal);
        Assert.Contains("DbTransaction transaction", source, StringComparison.Ordinal);
        Assert.Contains("NpgsqlParameter<Guid>", source, StringComparison.Ordinal);
        Assert.Contains("NpgsqlDbType.Jsonb", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RETURNING", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ILogger", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new NpgsqlConnection", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Productive_worker_uses_module_ports_without_direct_database_or_audit_dependencies()
    {
        var metadata = ProjectMetadataReader.Read(SolutionCatalog.Worker);
        Assert.DoesNotContain(metadata.PackageReferences, package =>
            package.Contains("Npgsql", StringComparison.OrdinalIgnoreCase));

        var source = File.ReadAllText(TestRepository.GetPath("src/Paqueteria.Worker/Program.cs"));
        Assert.DoesNotContain("Npgsql", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Audit", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ConnectionString", source, StringComparison.OrdinalIgnoreCase);
    }
}
