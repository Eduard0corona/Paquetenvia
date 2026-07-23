using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pricing.Application.Quotes;

namespace Paqueteria.IntegrationTests.Pricing;

public sealed class QuoteHttpWebApplicationFactory : WebApplicationFactory<Program>
{
    internal static readonly Guid ActiveQuoteId = Guid.Parse("70000000-0000-0000-0000-000000000001");
    internal static readonly Guid UsedQuoteId = Guid.Parse("70000000-0000-0000-0000-000000000002");
    internal static readonly Guid MissingQuoteId = Guid.Parse("70000000-0000-0000-0000-000000000003");
    internal static readonly Guid ExpiredQuoteId = Guid.Parse("70000000-0000-0000-0000-000000000004");
    internal static readonly Guid RevokedQuoteId = Guid.Parse("70000000-0000-0000-0000-000000000005");

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
            services.RemoveAll<IQuoteService>();
            services.AddSingleton<IQuoteService, StubQuoteService>();
        });
    }

    private sealed class StubQuoteService : IQuoteService
    {
        private readonly ConcurrentDictionary<string, (string Signature, QuoteResult Result)> responses = new(StringComparer.Ordinal);

        public Task<QuoteResult> CreateAsync(CreateQuoteCommand command, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var signature = string.Join('|',
                command.ClientAccountId,
                command.Origin.AddressText,
                command.Origin.Lat,
                command.Origin.Lng,
                command.Destination.AddressText,
                command.Destination.Lat,
                command.Destination.Lng,
                command.ServiceType,
                command.ConsolidatedRoute,
                command.Packages.Count,
                string.Join(',', command.Packages.Select(package => package.Description)),
                command.Packages.Sum(package => package.DeclaredValueCents));
            var result = Result(Guid.NewGuid(), "ACTIVE");
            var existing = responses.GetOrAdd(command.IdempotencyKey, (signature, result));
            if (!string.Equals(existing.Signature, signature, StringComparison.Ordinal))
            {
                throw new QuoteValidationException(QuoteValidationCode.IdempotencyConflict);
            }

            var marker = command.Origin.AddressText;
            if (marker.Contains("OUTSIDE", StringComparison.Ordinal) ||
                marker.Contains("EXCLUDED", StringComparison.Ordinal) ||
                marker.Contains("NO_RULE", StringComparison.Ordinal) ||
                marker.Contains("AMBIGUOUS", StringComparison.Ordinal) ||
                marker.Contains("TAX_BLOCKED", StringComparison.Ordinal))
            {
                throw new QuoteValidationException(QuoteValidationCode.NoTariffRule);
            }

            return Task.FromResult(existing.Result);
        }

        public Task<QuoteResult> GetAsync(Guid actorId, Guid organizationId, Guid quoteId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (quoteId == ActiveQuoteId) return Task.FromResult(Result(quoteId, "ACTIVE"));
            if (quoteId == UsedQuoteId) return Task.FromResult(Result(quoteId, "USED"));
            throw new QuoteNotFoundException();
        }

        private static QuoteResult Result(Guid id, string status) => new(
            id,
            new MoneyResult("MXN", 12_345),
            new MoneyResult("MXN", 0),
            new MoneyResult("MXN", 12_345),
            [Guid.Parse("71000000-0000-0000-0000-000000000001")],
            [new QuoteBreakdownLine("BASE_TARIFF", Guid.Parse("71000000-0000-0000-0000-000000000001"), 12_345, "OCCASIONAL", "EXEMPT")],
            new DateTimeOffset(2026, 7, 22, 13, 0, 0, TimeSpan.Zero),
            Guid.Parse("72000000-0000-0000-0000-000000000001"),
            Guid.Parse("72000000-0000-0000-0000-000000000002"),
            "SAME_DAY",
            false,
            [new QuotePackageInput("[REDACTED]", 1000, 5000, 100, 100, 100)],
            Guid.Parse("73000000-0000-0000-0000-000000000001"),
            Guid.Parse("74000000-0000-0000-0000-000000000001"),
            "OCCASIONAL",
            12_345,
            "PRC-001-v1",
            status,
            new Dictionary<string, object?>
            {
                ["package_count"] = 1,
                ["service_type"] = "SAME_DAY",
            });
    }
}
