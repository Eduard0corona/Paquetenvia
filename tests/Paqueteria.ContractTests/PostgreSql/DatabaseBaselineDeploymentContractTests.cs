using System.Text.Json.Nodes;
using Npgsql;
using Paqueteria.ContractTests.PostgreSql.Fixtures;
using Paqueteria.Infrastructure.Database.Baseline;

namespace Paqueteria.ContractTests.PostgreSql;

[Collection(PostgreSqlContractCollection.Name)]
[Trait("Category", "PostgreSqlContract")]
public sealed class DatabaseBaselineDeploymentContractTests(PostgreSqlContractFixture fixture)
{
    [PostgreSqlContractFact]
    public async Task Clean_database_applies_once_and_then_reports_already_applied()
    {
        var connectionString = await fixture.CreateIsolatedDatabaseAsync("idempotent");
        try
        {
            var baseline = await new DatabaseBaselineVerifier().VerifyAsync();
            var deployer = new DatabaseBaselineDeployer();

            var cleanPlan = await deployer.PlanAsync(baseline, connectionString);
            Assert.Equal(DatabaseBaselineStatus.Clean, cleanPlan.State.Status);
            Assert.Equal(["0001-canonical-schema", "0002-role-model"], cleanPlan.Steps.Select(step => step.Id));

            var first = await deployer.ApplyAsync(baseline, connectionString);
            var second = await deployer.ApplyAsync(baseline, connectionString);
            var finalPlan = await deployer.PlanAsync(baseline, connectionString);

            Assert.Equal(DatabaseBaselineApplyStatus.Applied, first.Status);
            Assert.Equal(DatabaseBaselineApplyStatus.AlreadyApplied, second.Status);
            Assert.Equal(DatabaseBaselineStatus.Applied, finalPlan.State.Status);
            Assert.True(first.Assertions.Checks >= 10);
            Assert.StartsWith("18.", first.Assertions.PostgreSqlVersion, StringComparison.Ordinal);
            Assert.StartsWith("3.6", first.Assertions.PostGisVersion, StringComparison.Ordinal);
        }
        finally
        {
            await fixture.DropIsolatedDatabaseAsync(connectionString);
        }
    }

