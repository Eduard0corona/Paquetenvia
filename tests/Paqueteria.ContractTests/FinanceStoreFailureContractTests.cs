using Finance.Application;
using Finance.Application.Cod;
using Finance.Infrastructure;
using Finance.Infrastructure.Cod;
using Finance.Infrastructure.Financials;
using Finance.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Npgsql;
using Paqueteria.Application;
using Paqueteria.Application.Auditing;
using Paqueteria.Infrastructure.Auditing;
using Paqueteria.Infrastructure.Tenancy;

namespace Paqueteria.ContractTests;

/// <summary>
/// An unreachable PostgreSQL store is unavailability, never an authorization or conflict outcome. The
/// retrying execution strategy production uses reports it as <see cref="RetryLimitExceededException"/> once
/// it gives up, and the non-retrying one as an <see cref="InvalidOperationException"/>; both wrap the
/// <see cref="NpgsqlException"/> that actually occurred and both must surface as
/// <see cref="FinanceUnavailableException"/>.
/// </summary>
public sealed class FinanceStoreFailureContractTests
{
    // Nothing listens on TCP port 1 of the loopback interface, so every connection attempt fails at once.
    private const string UnreachableStore =
        "Host=127.0.0.1;Port=1;Database=paqueteria;Username=paqueteria_app;Password=unused;Timeout=2;Pooling=false";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Fin001_an_unreachable_store_is_unavailable_for_every_finance_operation(bool retrying)
    {
        await using var dataSource = NpgsqlDataSource.Create(UnreachableStore);
        var (gateway, state) = CreateGateway(dataSource, retrying);
        var cod = new PostgreSqlCodTransactionService(
            gateway,
            new PostgreSqlAppendOnlyAuditWriter(state),
            new AuditPayloadRedactor(),
            new NoOpFinanceFailureInjector(),
            new FixedClock(new(2026, 9, 22, 17, 0, 0, TimeSpan.Zero)));
        var financials = new PostgreSqlOrderFinancialsService(gateway);
        var actor = Guid.NewGuid();
        var organization = Guid.NewGuid();
        var resource = Guid.NewGuid();

        FinanceUnavailableException[] failures =
        [
            await Assert.ThrowsAsync<FinanceUnavailableException>(() => cod.RecordAsync(
                new(actor, organization, "fin001-store-failure-record", resource, 5_000, "cash-01", false, null),
                default)),
            await Assert.ThrowsAsync<FinanceUnavailableException>(() => cod.ReconcileAsync(
                new(actor, organization, "fin001-store-failure-reconcile", resource, false, null),
                default)),
            await Assert.ThrowsAsync<FinanceUnavailableException>(() => financials.GetOrderFinancialsAsync(
                new(actor, organization, resource, false),
                default)),
            await Assert.ThrowsAsync<FinanceUnavailableException>(() => financials.GetRouteFinancialsAsync(
                new(actor, organization, resource, false),
                default)),
        ];

        Assert.All(failures, failure =>
        {
            Assert.IsType(
                retrying ? typeof(RetryLimitExceededException) : typeof(InvalidOperationException),
                failure.InnerException);
            Assert.IsAssignableFrom<NpgsqlException>(failure.InnerException!.InnerException);
        });
    }

    private static (FinanceTenantGateway Gateway, TenantDatabaseExecutionState State) CreateGateway(
        NpgsqlDataSource dataSource,
        bool retrying)
    {
        var state = new TenantDatabaseExecutionState();
        var options = new DbContextOptionsBuilder<FinanceDbContext>()
            .UseNpgsql(dataSource, postgres =>
            {
                // Zero retries is the production strategy reaching its limit without the backoff delays.
                if (retrying)
                {
                    postgres.EnableRetryOnFailure(0);
                }
            })
            .AddInterceptors(
                new TenantTransactionGuardInterceptor(state),
                new TenantSaveChangesGuardInterceptor(state))
            .Options;
        return (
            new FinanceTenantGateway(
                new TenantTransactionContext<FinanceDbContext>(new FinanceDbContext(options, state), state),
                Options.Create(new FinanceOptions { Provider = FinanceProviderKind.PostgreSql })),
            state);
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }
}
