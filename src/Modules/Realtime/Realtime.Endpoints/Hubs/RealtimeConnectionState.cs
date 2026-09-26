using Microsoft.AspNetCore.SignalR;

namespace Realtime.Endpoints.Hubs;

/// <summary>
/// Per-connection acceptance marker. SignalR creates a new hub instance for every
/// invocation (OnConnectedAsync and OnDisconnectedAsync of the same connection run on
/// different instances), so state that must survive until disconnect lives in the
/// connection's <see cref="HubCallerContext.Items"/>, never in hub fields.
/// </summary>
internal static class RealtimeConnectionState
{
    private static readonly object AcceptedKey = new();

    internal static void MarkAccepted(HubCallerContext context) =>
        context.Items[AcceptedKey] = true;

    /// <summary>
    /// Returns true exactly once for an accepted connection, so the active-connections
    /// gauge is decremented once and never for a rejected connection.
    /// </summary>
    internal static bool TryCompleteAccepted(HubCallerContext context) =>
        context.Items.Remove(AcceptedKey);
}