    [PostgreSqlContractFact]
    public async Task Partial_database_fails_closed_without_completing_the_baseline()
    {
        var connectionString = await fixture.CreateIsolatedDatabaseAsync("partial");
        try
        {
            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand("CREATE SCHEMA identity", connection);
                await command.ExecuteNonQueryAsync();
            }

            var baseline = await new DatabaseBaselineVerifier().VerifyAsync();
            var deployer = new DatabaseBaselineDeployer();
            var exception = await Assert.ThrowsAsync<PartialDatabaseBaselineException>(
                () => deployer.ApplyAsync(baseline, connectionString));

            Assert.Equal(DatabaseBaselineStatus.Partial, exception.State.Status);
            Assert.Contains("schema:identity", exception.State.PresentCriticalObjects);

            await using var verificationConnection = new NpgsqlConnection(connectionString);
            await verificationConnection.OpenAsync();
            await using var verification = new NpgsqlCommand(
                "SELECT to_regclass('platform.outbox_events') IS NULL AND to_regnamespace('orders') IS NULL",
                verificationConnection);
            Assert.True((bool)(await verification.ExecuteScalarAsync())!);
        }
        finally
        {
            await fixture.DropIsolatedDatabaseAsync(connectionString);
        }
    }

    [PostgreSqlContractFact]
    public async Task Concurrent_deployers_serialize_and_leave_one_complete_baseline()
    {
        var connectionString = await fixture.CreateIsolatedDatabaseAsync("concurrent");
        try
        {
            var baseline = await new DatabaseBaselineVerifier().VerifyAsync();
            var first = new DatabaseBaselineDeployer().ApplyAsync(baseline, connectionString);
            var second = new DatabaseBaselineDeployer().ApplyAsync(baseline, connectionString);
            var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromMinutes(3));

            Assert.Equal(
                [DatabaseBaselineApplyStatus.Applied, DatabaseBaselineApplyStatus.AlreadyApplied],
                results.Select(result => result.Status).Order());

            var report = await new DatabaseBaselineDeployer().AssertAsync(baseline, connectionString);
            Assert.True(report.Checks >= 10);
        }
        finally
        {
            await fixture.DropIsolatedDatabaseAsync(connectionString);
        }
    }

    [Fact]
    public async Task Canonical_bytes_are_rejected_when_the_hash_does_not_match()
    {
        var temporaryRoot = await CreateTemporaryBaselineAsync();
        try
        {
            var schemaPath = Path.Combine(temporaryRoot, CanonicalBaselineContract.SchemaRelativePath.Replace('/', Path.DirectorySeparatorChar));
            await File.AppendAllTextAsync(schemaPath, Environment.NewLine + "-- unauthorized mutation");

            var exception = await Assert.ThrowsAsync<BaselineVerificationException>(
                () => new DatabaseBaselineVerifier().VerifyAsync(temporaryRoot));
            Assert.Contains("Canonical hash mismatch", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Reordered_steps_are_rejected_before_any_database_connection_is_needed()
    {
        var temporaryRoot = await CreateTemporaryBaselineAsync();
        try
        {
            var manifestPath = Path.Combine(temporaryRoot, CanonicalBaselineContract.ManifestRelativePath.Replace('/', Path.DirectorySeparatorChar));
            var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
            var steps = manifest["steps"]!.AsArray();
            var first = steps[0]!.DeepClone();
            var second = steps[1]!.DeepClone();
            steps[0] = second;
            steps[1] = first;
            await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString(new() { WriteIndented = true }));

            var exception = await Assert.ThrowsAsync<BaselineVerificationException>(
                () => new DatabaseBaselineVerifier().VerifyAsync(temporaryRoot));
            Assert.Contains("Baseline step 1 must be", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    public static TheoryData<string> AssertionMutations => new()
    {
        { "ALTER TABLE platform.outbox_events OWNER TO postgres" },
        { "GRANT CREATE ON SCHEMA public TO PUBLIC" },
        { "GRANT SELECT ON platform.outbox_events TO paqueteria_app" },
        { "GRANT UPDATE ON platform.outbox_events TO paqueteria_maintenance" },
        { "GRANT EXECUTE ON FUNCTION security.resolve_identity_context(text) TO PUBLIC" },
        { "ALTER TABLE orders.order_events DISABLE TRIGGER order_events_append_only" },
    };

    [Theory]
    [MemberData(nameof(AssertionMutations))]
    public async Task Assertions_detect_forbidden_catalog_mutations(string mutation)
    {
        await using var connection = new NpgsqlConnection(fixture.DeploymentConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand(mutation, connection, transaction))
        {
            await command.ExecuteNonQueryAsync();
        }

        var exception = await Assert.ThrowsAsync<DatabaseAssertionException>(
            () => new DatabaseBaselineAssertions().AssertAsync(connection, transaction));
        Assert.NotEmpty(exception.Violations);
        await transaction.RollbackAsync();
    }

    private static async Task<string> CreateTemporaryBaselineAsync()
    {
        var sourceRoot = RepositoryRootLocator.Find();
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"paqueteria-dba001-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryRoot);
        foreach (var relativePath in new[]
        {
            CanonicalBaselineContract.SchemaRelativePath,
            CanonicalBaselineContract.RolesRelativePath,
            CanonicalBaselineContract.ManifestRelativePath,
        })
        {
            var source = Path.Combine(sourceRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            var target = Path.Combine(temporaryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var sourceStream = File.OpenRead(source);
            await using var targetStream = File.Create(target);
            await sourceStream.CopyToAsync(targetStream);
        }

        return temporaryRoot;
    }
}
