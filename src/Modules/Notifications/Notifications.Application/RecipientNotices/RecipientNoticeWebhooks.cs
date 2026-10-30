using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Paqueteria.Application.Messaging;
using Paqueteria.Application.Security;

namespace Notifications.Application.RecipientNotices;

/// <summary>The forward-only target of a delivery receipt (AI-24 <c>recipient_notices.receipts</c>).</summary>
public enum MessagingReceiptStatus
{
    Delivered,
    Failed,
}

/// <summary>
/// One delivery receipt: the channel wire name (<c>WHATSAPP</c> or <c>EMAIL</c>), the provider reference the send stored
/// (wamid or ACS operation id), the new status, the closed failure code and the provider's time. Never a recipient.
/// </summary>
public sealed record MessagingReceipt(
    string Channel,
    string ProviderReference,
    MessagingReceiptStatus Status,
    string? Code,
    DateTimeOffset? ReportedAt);

public enum MessagingReceiptResult
{
    /// <summary>A SENT notice moved to DELIVERED or FAILED, with its status event and status-changed row.</summary>
    Recorded,

    /// <summary>The notice is not SENT (a repeated receipt, or one after the notice moved on): nothing changed.</summary>
    Unchanged,

    /// <summary>No notice has the reference (O11 choice 6: also a receipt that arrives before its send was settled).</summary>
    UnknownReference,
}

/// <summary>
/// One inbound WhatsApp message of a sender, in memory only: the sender's <c>from</c> digits, the message time and
/// whether it is an opt-out (a keyword or the quick reply). The message text, the profile name and the
/// <c>recipient_id</c> are never kept. <see cref="ToString"/> prints neither the number nor the time.
/// </summary>
public sealed class InboundWhatsAppMessage(string from, DateTimeOffset? sentAt, bool isOptOut)
{
    public string From { get; } = from;

    public DateTimeOffset? SentAt { get; } = sentAt;

    public bool IsOptOut { get; } = isOptOut;

    public override string ToString() => $"InboundWhatsAppMessage {{ IsOptOut = {IsOptOut} }}";
}

/// <summary>What one signed Meta delivery carries for the configured number: receipts and inbound messages.</summary>
public sealed record MetaWhatsAppWebhookBatch(
    IReadOnlyList<MessagingReceipt> Receipts,
    IReadOnlyList<InboundWhatsAppMessage> Messages)
{
    public static MetaWhatsAppWebhookBatch Empty { get; } = new([], []);

    public override string ToString() =>
        $"MetaWhatsAppWebhookBatch {{ Receipts = {Receipts.Count}, Messages = {Messages.Count} }}";
}

/// <summary>What one Event Grid delivery carries: the handshake code, or the e-mail delivery receipts.</summary>
public sealed record AcsEmailEventsBatch(string? ValidationCode, IReadOnlyList<MessagingReceipt> Receipts)
{
    public override string ToString() =>
        $"AcsEmailEventsBatch {{ Validation = {ValidationCode is not null}, Receipts = {Receipts.Count} }}";
}

/// <summary>
/// AI-24 <c>recipient_notices.opt_out.whatsapp_reply</c> and NTF-RECIPIENT-NOTICE-RECEIPTS-2026-10-25 O11 choice 4: an
/// inbound text is an opt-out when, trimmed and with case and accents ignored, it is exactly BAJA, ALTO, STOP or CANCELAR;
/// a tap on the quick reply is recognized by its payload <c>RECIPIENT_NOTICE_OPT_OUT</c> or by its text "No recibir
/// avisos", normalized the same way.
/// </summary>
public static class RecipientOptOutMessages
{
    public static bool IsOptOutKeyword(string? text) =>
        Normalize(text) is { } normalized && RecipientNoticeTexts.OptOutKeywords.Contains(normalized, StringComparer.Ordinal);

    public static bool IsOptOutQuickReply(string? payload, string? text) =>
        string.Equals(payload, RecipientNoticeTexts.WhatsAppQuickReplyPayload, StringComparison.Ordinal) ||
        (Normalize(text) is { } normalized &&
         string.Equals(normalized, Normalize(RecipientNoticeTexts.WhatsAppQuickReplyButton), StringComparison.Ordinal));

