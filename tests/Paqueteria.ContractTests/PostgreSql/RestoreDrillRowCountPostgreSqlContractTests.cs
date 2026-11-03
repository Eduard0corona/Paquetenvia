using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Npgsql;
using Paqueteria.ContractTests.PostgreSql.Fixtures;
using Paqueteria.Infrastructure.Database.Baseline;

namespace Paqueteria.ContractTests.PostgreSql;

/// <summary>
/// REL-001 restore vigente (Z9, Z18 "Exacto, en el job (Recommended)") against real PostgreSQL/PostGIS, on two fully
/// migrated databases standing in for the server in service and its restored copy. <c>assert --compare-row-counts-env</c>
/// passes only when organizations, orders, order events and pickup and delivery photos have the same counts on both,
/// read as an Azure-like deployment login (NOSUPERUSER, BYPASSRLS). It fails on any difference, on the same database
/// named twice and on a login that row-level security would limit, and it writes nothing. Its output carries the four
/// counts only, never an identifier, a name, a host or a login.
/// </summary>
[Collection(PostgreSqlContractCollection.Name)]
[Trait("Category", "PostgreSqlContract")]
public sealed partial class RestoreDrillRowCountPostgreSqlContractTests(PostgreSqlContractFixture fixture)
{
    private static readonly string Password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

