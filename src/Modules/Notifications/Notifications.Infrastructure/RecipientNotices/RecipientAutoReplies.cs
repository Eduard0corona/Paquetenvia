using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Notifications.Application.RecipientNotices;
using Paqueteria.Application.Messaging;
using Paqueteria.Application.Scaling;
using Paqueteria.Infrastructure.Messaging;
using Paqueteria.Infrastructure.Observability;

namespace Notifications.Infrastructure.RecipientNotices;

/// <summary>
/// NTF-RECIPIENT-NOTICE-RECEIPTS-2026-10-25 (AI-24 <c>recipient_notices.auto_reply</c> and <c>opt_out.confirmation</c>;
/// O7 "Aprobar lo propuesto (Recommended)"): sends one leased automatic-reply request.
/// <list type="bullet">
/// <item>With notices off (<c>Notifications:RecipientNotices:Enabled</c>) or WhatsApp Disabled the request is dropped
/// and its number erased, with no provider call (O7 point 2).</item>
/// <item>Quiet hours do not apply: it answers the person's own message inside Meta's 23-hour bound (O7 point 1); a
/// request older than that is dropped. There is no daily cap of its own (O7 point 3).</item>
/// <item>The number is opened in memory only; a transient Key Vault failure before the call puts the request back with
/// its number, an unusable number drops it. After the one provider call the number is erased whatever the outcome; an
/// ambiguous WhatsApp send is never retried.</item>
/// </list>
/// Log lines carry the lane, the channel, the notice (<c>AUTO_REPLY</c> or <c>OPT_OUT_CONFIRMATION</c>), the outcome and
/// the code only (AI-24 <c>recipient_notices.logging</c>).
/// </summary>
internal sealed class RecipientAutoReplyProcessor(
    IRecipientAutoReplyStore store,
    RecipientAutoReplyNumberResolver resolver,
    IMessagingProvider provider,
    IOptions<RecipientNoticesOptions> options,
    IOptions<MessagingOptions> messaging,
    IOptions<NotificationsOptions> notifications,
    TimeProvider time,
    ILogger<RecipientAutoReplyProcessor> logger)
{
    private const string Lane = "AUTO_REPLY";

    public async Task ProcessAsync(ClaimedRecipientAutoReply request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!options.Value.Enabled || RecipientNoticeProviders.IsDisabled(messaging.Value, MessagingChannel.WhatsApp))
        {
            await SettleAsync(request, RecipientAutoReplyOutcome.Dropped, MessagingResultCodes.ChannelDisabled, null, cancellationToken);
            return;
        }

        var now = time.GetUtcNow();
        if (request.ReceivedAt <= now - RecipientAutoReplyNumbers.MaximumAge)
        {
            await SettleAsync(request, RecipientAutoReplyOutcome.Dropped, StaleCode, null, cancellationToken);
            return;
        }

        var contact = await resolver.ResolveAsync(request, cancellationToken);
        if (contact.Status == RecipientNoticeContactStatus.Unavailable)
        {
            await SettleAsync(
                request,
                RecipientAutoReplyOutcome.Retry,
                null,
                now + TimeSpan.FromSeconds(notifications.Value.RetryMaximumSeconds),
                cancellationToken);
            return;
        }

        if (contact.Status == RecipientNoticeContactStatus.Unusable)
        {
            await SettleAsync(request, RecipientAutoReplyOutcome.Dropped, MessagingResultCodes.RecipientInvalid, null, cancellationToken);
            return;
        }

        var confirmation = request.Kind == RecipientAutoReplyKind.OptOutConfirmation;
        var result = await provider.SendAsync(
            new MessagingRequest(
                request.RequestId,
                MessagingChannel.WhatsApp,
                contact.Recipient!,
                confirmation ? RecipientNoticeTexts.OptOutConfirmationKey : RecipientNoticeTexts.AutoReplyKey,
                [])
            {
                FreeFormText = confirmation ? RecipientNoticeTexts.OptOutConfirmationText : RecipientNoticeTexts.AutoReplyText,
            },
            cancellationToken);
        var (outcome, code) = result switch
        {
            { Outcome: MessagingOutcome.Accepted } => (RecipientAutoReplyOutcome.Sent, result.Code),
            // Nothing reached the provider: the number cannot be used, or the channel turned off meanwhile.
            { Code: MessagingResultCodes.RecipientInvalid or MessagingResultCodes.ChannelDisabled } =>
                (RecipientAutoReplyOutcome.Dropped, result.Code),
            // NTF-WHATSAPP-AMBIGUOUS-FAILS-2026-10-02: an unknown outcome is never retried.
            { Outcome: MessagingOutcome.AmbiguousTimeout } => (RecipientAutoReplyOutcome.Failed, AmbiguousCode),
            _ => (RecipientAutoReplyOutcome.Failed, result.Code),
        };
        await SettleAsync(request, outcome, code, null, cancellationToken);
    }

    /// <summary>The store code of a request dropped after 23 hours (AI-06).</summary>
    internal const string StaleCode = "AUTO_REPLY_STALE";

    internal const string AmbiguousCode = "AMBIGUOUS_TIMEOUT";

    private async Task SettleAsync(
        ClaimedRecipientAutoReply request,
        RecipientAutoReplyOutcome outcome,
        string? code,
        DateTimeOffset? availableAt,
        CancellationToken cancellationToken)
    {
        var settled = await store.SettleAutoReplyAsync(request, outcome, code, availableAt, cancellationToken);
        logger.LogInformation(
            "Recipient automatic reply processing completed with lane {Lane}, channel {Channel}, notice {Notice}, outcome {Outcome}, code {Code}.",
            Lane,
            "WHATSAPP",
            request.Kind == RecipientAutoReplyKind.OptOutConfirmation ? "OPT_OUT_CONFIRMATION" : "AUTO_REPLY",
            settled ? outcome.ToString().ToUpperInvariant() : "LEASE_LOST",
            code ?? "-");
    }
}

