using Microsoft.AspNetCore.Routing;

namespace Drivers.Endpoints;

/// <summary>VOICE-001: every route of the masked call bridge, mapped together by the API host.</summary>
public static class DriverVoiceEndpoints
{
    public static IEndpointRouteBuilder MapDriverVoiceEndpoints(this IEndpointRouteBuilder endpoints) =>
        endpoints
            .MapDriverPhoneEndpoints()
            .MapRecipientCallEndpoints()
            .MapVoiceWebhookEndpoints();
}