    /// <summary>Trimmed, accents removed, upper-case invariant; null for an empty or overlong text.</summary>
    internal static string? Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 256)
        {
            return null;
        }

        var decomposed = text.Trim().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(character);
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC).ToUpperInvariant();
    }
}

/// <summary>
/// AI-05 <c>receiveMetaWhatsAppWebhook</c> (AI-24 <c>recipient_notices.receipts.whatsapp</c>, <c>opt_out</c> and
/// <c>auto_reply</c>; O11 choice 5): reads a body whose signature was already verified. It is not a JSON object: the
/// caller answers 400. Only a <c>whatsapp_business_account</c> object is read, and only changes of field
/// <c>messages</c> whose <c>metadata.phone_number_id</c> is the configured number; everything else, and every field it
/// does not use, is ignored. <c>delivered</c> and <c>failed</c> statuses become receipts (<c>sent</c> and <c>read</c> are
/// ignored; a failure carries the code of the send classifier); every inbound message becomes an opt-out or an
/// automatic-reply candidate. Nothing it reads is logged.
/// </summary>
public static partial class MetaWhatsAppWebhookParser
{
    public const string BusinessAccountObject = "whatsapp_business_account";

    public static bool TryParse(ReadOnlyMemory<byte> body, string phoneNumberId, out MetaWhatsAppWebhookBatch batch)
    {
        batch = MetaWhatsAppWebhookBatch.Empty;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 32 });
        }
        catch (JsonException)
        {
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !WebhookJson.HasReadableStrings(root))
            {
                return false;
            }

            if (!IsString(root, "object", out var objectKind) ||
                !string.Equals(objectKind, BusinessAccountObject, StringComparison.Ordinal) ||
                !root.TryGetProperty("entry", out var entries) || entries.ValueKind != JsonValueKind.Array ||
                string.IsNullOrEmpty(phoneNumberId))
            {
                return true;
            }

            var receipts = new List<MessagingReceipt>();
            var messages = new List<InboundWhatsAppMessage>();
            foreach (var entry in entries.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object ||
                    !entry.TryGetProperty("changes", out var changes) || changes.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var change in changes.EnumerateArray())
                {
                    if (change.ValueKind != JsonValueKind.Object ||
                        !IsString(change, "field", out var field) || field != "messages" ||
                        !change.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Object ||
                        !value.TryGetProperty("metadata", out var metadata) || metadata.ValueKind != JsonValueKind.Object ||
                        !IsString(metadata, "phone_number_id", out var number) ||
                        !string.Equals(number, phoneNumberId, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    ReadStatuses(value, receipts);
                    ReadMessages(value, messages);
                }
            }

            batch = new MetaWhatsAppWebhookBatch(receipts, messages);
            return true;
        }
    }

    private static void ReadStatuses(JsonElement value, List<MessagingReceipt> receipts)
    {
        if (!value.TryGetProperty("statuses", out var statuses) || statuses.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var status in statuses.EnumerateArray())
        {
            if (status.ValueKind != JsonValueKind.Object ||
                !IsString(status, "id", out var reference) || !ProviderReferencePattern().IsMatch(reference) ||
                !IsString(status, "status", out var name))
            {
                continue;
            }

            var reportedAt = UnixTime(status);
            switch (name)
            {
                case "delivered":
                    receipts.Add(new MessagingReceipt("WHATSAPP", reference, MessagingReceiptStatus.Delivered, null, reportedAt));
                    break;
                case "failed":
                    receipts.Add(new MessagingReceipt(
                        "WHATSAPP", reference, MessagingReceiptStatus.Failed, FailureCode(status), reportedAt));
                    break;
            }
        }
    }

    /// <summary>The first error code of a failed status, classified like a send; none is a provider rejection.</summary>
    private static string FailureCode(JsonElement status)
    {
        if (status.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array &&
            errors.GetArrayLength() > 0 && errors[0].ValueKind == JsonValueKind.Object &&
            errors[0].TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number &&
            code.TryGetInt64(out var value))
        {
            return MetaWhatsAppErrorCodes.Classify(value);
        }

        return MessagingResultCodes.Rejected;
    }

    private static void ReadMessages(JsonElement value, List<InboundWhatsAppMessage> messages)
    {
        if (!value.TryGetProperty("messages", out var inbound) || inbound.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var message in inbound.EnumerateArray())
        {
            if (message.ValueKind != JsonValueKind.Object || !IsString(message, "from", out var from) ||
                from.Length is < 1 or > 32)
            {
                continue;
            }

            var optOut = false;
            if (IsString(message, "type", out var type))
            {
                optOut = type switch
                {
                    "text" => message.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.Object &&
                        IsString(text, "body", out var body) && RecipientOptOutMessages.IsOptOutKeyword(body),
                    "button" => message.TryGetProperty("button", out var button) &&
                        button.ValueKind == JsonValueKind.Object &&
                        RecipientOptOutMessages.IsOptOutQuickReply(
                            IsString(button, "payload", out var payload) ? payload : null,
                            IsString(button, "text", out var label) ? label : null),
                    _ => false,
                };
            }

            messages.Add(new InboundWhatsAppMessage(from, UnixTime(message), optOut));
        }
    }

    private static DateTimeOffset? UnixTime(JsonElement element) =>
        IsString(element, "timestamp", out var text) &&
        long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) &&
        seconds is > 0 and < 253_402_300_800
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;

    internal static bool IsString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String &&
            property.GetString() is { } text)
        {
            value = text;
            return true;
        }

        return false;
    }

    [GeneratedRegex("^[A-Za-z0-9._:=+/-]{1,256}$", RegexOptions.CultureInvariant)]
    internal static partial Regex ProviderReferencePattern();
}

