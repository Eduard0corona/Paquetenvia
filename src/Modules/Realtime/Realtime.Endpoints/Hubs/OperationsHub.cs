using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;
using System.Text.Json;
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
        EmitDiagnostic("server_onconnected_entry", new
        {
            connection_id = Context.ConnectionId,
        });
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
        EmitDiagnostic("server_authorization_complete", new
        {
            connection_id = Context.ConnectionId,
            authorized = result.IsAuthorized,
            organization_id = result.Authorization is null
                ? null
                : result.Authorization.OrganizationId.ToString("D"),
        });
        if (!result.IsAuthorized || result.Authorization is null)
        {
            Reject();
        }

        var group = RealtimeGroupNames.Organization(result.Authorization.OrganizationId);
        EmitDiagnostic("server_group_add_begin", new
        {
            connection_id = Context.ConnectionId,
            organization_id = result.Authorization.OrganizationId.ToString("D"),
            group,
        });
        await Groups.AddToGroupAsync(
            Context.ConnectionId,
            group,
            Context.ConnectionAborted);
        EmitDiagnostic("server_group_add_complete", new
        {
            connection_id = Context.ConnectionId,
            organization_id = result.Authorization.OrganizationId.ToString("D"),
            group,
        });
        _accepted = true;
        telemetry.ConnectionAccepted("operations", "oidc");
        await base.OnConnectedAsync();
        EmitDiagnostic("server_onconnected_complete", new
        {
            connection_id = Context.ConnectionId,
            organization_id = result.Authorization.OrganizationId.ToString("D"),
            group,
        });
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        EmitDiagnostic("server_ondisconnected_entry", new
        {
            connection_id = Context.ConnectionId,
            accepted = _accepted,
            error_type = exception?.GetType().Name,
            error_message = exception?.Message,
        });
        if (_accepted)
        {
            telemetry.ConnectionClosed("operations");
        }

        await base.OnDisconnectedAsync(exception);
        EmitDiagnostic("server_ondisconnected_complete", new
        {
            connection_id = Context.ConnectionId,
            accepted = _accepted,
        });
    }

    private static readonly long DiagnosticProcessStartedAt = Stopwatch.GetTimestamp();

    private static void EmitDiagnostic(string eventName, object details)
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("PAQUETERIA_REALTIME_DIAGNOSTICS"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var testElapsed = long.TryParse(
            Environment.GetEnvironmentVariable("PAQUETERIA_DIAGNOSTIC_TEST_STARTED_UNIX_MS"),
            out var testStartedAt)
            ? now.ToUnixTimeMilliseconds() - testStartedAt
            : (long?)null;
        Console.Error.WriteLine(
            $"RTDIAG {JsonSerializer.Serialize(new
            {
                schema = "paquetenvia-realtime-correlation-v1",
                repetition = Environment.GetEnvironmentVariable("PAQUETERIA_DIAGNOSTIC_REPETITION"),
                testcase = Environment.GetEnvironmentVariable("PAQUETERIA_DIAGNOSTIC_TESTCASE"),
                process_role = "operations-hub",
                pid = Environment.ProcessId,
                correlation_id = Environment.GetEnvironmentVariable("PAQUETERIA_DIAGNOSTIC_CORRELATION_ID"),
                utc = now.ToString("O"),
                elapsed_ms = testElapsed,
                process_elapsed_ms = Stopwatch.GetElapsedTime(DiagnosticProcessStartedAt).TotalMilliseconds,
                @event = eventName,
                details,
            })}");
    }

    [DoesNotReturn]
    private void Reject()
    {
        telemetry.ConnectionRejected("operations", "oidc");
        Context.Abort();
        throw new HubException("Connection rejected.");
    }
}
