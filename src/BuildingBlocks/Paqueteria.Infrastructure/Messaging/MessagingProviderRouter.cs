using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Paqueteria.Application.Messaging;

namespace Paqueteria.Infrastructure.Messaging;

/// <summary>One channel adapter behind <see cref="IMessagingProvider"/>.</summary>
internal interface IMessagingChannelProvider
{
    MessagingChannel Channel { get; }

    ValueTask<MessagingResult> SendAsync(MessagingRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Routes each request to the provider configured for its channel. A <c>Disabled</c> channel fails
/// closed with <see cref="MessagingResultCodes.ChannelDisabled"/> (permanent, nothing is sent).
/// Adapters are resolved lazily, so a disabled channel never builds its HTTP client or credential.
/// </summary>
internal sealed class MessagingProviderRouter(IServiceProvider services, IOptions<MessagingOptions> options)
    : IMessagingProvider
{
    public ValueTask<MessagingResult> SendAsync(MessagingRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.MessageId == Guid.Empty || string.IsNullOrWhiteSpace(request.TemplateKey))
        {
            return ValueTask.FromResult(new MessagingResult(MessagingOutcome.PermanentFailure, MessagingResultCodes.TemplateNotConfigured));
        }

        IMessagingChannelProvider? provider = request.Channel switch
        {
            MessagingChannel.WhatsApp => options.Value.WhatsApp.Provider switch
            {
                WhatsAppProviderKind.MetaCloudApi => services.GetRequiredService<MetaWhatsAppCloudApiProvider>(),
                WhatsAppProviderKind.Synthetic => services.GetRequiredService<SyntheticWhatsAppProvider>(),
                _ => null,
            },
            MessagingChannel.Email => options.Value.Email.Provider switch
            {
                EmailProviderKind.AzureCommunicationServices => services.GetRequiredService<AzureCommunicationEmailProvider>(),
                EmailProviderKind.Synthetic => services.GetRequiredService<SyntheticEmailProvider>(),
                _ => null,
            },
            _ => null,
        };

        return provider is null
            ? ValueTask.FromResult(new MessagingResult(MessagingOutcome.PermanentFailure, MessagingResultCodes.ChannelDisabled))
            : provider.SendAsync(request, cancellationToken);
    }
}

/// <summary>
/// Deterministic fake for tests, CI and DEV_SYNTHETIC (AI-03 §15): no network. It enforces the same
/// template configuration and recipient/parameter rules as the real adapter, then returns the
/// configured outcome with a receipt derived only from the message id, channel and template key.
/// </summary>
internal abstract class SyntheticMessagingProvider : IMessagingChannelProvider
{
    public abstract MessagingChannel Channel { get; }

    protected abstract MessagingOutcome ConfiguredOutcome { get; }

    public ValueTask<MessagingResult> SendAsync(MessagingRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (configured, parameterCount) = ReadTemplate(request.TemplateKey);
        if (!configured)
        {
            return Result(MessagingOutcome.PermanentFailure, MessagingResultCodes.TemplateNotConfigured);
        }

        if (!MessagingHttp.AreParametersValid(request.Parameters, parameterCount))
        {
            return Result(MessagingOutcome.PermanentFailure, MessagingResultCodes.TemplateParametersInvalid);
        }

        var recipientValid = Channel == MessagingChannel.WhatsApp
            ? MessagingHttp.TryNormalizePhone(request.Recipient.Address, out _)
            : MessagingOptionsValidator.IsEmailAddress(request.Recipient.Address);
        if (!recipientValid)
        {
            return Result(MessagingOutcome.PermanentFailure, MessagingResultCodes.RecipientInvalid);
        }

        var receipt = "synthetic-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{request.MessageId:D}|{Channel}|{request.TemplateKey}"))).ToLowerInvariant()[..32];
        return ValueTask.FromResult(ConfiguredOutcome switch
        {
            MessagingOutcome.Accepted => new MessagingResult(MessagingOutcome.Accepted, MessagingResultCodes.SyntheticAccepted, receipt),
            MessagingOutcome.TransientFailure => new MessagingResult(MessagingOutcome.TransientFailure, MessagingResultCodes.SyntheticTransient),
            MessagingOutcome.PermanentFailure => new MessagingResult(MessagingOutcome.PermanentFailure, MessagingResultCodes.SyntheticPermanent),
            MessagingOutcome.AmbiguousTimeout => new MessagingResult(MessagingOutcome.AmbiguousTimeout, MessagingResultCodes.SyntheticAmbiguous),
            _ => throw new InvalidOperationException("Unknown synthetic messaging outcome."),
        });
    }

    protected abstract (bool Configured, int ParameterCount) ReadTemplate(string templateKey);

    private static ValueTask<MessagingResult> Result(MessagingOutcome outcome, string code) =>
        ValueTask.FromResult(new MessagingResult(outcome, code));
}

internal sealed class SyntheticWhatsAppProvider(IOptions<MessagingOptions> options) : SyntheticMessagingProvider
{
    public override MessagingChannel Channel => MessagingChannel.WhatsApp;

    protected override MessagingOutcome ConfiguredOutcome => options.Value.WhatsApp.SyntheticOutcome;

    protected override (bool Configured, int ParameterCount) ReadTemplate(string templateKey) =>
        options.Value.WhatsApp.Templates.TryGetValue(templateKey, out var template)
            ? (true, template.ParameterCount)
            : (false, 0);
}

internal sealed class SyntheticEmailProvider(IOptions<MessagingOptions> options) : SyntheticMessagingProvider
{
    public override MessagingChannel Channel => MessagingChannel.Email;

    protected override MessagingOutcome ConfiguredOutcome => options.Value.Email.SyntheticOutcome;

    protected override (bool Configured, int ParameterCount) ReadTemplate(string templateKey) =>
        options.Value.Email.Templates.TryGetValue(templateKey, out var template)
            ? (true, template.ParameterCount)
            : (false, 0);
}
