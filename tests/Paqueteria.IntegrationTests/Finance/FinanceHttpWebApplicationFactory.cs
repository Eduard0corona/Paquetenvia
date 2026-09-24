using Finance.Application;
using Finance.Application.Cod;
using Finance.Application.Financials;
using Finance.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Paqueteria.IntegrationTests.Finance;

/// <summary>
/// Hosts the real API with the Finance services replaced by a stub whose outcome is chosen by the
/// resource identifier, so every failure class the application layer can raise reaches the real endpoints.
/// </summary>
public sealed class FinanceHttpWebApplicationFactory : WebApplicationFactory<Program>
{
    internal static readonly Guid Succeeds = Guid.Parse("f1000000-0000-0000-0000-000000000001");
    internal static readonly Guid Forbidden = Guid.Parse("f1000000-0000-0000-0000-000000000002");
    internal static readonly Guid Missing = Guid.Parse("f1000000-0000-0000-0000-000000000003");
    internal static readonly Guid AmountMismatch = Guid.Parse("f1000000-0000-0000-0000-000000000004");
    internal static readonly Guid ConcurrencyConflict = Guid.Parse("f1000000-0000-0000-0000-000000000005");
    internal static readonly Guid Unavailable = Guid.Parse("f1000000-0000-0000-0000-000000000006");

    /// <summary>Store evidence an unavailable provider might carry; none of it may reach a response.</summary>
    internal const string InternalDetail =
        "Host=10.20.30.40;Password=fin001-secret relation finance.cod_transactions does not exist";

    private static readonly DateTimeOffset RecordedAt = new(2026, 9, 22, 17, 0, 0, TimeSpan.Zero);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration(configuration =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Provider"] = "Mock",
                ["IdentityBootstrap:Provider"] = "Mock",
            }));
        builder.ConfigureTestServices(services =>
        {
            var stub = new StubFinance();
            services.RemoveAll<ICodTransactionService>();
            services.RemoveAll<IOrderFinancialsService>();
            services.AddSingleton<ICodTransactionService>(stub);
            services.AddSingleton<IOrderFinancialsService>(stub);
        });
    }

    /// <summary>
    /// An order expecting 5000 cents of COD that has not been collected yet, so its COD position has no
    /// status and no amount.
    /// </summary>
    private static OrderUnitEconomics UncollectedCodOrder(Guid orderId) => OrderUnitEconomics.Calculate(
        orderId,
        new(12_000),
        [new(DeliveryModality.Own, new(4_500), 1)],
        new(new(5_000), null, null));

    private sealed class StubFinance : ICodTransactionService, IOrderFinancialsService
    {
        public Task<CodTransactionResult> RecordAsync(
            RecordCodCollectionCommand command,
            CancellationToken cancellationToken) =>
            Outcome(command.OrderId, () => new CodTransactionResult(
                Guid.NewGuid(), command.OrderId, command.AmountCents, "RECORDED", RecordedAt, null));

        public Task<CodTransactionResult> ReconcileAsync(
            ReconcileCodCommand command,
            CancellationToken cancellationToken) =>
            Outcome(command.CodTransactionId, () => new CodTransactionResult(
                command.CodTransactionId, Guid.NewGuid(), 5_000, "RECONCILED", RecordedAt, RecordedAt.AddHours(1)));

        public Task<OrderFinancialsResult> GetOrderFinancialsAsync(
            GetOrderFinancialsQuery query,
            CancellationToken cancellationToken) =>
            Outcome(query.OrderId, () => OrderFinancialsResult.From("DELIVERING", UncollectedCodOrder(query.OrderId)));

        public Task<RouteFinancialsResult> GetRouteFinancialsAsync(
            GetRouteFinancialsQuery query,
            CancellationToken cancellationToken) =>
            Outcome(query.RouteId, () => RouteFinancialsResult.From(
                query.RouteId,
                "ACTIVE",
                RouteUnitEconomics.Aggregate([UncollectedCodOrder(Guid.NewGuid())])));

        private static Task<T> Outcome<T>(Guid id, Func<T> success)
        {
            Exception? failure =
                id == Forbidden ? new FinanceForbiddenException() :
                id == Missing ? new FinanceNotFoundException() :
                id == AmountMismatch ? new FinanceConflictException(FinanceConflictCode.CodAmountMismatch) :
                id == ConcurrencyConflict ? new FinanceConflictException(FinanceConflictCode.ConcurrencyConflict) :
                id == Unavailable ? new FinanceUnavailableException(
                    InternalDetail,
                    new InvalidOperationException(InternalDetail)) :
                null;
            return failure is null ? Task.FromResult(success()) : Task.FromException<T>(failure);
        }
    }
}

/// <summary>The real API and real Finance registrations with the provider left at its Disabled default.</summary>
internal sealed class DisabledFinanceWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration(configuration =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Provider"] = "Mock",
                ["IdentityBootstrap:Provider"] = "Mock",
                ["Finance:Provider"] = "Disabled",
            }));
    }
}
