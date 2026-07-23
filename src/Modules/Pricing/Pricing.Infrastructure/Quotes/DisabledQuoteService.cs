using Pricing.Application.Quotes;

namespace Pricing.Infrastructure.Quotes;

public sealed class DisabledQuoteService : IQuoteService
{
    public Task<QuoteResult> CreateAsync(CreateQuoteCommand command, CancellationToken cancellationToken) => Fail();
    public Task<QuoteResult> GetAsync(Guid actorId, Guid organizationId, Guid quoteId, CancellationToken cancellationToken) => Fail();

    private static Task<QuoteResult> Fail() => Task.FromException<QuoteResult>(
        new QuoteServiceUnavailableException("Pricing is disabled."));
}
