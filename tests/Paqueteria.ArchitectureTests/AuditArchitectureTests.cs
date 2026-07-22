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

        var source = Assert.Single(sources);
        Assert.Equal(
            TestRepository.GetPath("src/BuildingBlocks/Paqueteria.Infrastructure/Auditing/PostgreSqlAppendOnlyAuditWriter.cs"),
            source.Path,
            ignoreCase: true);
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
    public void Productive_worker_remains_disconnected_from_PostgreSql_and_auditing()
    {
        var metadata = ProjectMetadataReader.Read(SolutionCatalog.Worker);
        Assert.DoesNotContain(metadata.ProjectReferencePaths, reference =>
            reference.Contains("Organizations", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(metadata.PackageReferences, package =>
            package.Contains("Npgsql", StringComparison.OrdinalIgnoreCase));

        var source = File.ReadAllText(TestRepository.GetPath("src/Paqueteria.Worker/Program.cs"));
        Assert.DoesNotContain("Npgsql", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Audit", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ConnectionString", source, StringComparison.OrdinalIgnoreCase);
    }
}