    [GeneratedRegex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.CultureInvariant)]
    private static partial Regex UuidPattern();

    [PostgreSqlContractFact]
    public async Task Assert_compares_the_four_counts_of_the_server_in_service_and_its_restored_copy()
    {
        var source = await fixture.CreateIsolatedDatabaseAsync("rel001src");
        var restored = await fixture.CreateIsolatedDatabaseAsync("rel001rst");
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var deployLogin = $"pv_rel001_deploy_{suffix}";
        var limitedLogin = $"pv_rel001_limited_{suffix}";
        var sourceVariable = $"PAQUETERIA_REL001_SOURCE_{suffix.ToUpperInvariant()}";
        var restoredVariable = $"PAQUETERIA_REL001_RESTORED_{suffix.ToUpperInvariant()}";
        try
        {
            await MigrateAsync(source);
            await MigrateAsync(restored);
            await ExecuteAsync(source, $"""
                CREATE ROLE {deployLogin} LOGIN NOSUPERUSER NOCREATEDB CREATEROLE NOREPLICATION BYPASSRLS PASSWORD '{Password}';
                GRANT paqueteria_migrator TO {deployLogin} WITH INHERIT TRUE, SET TRUE;
                CREATE ROLE {limitedLogin} LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD '{Password}';
                GRANT paqueteria_migrator TO {limitedLogin} WITH INHERIT TRUE, SET TRUE;
                """);
            // The OPS-002 synthetic seed on both: 2 organizations, 2 orders, 9 order events, a pickup and a delivery photo.
            await ExecuteAsync(source, SeedSql());
            await ExecuteAsync(restored, SeedSql());
            var deploySource = Login(source, deployLogin);
            var deployRestored = Login(restored, deployLogin);
            var before = await FingerprintAsync(source) + await FingerprintAsync(restored);

            // Equal counts: the whole assert passes and prints the four counts only. assert runs as the fixture's
            // administrator, as the verify job runs it as the server administrator.
            var (matchedExit, matchedOutput) = await RunAssertAsync(restoredVariable, restored, sourceVariable, source);
            Assert.True(matchedExit == 0, matchedOutput);
            Assert.Contains("ROW_COUNTS_MATCHED organizations=2 orders=2 order_events=9 proof_photos=2", matchedOutput,
                StringComparison.Ordinal);
            AssertNothingIdentifying(matchedOutput, deployLogin);
            // Read only: not a row of the four tables changed on either side.
            Assert.Equal(before, await FingerprintAsync(source) + await FingerprintAsync(restored));

            // An Azure-like deployment login (NOSUPERUSER, BYPASSRLS) sees every row too.
            Assert.Equal([2L, 2, 9, 2], await RowCountComparison.CompareAsync(deploySource, deployRestored, TextWriter.Null, default));

            // A SIGNATURE proof is not a photo: still equal.
            await ExecuteAsync(source, SignatureProofSql);
            Assert.Equal([2L, 2, 9, 2], await RowCountComparison.CompareAsync(deploySource, deployRestored, TextWriter.Null, default));

            // One more order event on the server in service: assert fails with exit 13 and both sides' counts.
            await ExecuteAsync(source, ExtraEventSql);
            var (differExit, differOutput) = await RunAssertAsync(restoredVariable, restored, sourceVariable, source);
            Assert.True(differExit == RowCountComparison.MismatchExitCode, differOutput);
            Assert.Contains(
                "ROW_COUNTS_DIFFER source organizations=2 orders=2 order_events=10 proof_photos=2 " +
                "restored organizations=2 orders=2 order_events=9 proof_photos=2",
                differOutput, StringComparison.Ordinal);
            AssertNothingIdentifying(differOutput, deployLogin);

            // The same database twice, or a login that row-level security limits, never compares.
            var same = await Assert.ThrowsAsync<RowCountComparisonException>(() =>
                RowCountComparison.CompareAsync(deployRestored, deployRestored, TextWriter.Null, default));
            Assert.StartsWith("ROW_COUNTS_SAME_DATABASE", same.Message, StringComparison.Ordinal);
            var limited = await Assert.ThrowsAsync<RowCountComparisonException>(() =>
                RowCountComparison.CompareAsync(Login(source, limitedLogin), deployRestored, TextWriter.Null, default));
            Assert.StartsWith("ROW_COUNTS_LOGIN_CANNOT_SEE_ALL_ROWS", limited.Message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(sourceVariable, null);
            Environment.SetEnvironmentVariable(restoredVariable, null);
            NpgsqlConnection.ClearAllPools();
            await fixture.DropIsolatedDatabaseAsync(source);
            await fixture.DropIsolatedDatabaseAsync(restored);
            await using var cleanup = fixture.AdminDataSource.CreateCommand(
                $"DROP ROLE IF EXISTS {deployLogin}; DROP ROLE IF EXISTS {limitedLogin};");
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    /// <summary>The comparison's own lines hold names and counts only; the whole output names no id, login or secret.</summary>
    private static void AssertNothingIdentifying(string output, string login)
    {
        var lines = output.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.StartsWith("ROW_COUNTS", StringComparison.Ordinal))
            .ToArray();
        Assert.NotEmpty(lines);
        Assert.All(lines, line => Assert.Matches(CountsLinePattern(), line));
        Assert.DoesNotMatch(UuidPattern(), output);
        Assert.DoesNotContain(login, output, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, output, StringComparison.Ordinal);
        Assert.DoesNotContain("SINTETICO", output, StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(
        "^ROW_COUNTS_(MATCHED|DIFFER( source)?)( (organizations|orders|order_events|proof_photos)=[0-9]+){4}( restored( (organizations|orders|order_events|proof_photos)=[0-9]+){4})?$|^ROW_COUNTS_[A-Z_]+: [^=]*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex CountsLinePattern();

    /// <summary>The whole <c>assert</c> command in process, with its output captured.</summary>
    private static async Task<(int ExitCode, string Output)> RunAssertAsync(
        string restoredVariable, string restored, string sourceVariable, string source)
    {
        Environment.SetEnvironmentVariable(restoredVariable, restored);
        Environment.SetEnvironmentVariable(sourceVariable, source);
        var stdout = Console.Out;
        var stderr = Console.Error;
        await using var capture = new StringWriter();
        Console.SetOut(capture);
        Console.SetError(capture);
        try
        {
            var exitCode = await DatabaseMigratorProgram.RunAsync(
                ["assert", "--connection-env", restoredVariable, "--compare-row-counts-env", sourceVariable]);
            return (exitCode, capture.ToString());
        }
        finally
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
        }
    }

    private static async Task MigrateAsync(string database)
    {
        var baseline = await new DatabaseBaselineVerifier().VerifyAsync();
        await new DatabaseBaselineDeployer().ApplyAsync(baseline, database);
        await new ModuleMigrationCoordinator().ApplyAsync(database, CancellationToken.None);
    }

    /// <summary>The OPS-002 seed (tests/fixtures/ops-002/seed.sql) with synthetic object keys and digests.</summary>
    private static string SeedSql()
    {
        var text = File.ReadAllText(Path.Combine(
            RepositoryRootLocator.Find(AppContext.BaseDirectory), "tests", "fixtures", "ops-002", "seed.sql"));
        var replacements = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["__DRIVER_DOCUMENT_KEY__"] = "synthetic/rel001-driver-document",
            ["__PICKUP_QUARANTINE_KEY__"] = "quarantine/rel001/pickup",
            ["__DELIVERY_QUARANTINE_KEY__"] = "quarantine/rel001/delivery",
            ["__PICKUP_KEY__"] = "proofs/rel001/pickup",
            ["__DELIVERY_KEY__"] = "proofs/rel001/delivery",
            ["__PICKUP_SHA__"] = new string('a', 64),
            ["__DELIVERY_SHA__"] = new string('b', 64),
            ["__PICKUP_BYTES__"] = "68",
            ["__DELIVERY_BYTES__"] = "68",
        };
        foreach (var (placeholder, value) in replacements)
        {
            text = text.Replace(placeholder, value, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("__", text.Replace("__ef_", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        // psql meta-commands (\set ON_ERROR_STOP) are not SQL.
        return string.Join('\n', text.Split('\n').Where(line => !line.TrimStart().StartsWith('\\')));
    }

    private const string SignatureProofSql =
        """
        INSERT INTO custody.proof_upload_sessions(
          id,order_id,owner_org_id,requested_by,object_key_quarantine,expected_content_type,
          maximum_bytes,status,expires_at,created_at,updated_at)
        VALUES ('7e100000-0000-4000-8000-000000000301','10000000-0000-4000-8000-000000000010',
          '10000000-0000-4000-8000-000000000001','10000000-0000-4000-8000-000000000002','quarantine/rel001/signature',
          'image/png',1048576,'CONSUMED','2026-01-02T00:00:00Z','2026-01-01T00:08:00Z','2026-01-01T00:08:30Z');
        INSERT INTO custody.proofs(
          id,order_id,owner_org_id,upload_session_id,proof_type,object_key,sha256,
          content_type,size_bytes,captured_at,created_by,created_at)
        VALUES ('7e100000-0000-4000-8000-000000000302','10000000-0000-4000-8000-000000000010',
          '10000000-0000-4000-8000-000000000001','7e100000-0000-4000-8000-000000000301','SIGNATURE',
          'proofs/rel001/signature',decode(repeat('c',64),'hex'),'image/png',68,'2026-01-01T00:08:30Z',
          '10000000-0000-4000-8000-000000000002','2026-01-01T00:08:30Z');
        """;

    private const string ExtraEventSql =
        """
        INSERT INTO orders.order_events(id,order_id,owner_org_id,aggregate_version,event_type,payload,occurred_at)
        VALUES ('7e100000-0000-4000-8000-000000000401','20000000-0000-4000-8000-000000000010',
          '20000000-0000-4000-8000-000000000001',1,'ORDER_CREATED','{}','2026-01-01T00:10:00Z');
        """;

    /// <summary>Every row of the four counted tables, as text, so a test can prove nothing was written.</summary>
    private static Task<string> FingerprintAsync(string database) => ScalarAsync<string>(database,
        """
        SELECT md5(concat_ws('|',
          (SELECT string_agg(o::text, ',' ORDER BY id) FROM organizations.organizations o),
          (SELECT string_agg(o::text, ',' ORDER BY id) FROM orders.orders o),
          (SELECT string_agg(e::text, ',' ORDER BY id) FROM orders.order_events e),
          (SELECT string_agg(p::text, ',' ORDER BY id) FROM custody.proofs p)))
        """);

    private static string Login(string database, string login) =>
        new NpgsqlConnectionStringBuilder(database) { Username = login, Password = Password, Pooling = false }.ConnectionString;

    private static async Task ExecuteAsync(string database, string sql)
    {
        await using var connection = new NpgsqlConnection(database);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(string database, string sql)
    {
        await using var connection = new NpgsqlConnection(database);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }
}
