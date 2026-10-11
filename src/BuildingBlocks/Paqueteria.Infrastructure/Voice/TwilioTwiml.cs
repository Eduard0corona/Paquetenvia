using System.Globalization;
using System.Xml.Linq;
using Paqueteria.Application.Voice;

namespace Paqueteria.Infrastructure.Voice;

/// <summary>
/// VOICE-001 TwiML. Every message is a fixed Spanish sentence without personal data, brand or order data; numbers
/// appear only as dial targets and caller id. Nothing is recorded (<c>record="do-not-record"</c>). Built with
/// <see cref="XElement"/> so every attribute and text node is escaped.
/// </summary>
internal static class TwilioTwiml
{
    public const string Language = "es-MX";

    public const string BridgeGreeting =
        "Te comunicamos con el destinatario de tu entrega. Espera en la línea.";

    public const string BridgeEnded = "La llamada con el destinatario terminó.";

    public const string InboundMessage =
        "Gracias por llamar. Este número solo se usa para comunicar a los repartidores con los destinatarios de sus " +
        "entregas y no recibe llamadas. Para dudas sobre tu envío, usa el enlace de seguimiento de tu guía.";

    public const string InboundForwardGreeting = "Gracias por llamar. Te comunicamos con nuestro equipo.";

    /// <summary>
    /// What the driver hears after answering: a short greeting, then a dial to the recipient with the company number
    /// as caller id, a ring timeout and a hard time limit; a closing sentence when the dial ends.
    /// </summary>
    public static string Bridge(TwilioVoiceOptions options, VoicePhoneNumber companyNumber, VoicePhoneNumber recipient) =>
        new XElement(
            "Response",
            Say(options, BridgeGreeting),
            Dial(options, companyNumber, recipient, options.RecipientRingSeconds),
            Say(options, BridgeEnded)).ToString(SaveOptions.DisableFormatting);

    /// <summary>A call to the company number: forward to the configured dispatch line, or a message and hang up.</summary>
    public static string Inbound(TwilioVoiceOptions options, VoicePhoneNumber companyNumber, VoicePhoneNumber? forward) =>
        forward is null
            ? new XElement("Response", Say(options, InboundMessage), new XElement("Hangup"))
                .ToString(SaveOptions.DisableFormatting)
            : new XElement(
                    "Response",
                    Say(options, InboundForwardGreeting),
                    Dial(options, companyNumber, forward, options.RecipientRingSeconds))
                .ToString(SaveOptions.DisableFormatting);

    private static XElement Say(TwilioVoiceOptions options, string text) =>
        new(
            "Say",
            new XAttribute("language", Language),
            new XAttribute("voice", options.SayVoice),
            text);

    private static XElement Dial(
        TwilioVoiceOptions options,
        VoicePhoneNumber callerId,
        VoicePhoneNumber target,
        int ringSeconds) =>
        new(
            "Dial",
            new XAttribute("callerId", callerId.E164),
            new XAttribute("timeout", ringSeconds.ToString(CultureInfo.InvariantCulture)),
            new XAttribute("timeLimit", options.MaximumCallSeconds.ToString(CultureInfo.InvariantCulture)),
            new XAttribute("record", "do-not-record"),
            new XElement("Number", target.E164));
}
