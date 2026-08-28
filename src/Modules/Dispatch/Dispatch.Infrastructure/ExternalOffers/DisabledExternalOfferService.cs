using Dispatch.Application.Assignments;
using Dispatch.Application.ExternalOffers;

namespace Dispatch.Infrastructure.ExternalOffers;

public sealed class DisabledExternalOfferService : IExternalOfferService
{
    public Task<ExternalOfferResult> CreateAsync(
        CreateExternalOfferCommand command,
        CancellationToken cancellationToken) =>
        throw new ExternalOfferForbiddenException();

    public Task<ExternalOfferPageResult> ListEligibleAsync(
        Guid actorId,
        Guid organizationId,
        string? cursor,
        CancellationToken cancellationToken) =>
        throw new ExternalOfferForbiddenException();

    public Task<AssignmentResult> AcceptAsync(
        AcceptExternalOfferCommand command,
        CancellationToken cancellationToken) =>
        throw new ExternalOfferForbiddenException();
}
