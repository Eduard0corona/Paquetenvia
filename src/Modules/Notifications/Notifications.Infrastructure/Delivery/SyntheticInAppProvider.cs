using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Notifications.Application.Dispatching;

namespace Notifications.Infrastructure.Delivery;

internal sealed record SyntheticInAppRequest(
    Guid NotificationId,
    string TemplateKey,
    int TemplateVersion,
    string Channel,
    string Body,
    IReadOnlyDictionary<string, string> Variables,
    string IdempotencyKey);

internal sealed record SyntheticInAppResult(string Outcome, string Code, string Receipt);

internal interface ISyntheticInAppProvider
{
    ValueTask<SyntheticInAppResult> SendAsync(
        SyntheticInAppRequest request,
        CancellationToken cancellationToken);
}

internal sealed class SyntheticInAppProvider(IOptions<NotificationsOptions> options)
    : ISyntheticInAppProvider
{
    public ValueTask<SyntheticInAppResult> SendAsync(
        SyntheticInAppRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Channel != NotificationTemplate.Channel ||
            request.TemplateKey != NotificationTemplate.Key ||
            request.TemplateVersion != NotificationTemplate.Version ||
            request.IdempotencyKey != NotificationIdempotencyKey.Create(
                request.NotificationId,
                request.TemplateKey,
                request.TemplateVersion,
                request.Channel))
        {
            throw new NotificationMessageException(NotificationErrorCodes.InvalidPayload);
        }

        var rendered = NotificationTemplateRenderer.Render(request.Body, request.Variables);
        var receipt = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            request.IdempotencyKey + "|" + rendered))).ToLowerInvariant();
        return ValueTask.FromResult(options.Value.SyntheticOutcome switch
        {
            SyntheticInAppOutcome.Success =>
                new SyntheticInAppResult("SUCCESS", NotificationErrorCodes.ProviderAccepted, receipt),
            SyntheticInAppOutcome.TransientFailure =>
                new SyntheticInAppResult("TRANSIENT", NotificationErrorCodes.ProviderTransient, receipt),
            SyntheticInAppOutcome.PermanentFailure =>
                new SyntheticInAppResult("PERMANENT", NotificationErrorCodes.ProviderPermanent, receipt),
            SyntheticInAppOutcome.AmbiguousTimeout =>
                new SyntheticInAppResult("AMBIGUOUS", NotificationErrorCodes.ProviderAmbiguous, receipt),
            _ => throw new InvalidOperationException("Unknown synthetic provider outcome."),
        });
    }
}