/// <summary>
/// The JSON rule both webhook readers share: JSON admits an escaped lone surrogate (<c>"\uD800"</c>) that
/// System.Text.Json parses but cannot read as a string. A body with any such string, as a property name or a value, is
/// malformed (the caller answers 400) instead of an exception and a 500.
/// </summary>
internal static class WebhookJson
{
    internal static bool HasReadableStrings(JsonElement element)
    {
        try
        {
            ReadEveryString(element);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static void ReadEveryString(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                _ = element.GetString();
                break;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    _ = property.Name;
                    ReadEveryString(property.Value);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    ReadEveryString(item);
                }

                break;
        }
    }
}

/// <summary>
/// AI-05 <c>receiveAcsEmailEvents</c> (AI-24 <c>recipient_notices.receipts.email</c>; O6 "Cuarentena = bloqueado; Expanded
/// se ignora (Recommended)"): reads an Event Grid event schema body, an array of events. It is not an array: the caller
/// answers 400. A <c>Microsoft.EventGrid.SubscriptionValidationEvent</c> gives the handshake code. Each
/// <c>Microsoft.Communication.EmailDeliveryReportReceived</c> event of the configured Communication Services resource
/// becomes a receipt: Delivered is DELIVERED; Bounced and Suppressed are FAILED MESSAGING_RECIPIENT_UNDELIVERABLE;
/// FilteredSpam and Quarantined are FAILED MESSAGING_POLICY_BLOCKED; Failed is FAILED MESSAGING_PROVIDER_REJECTED;
/// Expanded, an unknown status and every other event type are ignored. The recipient address and the status message are
/// never read.
/// </summary>
public static partial class AcsEmailEventsParser
{
    public const string ValidationEventType = "Microsoft.EventGrid.SubscriptionValidationEvent";
    public const string DeliveryReportEventType = "Microsoft.Communication.EmailDeliveryReportReceived";