/// <summary>
/// NTF-RECIPIENT-NOTICE-RECEIPTS-2026-10-25: the Worker loop of the automatic-reply store, on the Notifications lane's
/// settings (<c>Notifications:Provider=PostgreSql</c>, batch size, lease and adaptive idle wait). Each poll is its own
/// root trace; a request is processed with no database transaction open.
/// </summary>
internal sealed class RecipientAutoReplyDispatcher(
    IRecipientAutoReplyStore store,
    RecipientAutoReplyProcessor processor,
    IOptions<NotificationsOptions> options,
    TimeProvider timeProvider,
    ILogger<RecipientAutoReplyDispatcher> logger) : BackgroundService
{
    internal const string LoopName = "recipient_auto_reply";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Value.Provider != NotificationsDispatcherProviderKind.PostgreSql)
        {
            return;
        }

        var idle = new OutboxIdleWait(options.Value.PollIntervalMilliseconds, options.Value.MaximumIdleWaitMilliseconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan wait;
            // OBS-003: each poll is a new root trace whose id is in the poll's log lines; it is never exported (Q-PR6-1).
            using (TraceCorrelation.StartPollCycle(LoopName))
            {
                try
                {
                    var claimed = await store.ClaimAutoRepliesAsync(
                        options.Value.BatchSize,
                        TimeSpan.FromSeconds(options.Value.LeaseSeconds),
                        stoppingToken);
                    foreach (var request in claimed)
                    {
                        try
                        {
                            await processor.ProcessAsync(request, stoppingToken);
                        }
                        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception)
                        {
                            // The lease expires and the next claim ends the request FAILED (it may have been sent).
                            logger.LogWarning("Recipient automatic reply processing failed with outcome {Outcome}.", "MESSAGE_FAILURE");
                        }
                    }

                    wait = idle.After(claimed.Count);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception)
                {
                    logger.LogError("Recipient automatic reply loop failed with outcome {Outcome}.", "LOOP_FAILURE");
                    wait = idle.Shortest;
                }
            }

            if (wait > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(wait, timeProvider, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }
}
