using System.Globalization;
using System.Xml.Linq;
using Paqueteria.Application.Voice;

namespace Paqueteria.Infrastructure.Voice;

/// <summary>
/// VOICE-001 TwiML. Every message is a fixed Spanish sentence without personal data, brand or order data; numbers
/// appear only as dial targets and caller id. Nothing is recorded (<c>record="do-not-record"</c>). Built with
/// <see cref="XElement"/> so every attribute and text node is escaped.
/// <para>
/// VOICE-001-VOICEMAIL-KEYPRESS-2026-10-11 ("Confirmar con una tecla"): a voicemail greeting can say a number, so
/// nobody is bridged on a mere answer. The driver leg only asks for a 1; the recipient is dialed only after it, and
/// the recipient leg is screened with its own 1 before both legs are connected. Without the key press the call hangs
/// up without bridging.
/// </para>
/// </summary>
internal static class TwilioTwiml
{
    public const string Language = "es-MX";

    /// <summary>The driver leg's prompt, literally as the owner decided it.</summary>
    public const string DriverConfirmPrompt = "Presiona 1 para llamar al destinatario.";

    /// <summary>The recipient leg's prompt, literally as the owner decided it: neutral, without brand (GATE-001).</summary>
    public const string RecipientConfirmPrompt = "Tiene una llamada sobre su envío; presione 1 para contestar.";

    public const string BridgeGreeting =
        "Te comunicamos con el destinatario de tu entrega. Espera en la línea.";

    public const string BridgeEnded = "La llamada con el destinatario terminó.";

    public const string InboundMessage =
        "Gracias por llamar. Este número solo se usa para comunicar a los repartidores con los destinatarios de sus " +
        "entregas y no recibe llamadas. Para dudas sobre tu envío, usa el enlace de seguimiento de tu guía.";

    public const string InboundForwardGreeting = "Gracias por llamar. Te comunicamos con nuestro equipo.";

    /// <summary>
    /// The driver leg (REST create call): only the one-digit prompt. Twilio posts the result, a timeout included
    /// (<c>actionOnEmptyResult</c>), to <see cref="VoiceWebhookPaths.DriverConfirm"/>; no number is in this document.
    /// </summary>
    public static string DriverLeg(TwilioVoiceOptions options, Guid organizationId, Guid callRequestId) =>
        new XElement(
            "Response",
            Gather(
                options,
                options.DriverConfirmSeconds,
                VoiceWebhookPaths.DriverConfirm,
                organizationId,
                callRequestId,
                DriverConfirmPrompt)).ToString(SaveOptions.DisableFormatting);

    /// <summary>
    /// After the driver pressed 1: a short greeting, then the recipient is dialed with the company number as caller id,
    /// a ring timeout and a hard time limit, through the recipient screening; a closing sentence when the dial ends.
    /// </summary>
    public static string Bridge(
        TwilioVoiceOptions options,
        VoicePhoneNumber companyNumber,
        VoicePhoneNumber recipient,
        Guid organizationId,
        Guid callRequestId) =>
        new XElement(
            "Response",
            Say(options, BridgeGreeting),
            new XElement(
                "Dial",
                new XAttribute("callerId", companyNumber.E164),
                new XAttribute("timeout", Seconds(options.RecipientRingSeconds)),
                new XAttribute("timeLimit", Seconds(options.MaximumCallSeconds)),
                new XAttribute("record", "do-not-record"),
                new XElement(
                    "Number",
                    new XAttribute("url", Url(options, VoiceWebhookPaths.RecipientScreen, organizationId, callRequestId)),
                    new XAttribute("method", "POST"),
                    recipient.E164)),
            Say(options, BridgeEnded)).ToString(SaveOptions.DisableFormatting);

    /// <summary>
    /// The recipient leg's screening (<c>&lt;Number url&gt;</c>): the neutral prompt and a one-digit gather posted to
    /// <see cref="VoiceWebhookPaths.RecipientConfirm"/>; a hang up if the gather ever falls through.
    /// </summary>
    public static string RecipientScreen(TwilioVoiceOptions options, Guid organizationId, Guid callRequestId) =>
        new XElement(
            "Response",
            Gather(
                options,
                options.RecipientConfirmSeconds,
                VoiceWebhookPaths.RecipientConfirm,
                organizationId,
                callRequestId,
                RecipientConfirmPrompt),
            new XElement("Hangup")).ToString(SaveOptions.DisableFormatting);

    /// <summary>An empty document: the screening ends and Twilio connects both legs.</summary>
    public static string Connect() => new XElement("Response").ToString(SaveOptions.DisableFormatting);

    public static string Hangup() =>
        new XElement("Response", new XElement("Hangup")).ToString(SaveOptions.DisableFormatting);

    /// <summary>
    /// A call to the company number: a fixed message and hang up, or a forward to the configured dispatch line with its
    /// own ring timeout and time limit (<c>InboundForward*</c>), never the bridged-call cost cap.
    /// </summary>
    public static string Inbound(TwilioVoiceOptions options, VoicePhoneNumber companyNumber, VoicePhoneNumber? forward) =>
        forward is null
            ? new XElement("Response", Say(options, InboundMessage), new XElement("Hangup"))
                .ToString(SaveOptions.DisableFormatting)
            : new XElement(
                    "Response",
                    Say(options, InboundForwardGreeting),
                    new XElement(
                        "Dial",
                        new XAttribute("callerId", companyNumber.E164),
                        new XAttribute("timeout", Seconds(options.InboundForwardRingSeconds)),
                        new XAttribute("timeLimit", Seconds(options.InboundForwardMaximumSeconds)),
                        new XAttribute("record", "do-not-record"),
                        new XElement("Number", forward.E164)))
                .ToString(SaveOptions.DisableFormatting);

    /// <summary>The webhook URL Twilio will call (and sign): the configured public origin, the path and the ids.</summary>
    internal static string Url(TwilioVoiceOptions options, string path, Guid organizationId, Guid callRequestId) =>
        options.WebhookBaseUri.TrimEnd('/') + VoiceWebhookPaths.PathAndQuery(path, organizationId, callRequestId);

    private static XElement Gather(
        TwilioVoiceOptions options,
        int timeoutSeconds,
        string path,
        Guid organizationId,
        Guid callRequestId,
        string prompt) =>
        new(
            "Gather",
            new XAttribute("input", "dtmf"),
            new XAttribute("numDigits", "1"),
            new XAttribute("timeout", Seconds(timeoutSeconds)),
            new XAttribute("action", Url(options, path, organizationId, callRequestId)),
            new XAttribute("method", "POST"),
            new XAttribute("actionOnEmptyResult", "true"),
            Say(options, prompt));

    private static XElement Say(TwilioVoiceOptions options, string text) =>
        new(
            "Say",
            new XAttribute("language", Language),
            new XAttribute("voice", options.SayVoice),
            text);

    private static string Seconds(int value) => value.ToString(CultureInfo.InvariantCulture);
}
