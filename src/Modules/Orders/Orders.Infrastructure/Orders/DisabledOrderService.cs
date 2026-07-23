using Orders.Application.Orders;

namespace Orders.Infrastructure.Orders;

public sealed class DisabledOrderService : IOrderService
{
    public Task<OrderResult> CreateAsync(CreateOrderCommand command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new OrderConflictException(OrderConflictCode.QuoteUnavailable);
    }

    public Task<OrderPageResult> ListAsync(
        Guid actorId,
        Guid organizationId,
        string? status,
        Guid? ownerOrganizationId,
        string? cursor,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new OrderPageResult([], null));
    }

    public Task<OrderDetailResult> GetAsync(
        Guid actorId,
        Guid organizationId,
        Guid orderId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new OrderNotFoundException();
    }
}
