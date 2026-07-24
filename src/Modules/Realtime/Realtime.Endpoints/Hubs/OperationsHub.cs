using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.SignalR;
using Realtime.Application.Authorization;
using Realtime.Application.Clients;
using Realtime.Application.Observability;
using Realtime.Application.Publishing;
using Realtime.Endpoints.Connection;

namespace Realtime.Endpoints.Hubs;

public sealed class OperationsHub(
    IRealtimeConnectionAuthorizer authorizer,
    IRealtimeTelemetry telemetry) : Hub<IOperationsClient>
{
    private bool _accepted;

    public override async Task OnConnectedAsync()
    {
        var httpContext = Context.GetHttpContext();
        using var measurement = telemetry.MeasureAuthorization("operations", "oidc");
        var request = httpContext?.Items[RealtimeConnectionGateMiddleware.PrivateRequestItemKey]
            as PrivateRealtimeConnectionRequest;
        if (request is null)
        {
            Reject();
        }

        var result = await authorizer.AuthorizeOperationsAsync(
            request,
            Context.ConnectionAborted);
        if (!result.IsAuthorized || result.Authorization is null)
        {
            Reject();
        }

        await Groups.AddToGroupAsync(
            Context.ConnectionId,
            RealtimeGroupNames.Organization(result.Authorization.OrganizationId),
            Context.ConnectionAborted);
        _accepted = true;
        telemetry.ConnectionAccepted("operations", "oidc");
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (_accepted)
        {
            telemetry.ConnectionClosed("operations");
        }

        await base.OnDisconnectedAsync(exception);
    }

    [DoesNotReturn]
    private void Reject()
    {
        telemetry.ConnectionRejected("operations", "oidc");
        Context.Abort();
        throw new HubException("Connection rejected.");
    }
}
