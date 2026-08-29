using Routing.Application.Routes;

namespace Routing.Infrastructure.Routes;

internal sealed class DisabledRouteService : IRouteService
{
    public Task<RouteResult> CreateAsync(CreateRouteCommand command, CancellationToken cancellationToken) =>
        throw new RoutingForbiddenException();

    public Task<RoutePageResult> ListAsync(ListRoutesQuery query, CancellationToken cancellationToken) =>
        throw new RoutingForbiddenException();

    public Task<RouteDetailResult> GetAsync(GetRouteQuery query, CancellationToken cancellationToken) =>
        throw new RoutingForbiddenException();

    public Task<RouteDetailResult> AddStopAsync(AddRouteStopCommand command, CancellationToken cancellationToken) =>
        throw new RoutingForbiddenException();

    public Task<RouteDetailResult> RemoveStopAsync(RemoveRouteStopCommand command, CancellationToken cancellationToken) =>
        throw new RoutingForbiddenException();

    public Task<RouteDetailResult> ReorderStopsAsync(ReorderRouteStopsCommand command, CancellationToken cancellationToken) =>
        throw new RoutingForbiddenException();
}
