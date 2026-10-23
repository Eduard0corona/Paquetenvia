namespace Notifications.Application.RecipientNotices;

/// <summary>
/// NTF-RECIPIENT-NOTICES-2026-10-20 (E4-P6 "De "tú", sin nombres (Recommended)", E4-P12, E4-P14; AI-24
/// <c>recipient_notices.templates</c>) and NTF-RECIPIENT-NOTICE-CAPS-2026-10-23 L4 choice 8: the WhatsApp templates and
/// the e-mail texts exactly as AI-24 records them (a contract test compares them with the YAML). The WhatsApp texts live
/// in WhatsApp Manager under the approved template names; the e-mail texts are configuration
/// (<c>Messaging:Email:Templates:&lt;key&gt;</c>), which the Worker refuses at start unless it equals
/// <see cref="EmailSubject"/> and <see cref="EmailPlainText"/>. The adapters keep no default text. The privacy notice line
/// is added only when GATE-007 gives its wording and address.
/// </summary>
public static class RecipientNoticeTexts
{
    public const string WhatsAppCategory = "UTILITY";
    public const string WhatsAppLanguage = "es_MX";

    /// <summary>Body parameters of each WhatsApp template: <c>{{1}}</c>, the número de guía.</summary>
    public const int WhatsAppParameterCount = 1;

    /// <summary>The first button: a URL button whose dynamic suffix is the tracking token.</summary>
    public const string WhatsAppUrlButton = "Ver seguimiento";

    /// <summary>The second button: a quick reply.</summary>
    public const string WhatsAppQuickReplyButton = "No recibir avisos";

    /// <summary>The fixed payload of the quick reply (never personal data); AI-08 NTF-006 reads it as an opt-out.</summary>
    public const string WhatsAppQuickReplyPayload = "RECIPIENT_NOTICE_OPT_OUT";

    public const string WhatsAppFooter = "Responde BAJA para no recibir más avisos";

    /// <summary>E-mail placeholders: <c>{{1}}</c> número de guía, <c>{{2}}</c> tracking link, <c>{{3}}</c> unsubscribe link.</summary>
    public const int EmailParameterCount = 3;

    public const string EmailSender = "avisos@paquetenvia.com";
    public const string EmailSenderDisplayName = "Seguimiento de envío";
    public const string EmailUnsubscribeLine = "Para dejar de recibir estos correos: {{3}}";
    public const string EmailAutomaticLine = "Este correo se envía automáticamente; no lo respondas.";

    public static string WhatsAppBody(RecipientNoticeKind kind) => kind switch
    {
        RecipientNoticeKind.TrackingLink =>
            "Hola. Hay un envío para ti con número de guía {{1}}. Puedes ver cómo va en cualquier momento con el botón Ver seguimiento. Si no esperabas este envío, ignora este mensaje.",
        RecipientNoticeKind.InTransit =>
            "Tu envío con número de guía {{1}} ya va en camino a la dirección de entrega. Síguelo con el botón Ver seguimiento.",
        RecipientNoticeKind.DeliveryFailed =>
            "Intentamos entregar tu envío con número de guía {{1}} y no fue posible. Con el botón Ver seguimiento verás el siguiente paso.",
        RecipientNoticeKind.Delivered =>
            "Tu envío con número de guía {{1}} fue entregado. Puedes ver el detalle con el botón Ver seguimiento.",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown recipient notice."),
    };

    public static string EmailSubject(RecipientNoticeKind kind) => kind switch
    {
        RecipientNoticeKind.TrackingLink => "Seguimiento de envío: guía {{1}}",
        RecipientNoticeKind.InTransit => "Tu envío {{1}} va en camino",
        RecipientNoticeKind.DeliveryFailed => "No pudimos entregar tu envío {{1}}",
        RecipientNoticeKind.Delivered => "Tu envío {{1}} fue entregado",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown recipient notice."),
    };

    public static string EmailBody(RecipientNoticeKind kind) => kind switch
    {
        RecipientNoticeKind.TrackingLink =>
            "Hola. Hay un envío para ti con número de guía {{1}}. Puedes ver cómo va en cualquier momento en el enlace de abajo. Si no esperabas este envío, ignora este mensaje.",
        RecipientNoticeKind.InTransit =>
            "Tu envío con número de guía {{1}} ya va en camino a la dirección de entrega. Síguelo en el enlace de abajo.",
        RecipientNoticeKind.DeliveryFailed =>
            "Intentamos entregar tu envío con número de guía {{1}} y no fue posible. En el enlace de abajo verás el siguiente paso.",
        RecipientNoticeKind.Delivered =>
            "Tu envío con número de guía {{1}} fue entregado. Puedes ver el detalle en el enlace de abajo.",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown recipient notice."),
    };

    /// <summary>
    /// AI-24 <c>recipient_notices.templates.email.body</c>: the e-mail body of the notice, then the full tracking link
    /// (<c>{{2}}</c>) on a line of its own, the unsubscribe line with the link of that notification (<c>{{3}}</c>) and the
    /// automatic-message line, blocks separated by an empty line. The privacy notice line comes with GATE-007.
    /// </summary>
    public static string EmailPlainText(RecipientNoticeKind kind) =>
        string.Join("\n\n", EmailBody(kind), "{{2}}", EmailUnsubscribeLine, EmailAutomaticLine);
}
