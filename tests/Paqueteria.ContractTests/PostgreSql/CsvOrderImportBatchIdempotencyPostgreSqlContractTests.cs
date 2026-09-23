using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Orders.Application.Csv;
using Orders.Infrastructure;
using Orders.Infrastructure.Csv;
using Orders.Infrastructure.Persistence;
using Paqueteria.ContractTests.PostgreSql.Fixtures;
using Paqueteria.Infrastructure;
using Paqueteria.Infrastructure.Tenancy;

namespace Paqueteria.ContractTests.PostgreSql;

/// <summary>
/// CSV-001 batch idempotency as it actually behaves on PostgreSQL: one
/// <c>platform.idempotency_keys</c> record per tenant and batch key, bound to the canonical batch,
/// replayed on reuse, refused when the key is reused for other content, and serialized by the same
/// advisory transaction lock ORD-001 takes out for a single order.
/// </summary>
[Collection(PostgreSqlContractCollection.Name)]
public sealed class CsvOrderImportBatchIdempotencyPostgreSqlContractTests(PostgreSqlContractFixture fixture)
{
    private const string BatchKey = "csv-batch-contract-0001";

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task A_batch_key_reserves_completes_replays_and_refuses_other_content()
    {
        await using var tenant = new SyntheticOrderScenario(fixture);
        await tenant.InitializeAsync(createOrder: false);
        var identity = Identity(tenant, "digest-batch-a");
        await using var scope = CreateScope();

        Assert.Null(await scope.Store.ReserveOrReplayAsync(identity, CancellationToken.None));

        var committed = Batch(identity.ContentDigest, "ORD_AAAAAAAAAAAAAAAAAAAAAA");
        Assert.Equal(
            Fingerprint(committed),
            Fingerprint(await scope.Store.CompleteAsync(identity, committed, CancellationToken.None)));
        var replay = await scope.Store.ReserveOrReplayAsync(identity, CancellationToken.None);
        Assert.NotNull(replay);
        Assert.Equal(Fingerprint(committed), Fingerprint(replay));

        await Assert.ThrowsAsync<CsvOrderImportBatchConflictException>(() =>
            scope.Store.ReserveOrReplayAsync(
                identity with { ContentDigest = "digest-batch-b" },
                CancellationToken.None));

        await using var reader = fixture.AdminDataSource.CreateCommand(
            """
            SELECT count(*),min(scope),min(response_status),min(resource_id)
            FROM platform.idempotency_keys
            WHERE owner_org_id=@owner AND idempotency_key=@key
            """);
        reader.Parameters.AddWithValue("owner", tenant.OrganizationId);
        reader.Parameters.AddWithValue("key", BatchKey);
        await using var row = await reader.ExecuteReaderAsync();
        Assert.True(await row.ReadAsync());
        Assert.Equal(1L, row.GetInt64(0));
        Assert.Equal(CsvOrderImportIdempotency.BatchScope, row.GetString(1));
        Assert.Equal(CsvOrderImportIdempotency.BatchResponseStatus, row.GetInt32(2));
        Assert.Equal(CsvOrderImportIdempotency.DeriveBatchId(identity), row.GetGuid(3));
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task Concurrent_commits_of_one_batch_key_agree_on_a_single_stored_batch()
    {
        await using var tenant = new SyntheticOrderScenario(fixture);
        await tenant.InitializeAsync(createOrder: false);
        var identity = Identity(tenant, "digest-batch-concurrent");

        // Each task is a full commit attempt on its own connection: reserve, produce its own
        // distinguishable view of the batch, then complete. Only one view may survive as the batch,
        // and every caller must be answered with that one.
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(async index =>
        {
            await using var scope = CreateScope();
            var replay = await scope.Store.ReserveOrReplayAsync(identity, CancellationToken.None);
            return Fingerprint(replay ?? await scope.Store.CompleteAsync(
                identity,
                Batch(identity.ContentDigest, $"ORD_CONCURRENT{index}AAAAAAAAA"),
                CancellationToken.None));
        }));

        Assert.Equal(6, results.Length);
        Assert.Single(results.Distinct(StringComparer.Ordinal));

        await using var reader = fixture.AdminDataSource.CreateCommand(
            """
            SELECT count(*),count(*) FILTER (WHERE response_status IS NOT NULL)
            FROM platform.idempotency_keys
            WHERE owner_org_id=@owner AND scope=@scope AND idempotency_key=@key
            """);
        reader.Parameters.AddWithValue("owner", tenant.OrganizationId);
        reader.Parameters.AddWithValue("scope", CsvOrderImportIdempotency.BatchScope);
        reader.Parameters.AddWithValue("key", BatchKey);
        await using var row = await reader.ExecuteReaderAsync();
        Assert.True(await row.ReadAsync());
        Assert.Equal(1L, row.GetInt64(0));
        Assert.Equal(1L, row.GetInt64(1));
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task One_batch_key_in_two_tenants_is_two_records_and_never_replays_across_them()
    {
        await using var owner = new SyntheticOrderScenario(fixture);
        await using var other = new SyntheticOrderScenario(fixture);
        await owner.InitializeAsync(createOrder: false);
        await other.InitializeAsync(createOrder: false);
        var ownerIdentity = Identity(owner, "digest-owner");
        var otherIdentity = Identity(other, "digest-other");

        await using (var scope = CreateScope())
        {
            Assert.Null(await scope.Store.ReserveOrReplayAsync(ownerIdentity, CancellationToken.None));
            await scope.Store.CompleteAsync(
                ownerIdentity,
                Batch("digest-owner", "ORD_OWNERAAAAAAAAAAAAAAAA"),
                CancellationToken.None);
        }

        await using (var scope = CreateScope())
        {
            // The same key with different content is a conflict inside one tenant, so reserving it
            // cleanly here proves the first tenant's record is invisible rather than merely unused.
            Assert.Null(await scope.Store.ReserveOrReplayAsync(otherIdentity, CancellationToken.None));
            var committed = Batch("digest-other", "ORD_OTHERAAAAAAAAAAAAAAAA");
            Assert.Equal(
                Fingerprint(committed),
                Fingerprint(await scope.Store.CompleteAsync(otherIdentity, committed, CancellationToken.None)));
        }

        await using var reader = fixture.AdminDataSource.CreateCommand(
            """
            SELECT owner_org_id FROM platform.idempotency_keys
            WHERE scope=@scope AND idempotency_key=@key ORDER BY owner_org_id
            """);
        reader.Parameters.AddWithValue("scope", CsvOrderImportIdempotency.BatchScope);
        reader.Parameters.AddWithValue("key", BatchKey);
        await using var row = await reader.ExecuteReaderAsync();
        var owners = new List<Guid>();
        while (await row.ReadAsync())
        {
            owners.Add(row.GetGuid(0));
        }

        Assert.Equal(
            new[] { owner.OrganizationId, other.OrganizationId }.Order().ToArray(),
            owners.ToArray());
    }

    private static CsvOrderImportBatchIdentity Identity(SyntheticOrderScenario tenant, string digest) => new(
        tenant.UserId,
        tenant.OrganizationId,
        BatchKey,
        digest,
        2);

    private static CsvOrderImportCommitResult Batch(string digest, string publicId) => new(
        digest,
        [
            new CsvOrderImportRowOutcome(
                2,
                "81000000-0000-0000-0000-00000000000a",
                "CREATED",
                Guid.Parse("82000000-0000-0000-0000-00000000000a"),
                publicId,
                null),
            new CsvOrderImportRowOutcome(
                3,
                "81000000-0000-0000-0000-00000000000b",
                "FAILED",
                null,
                null,
                "QUOTE_UNAVAILABLE"),
        ]);

    /// <summary>
    /// A structural rendering of a batch. The stored response comes back as a fresh object graph,
    /// so replays have to be compared by content rather than by reference.
    /// </summary>
    private static string Fingerprint(CsvOrderImportCommitResult result) => string.Join(
        '|',
        result.ContentDigest,
        result.TotalRows,
        result.CreatedRows,
        result.FailedRows,
        string.Join(
            ';',
            result.Rows.Select(row => string.Join(
                ',',
                row.RowNumber,
                row.QuoteId,
                row.Status,
                row.OrderId,
                row.PublicId,
                row.ErrorCode))));

    private RuntimeScope CreateScope()
    {
        var state = new TenantDatabaseExecutionState();
        var options = new DbContextOptionsBuilder<OrdersDbContext>()
            .UseNpgsql(fixture.AppDataSource, postgres => postgres.EnableRetryOnFailure())
            .AddInterceptors(
                new TenantTransactionGuardInterceptor(state),
                new TenantSaveChangesGuardInterceptor(state))
            .Options;
        var context = new OrdersDbContext(options, state);
        return new RuntimeScope(
            context,
            new PostgreSqlCsvOrderImportBatchIdempotencyStore(
                new TenantTransactionContext<OrdersDbContext>(context, state),
                Options.Create(new OrdersOptions
                {
                    Provider = OrdersProviderKind.PostgreSql,
                    CommandTimeoutSeconds = 30,
                    IdempotencyLifetimeMinutes = 60,
                }),
                new SystemClock()));
    }

    private sealed class RuntimeScope(
        OrdersDbContext context,
        ICsvOrderImportBatchIdempotencyStore store) : IAsyncDisposable
    {
        internal ICsvOrderImportBatchIdempotencyStore Store { get; } = store;

        public ValueTask DisposeAsync() => context.DisposeAsync();
    }
}