    public static bool TryParse(ReadOnlyMemory<byte> body, string? sourceResourceId, out AcsEmailEventsBatch batch)
    {
        batch = new AcsEmailEventsBatch(null, []);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 32 });
        }
        catch (JsonException)
        {
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array || !WebhookJson.HasReadableStrings(root))
            {
                return false;
            }

            string? validationCode = null;
            var receipts = new List<MessagingReceipt>();
            foreach (var item in root.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !MetaWhatsAppWebhookParser.IsString(item, "eventType", out var eventType) ||
                    !item.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (eventType == ValidationEventType)
                {
                    // Event Grid sends a GUID; nothing else is echoed back in validationResponse.
                    if (MetaWhatsAppWebhookParser.IsString(data, "validationCode", out var code) &&
                        ValidationCodePattern().IsMatch(code))
                    {
                        validationCode ??= code;
                    }

                    continue;
                }

                if (eventType != DeliveryReportEventType ||
                    !MetaWhatsAppWebhookParser.IsString(item, "topic", out var topic) ||
                    string.IsNullOrEmpty(sourceResourceId) ||
                    !string.Equals(topic, sourceResourceId, StringComparison.OrdinalIgnoreCase) ||
                    !MetaWhatsAppWebhookParser.IsString(data, "messageId", out var reference) ||
                    !MetaWhatsAppWebhookParser.ProviderReferencePattern().IsMatch(reference) ||
                    !MetaWhatsAppWebhookParser.IsString(data, "status", out var status) ||
                    Map(status) is not { } mapped)
                {
                    continue;
                }

                DateTimeOffset? reportedAt = MetaWhatsAppWebhookParser.IsString(data, "deliveryAttemptTimeStamp", out var time) &&
                    DateTimeOffset.TryParse(time, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedTime)
                        ? parsedTime
                        : null;
                receipts.Add(new MessagingReceipt("EMAIL", reference, mapped.Status, mapped.Code, reportedAt));
            }

            batch = new AcsEmailEventsBatch(validationCode, receipts);
            return true;
        }
    }

    /// <summary>O6: the ACS status map; null for Expanded (not terminal) and for an unknown status.</summary>
    public static (MessagingReceiptStatus Status, string? Code)? Map(string status) => status switch
    {
        "Delivered" => (MessagingReceiptStatus.Delivered, null),
        "Bounced" or "Suppressed" => (MessagingReceiptStatus.Failed, MessagingResultCodes.RecipientUndeliverable),
        "FilteredSpam" or "Quarantined" => (MessagingReceiptStatus.Failed, MessagingResultCodes.PolicyBlocked),
        "Failed" => (MessagingReceiptStatus.Failed, MessagingResultCodes.Rejected),
        _ => null,
    };

    [GeneratedRegex("^[A-Za-z0-9-]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex ValidationCodePattern();
}

/// <summary>
/// One inbound WhatsApp message to record (AI-24 <c>recipient_notices.opt_out</c> and <c>auto_reply</c>): the request id,
/// whether it is an opt-out, the sender's keyed hashes (current version first), the sender number sealed under the
/// recipient-notice key and its key version, and the message time. <see cref="ToString"/> prints none of the values.
/// </summary>
public sealed record RecipientInboundRecord(
    Guid RequestId,
    bool IsOptOut,
    IReadOnlyList<ContactLookupHash> Hashes,
    byte[] SealedNumber,
    string NumberKeyVersion,
    DateTimeOffset ReceivedAt)
{
    public override string ToString() => $"RecipientInboundRecord {{ IsOptOut = {IsOptOut} }}";
}

public enum RecipientInboundResult
{
    /// <summary>
    /// The WhatsApp opt-out was recorded and its one confirmation requested (none for a message older than 23 hours).
    /// </summary>
    OptOutRecorded,

    /// <summary>The contact was already opted out on WhatsApp: no confirmation and no automatic reply.</summary>
    OptOutAlreadyRecorded,

    /// <summary>The automatic reply was requested.</summary>
    AutoReplyRequested,

    /// <summary>The contact already had its automatic reply in the 24 hours before this message.</summary>
    AutoReplyLimited,

    /// <summary>The message is older than 23 hours: no automatic reply is requested.</summary>
    Stale,
}

/// <summary>
/// The e-mail copy an unsubscribe token points to: the destination of an ORDER_DESTINATION e-mail notice, as the PII key
/// sealed it. <see cref="ToString"/> prints no ciphertext.
/// </summary>
public sealed record RecipientNoticeUnsubscribeContact(Guid OwnerOrganizationId, Guid LocationId, string KeyVersion, byte[] EmailCiphertext)
{
    public override string ToString() => $"RecipientNoticeUnsubscribeContact {{ LocationId = {LocationId:D} }}";
}

/// <summary>
/// NTF-RECIPIENT-NOTICE-RECEIPTS-2026-10-25: the API's store of receipts, opt-outs and automatic-reply requests. Every
/// write goes through the SECURITY DEFINER functions of the receipt executor (AI-18 recipient notices step 3) in a short
/// transaction with an empty organization context; the unsubscribe read runs in a tenant transaction of the token's
/// organization under FORCE RLS. A store failure throws <see cref="RecipientNoticeStoreUnavailableException"/> (503).
/// </summary>
public interface IRecipientNoticeReceiptStore
{
    Task<MessagingReceiptResult> RecordReceiptAsync(MessagingReceipt receipt, CancellationToken cancellationToken);

    /// <summary>One transaction: the opt-out and, when it is new, its confirmation; or the automatic-reply request.</summary>
    Task<RecipientInboundResult> RecordWhatsAppInboundAsync(RecipientInboundRecord record, CancellationToken cancellationToken);

    /// <summary>Records the EMAIL opt-out (source EMAIL_LINK); false when it was already recorded.</summary>
    Task<bool> RecordEmailOptOutAsync(IReadOnlyList<ContactLookupHash> hashes, CancellationToken cancellationToken);

    /// <summary>The e-mail copy of an ORDER_DESTINATION e-mail notice of the organization, or null.</summary>
    Task<RecipientNoticeUnsubscribeContact?> ReadUnsubscribeContactAsync(
        Guid ownerOrganizationId,
        Guid notificationId,
        CancellationToken cancellationToken);
}

/// <summary>The store or a key was unavailable: the caller answers 503 so the provider retries.</summary>
public sealed class RecipientNoticeStoreUnavailableException(Exception? innerException = null)
    : Exception("The recipient notice store is unavailable.", innerException);

/// <summary>
/// NTF-RECIPIENT-NOTICE-RECEIPTS-2026-10-25 (O2, O11 choice 9): the switches and the secrets of the provider webhooks,
/// read from configuration. Every secret is compared in constant time against both configured slots (current and
/// previous) before anything is parsed; no secret, header or comparison result is ever logged.
/// </summary>
public interface IRecipientNoticeWebhookGuard
{
    /// <summary><c>Messaging:WhatsApp:Webhook:Enabled</c>; false answers the uniform 404.</summary>
    bool WhatsAppEnabled { get; }

    /// <summary><c>Messaging:Email:Events:Enabled</c>; false answers the uniform 404.</summary>
    bool EmailEventsEnabled { get; }

    /// <summary>The configured WhatsApp Business phone number id (O11 choice 5).</summary>
    string WhatsAppPhoneNumberId { get; }

    /// <summary>The configured Communication Services resource id, the <c>topic</c> of its events.</summary>
    string EmailEventsSourceResourceId { get; }

    /// <summary><c>X-Hub-Signature-256</c>: <c>sha256=</c> and the hex HMAC-SHA256 of the raw body under the app secret.</summary>
    bool IsValidMetaSignature(string? header, ReadOnlySpan<byte> body);

    /// <summary><c>hub.verify_token</c> of the verification request.</summary>
    bool IsValidMetaVerifyToken(string? token);

    /// <summary>The Key Vault secret header of an Event Grid delivery (O2).</summary>
    bool IsValidEventGridSecret(string? header);
}

/// <summary>
/// NTF-RECIPIENT-NOTICE-RECEIPTS-2026-10-25: what a verified delivery is turned into. Each throws
/// <see cref="RecipientNoticeStoreUnavailableException"/> when the store or a key is unavailable.
/// </summary>
public interface IRecipientNoticeWebhookService
{
    Task ReceiveWhatsAppAsync(MetaWhatsAppWebhookBatch batch, CancellationToken cancellationToken);

    Task ReceiveEmailEventsAsync(IReadOnlyList<MessagingReceipt> receipts, CancellationToken cancellationToken);
}

public enum RecipientNoticeUnsubscribeResult
{
    /// <summary>The EMAIL opt-out of the notice's e-mail is recorded (now or earlier): 204.</summary>
    Recorded,

    /// <summary>The token is invalid, or its notice or e-mail copy no longer exists: the uniform 404.</summary>
    NotFound,

    /// <summary>The store or a key is unavailable: 503.</summary>
    Unavailable,
}

/// <summary>AI-05 <c>unsubscribeRecipientNotices</c>: the e-mail opt-out of the notification a token names.</summary>
public interface IRecipientNoticeUnsubscribeService
{
    Task<RecipientNoticeUnsubscribeResult> UnsubscribeAsync(string? token, CancellationToken cancellationToken);
}
