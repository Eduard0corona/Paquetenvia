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

        // REG-001 and REG-002 are SQL-level exceptions: onboarding, the ALLY decision and pending
        // memberships write their audit rows for an organization the caller's tenant context cannot see yet
        // (pre-tenant, the sign-in that accepts an entry) or at all (cross-tenant), so the rows are inserted
        // inside the SECURITY DEFINER functions of the Organizations lane, in the same transaction as the
        // change they record. MDM-001-OPERATOR-LOADER is the third: the operator job has EXECUTE only and no
        // table privilege, so its one audit row per load is inserted by the loader function in the same
        // transaction as the load. Nothing else may insert audit rows outside the general writer.
        var registrationLane = TestRepository.GetPath(
            "src/Modules/Organizations/Organizations.Infrastructure/Persistence/Migrations/20260927000400_AddSelfServiceRegistration.cs");
        var pendingMembershipLane = TestRepository.GetPath(
            "src/Modules/Organizations/Organizations.Infrastructure/Persistence/Migrations/20260927000500_AddPendingMemberships.cs");
        var masterDataLane = TestRepository.GetPath(
            "src/Modules/Pricing/Pricing.Infrastructure/Persistence/Migrations/20260928000100_AddMasterDataLoader.cs");
        // DSP-OPERATOR-OWNER-OUTBOX-DEFINER-2026-10-03 is the fourth: the operator of another owner's order cannot
        // write the owner's audit row under its own tenant context, so the row is inserted by the operator
        // outbox executor's SECURITY DEFINER audit function, in the same transaction as the assignment.
        var operatorOutboxLane = TestRepository.GetPath(
            "src/Modules/Dispatch/Dispatch.Infrastructure/Persistence/Migrations/20261003000100_AddOperatorOwnerOutboxExecutor.cs");
        Assert.Equal(
            new[]
            {
                TestRepository.GetPath("src/BuildingBlocks/Paqueteria.Infrastructure/Auditing/PostgreSqlAppendOnlyAuditWriter.cs"),
                registrationLane,
                pendingMembershipLane,
                masterDataLane,
                operatorOutboxLane,
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

        // MDM-001: exactly one insert, inside the SECURITY DEFINER loader function, never in the rollback.
        var masterData = sources.Single(source =>
            string.Equals(source.Path, masterDataLane, StringComparison.OrdinalIgnoreCase)).Source;
        var loader = Assert.Single(masterData.Split("CREATE OR REPLACE FUNCTION ", StringSplitOptions.None).Skip(1));
        Assert.StartsWith("security.load_master_data(", loader, StringComparison.Ordinal);
        var loaderDefinition = loader[..loader.IndexOf("$function$;", StringComparison.Ordinal)];
        Assert.Contains("SECURITY DEFINER", loaderDefinition, StringComparison.Ordinal);
        Assert.DoesNotContain("RETURNING", loaderDefinition, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, loaderDefinition.Split("INSERT INTO platform.audit_logs", StringSplitOptions.None).Length - 1);
        Assert.Equal(1, masterData.Split("INSERT INTO platform.audit_logs", StringSplitOptions.None).Length - 1);

        // DSP-OPERATOR-OWNER-OUTBOX: exactly one audit insert, inside the SECURITY DEFINER audit function, and
        // exactly one outbox insert, inside the SECURITY DEFINER outbox function; none in a DO block or rollback.
        var operatorOutbox = sources.Single(source =>
            string.Equals(source.Path, operatorOutboxLane, StringComparison.OrdinalIgnoreCase)).Source;
        var operatorFunctions = operatorOutbox.Split("CREATE OR REPLACE FUNCTION ", StringSplitOptions.None).Skip(1)
            .ToDictionary(body => body[..body.IndexOf('(', StringComparison.Ordinal)], StringComparer.Ordinal);
        Assert.Equal(
            ["security.append_operator_order_audit", "security.append_operator_order_outbox"],
            operatorFunctions.Keys.Order(StringComparer.Ordinal));
        foreach (var (name, body) in operatorFunctions)
        {
            var definition = body[..body.IndexOf("$function$;", StringComparison.Ordinal)];
            Assert.Contains("SECURITY DEFINER", definition, StringComparison.Ordinal);
            Assert.Contains("SET search_path = pg_catalog, pg_temp", definition, StringComparison.Ordinal);
            Assert.DoesNotContain("RETURNING", definition, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(
                name == "security.append_operator_order_audit" ? 1 : 0,
                definition.Split("INSERT INTO platform.audit_logs", StringSplitOptions.None).Length - 1);
            Assert.Equal(
                name == "security.append_operator_order_outbox" ? 1 : 0,
                definition.Split("INSERT INTO platform.outbox_events", StringSplitOptions.None).Length - 1);
        }

        Assert.Equal(1, operatorOutbox.Split("INSERT INTO platform.audit_logs", StringSplitOptions.None).Length - 1);
        Assert.Equal(1, operatorOutbox.Split("INSERT INTO platform.outbox_events", StringSplitOptions.None).Length - 1);
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
