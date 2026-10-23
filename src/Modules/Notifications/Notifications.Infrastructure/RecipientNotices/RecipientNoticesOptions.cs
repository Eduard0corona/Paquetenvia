using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Notifications.Application.RecipientNotices;
using Paqueteria.Application.RecipientNotices;
using Orders.Application.RecipientNotices;
using Orders.Application.Tracking;
using Paqueteria.Application.Messaging;
using Paqueteria.Application.Security;
using Paqueteria.Infrastructure.Messaging;

namespace Notifications.Infrastructure.RecipientNotices;

/// <summary>
/// <c>Notifications:RecipientNotices</c> (AI-24 <c>recipient_notices.switches</c>, <c>quiet_hours</c>, <c>staleness</c>
/// and <c>caps</c>; AI-08 NTF-005). Off by default: while <see cref="Enabled"/> is false every request row ends PROCESSED
/// with <c>RECIPIENT_NOTICES_DISABLED</c> and every notice already created ends FAILED with
/// <c>MESSAGING_CHANNEL_DISABLED</c>, with no read, decryption or provider call. Only the gate-closing change sets it.
/// </summary>
public sealed class RecipientNoticesOptions
{
    public const string SectionName = "Notifications:RecipientNotices";

    public bool Enabled { get; set; }

    public RecipientNoticeQuietHoursOptions QuietHours { get; set; } = new();

    /// <summary>The stale bound, 1 to 24 hours, counted from occurred_at without the quiet-hours wait.</summary>
    public int MaximumNoticeDelayHours { get; set; } = 12;

    /// <summary>NTF-RECIPIENT-NOTICE-CAPS-2026-10-23: notices per order and channel whose provider was called.</summary>
    public int MaximumPerOrderAndChannel { get; set; } = 8;

    /// <summary>NTF-RECIPIENT-NOTICE-CAPS-2026-10-23: notices per channel and Mazatlan day on the whole platform.</summary>
    public int DailyLimitPerChannel { get; set; } = 500;

    /// <summary>The decision-log row that resolves GATE-004 (for example <c>GATE-004-...</c>); format only.</summary>
    public string Gate004DecisionId { get; set; } = string.Empty;

    /// <summary>The decision-log row that resolves GATE-007 (for example <c>GATE-007-...</c>); format only.</summary>
    public string Gate007DecisionId { get; set; } = string.Empty;

    public TimeSpan MaximumNoticeDelay => TimeSpan.FromHours(MaximumNoticeDelayHours);
}

/// <summary>AI-24 <c>recipient_notices.quiet_hours.settings</c>: Start (08:00) and End (21:00), America/Mazatlan.</summary>
public sealed class RecipientNoticeQuietHoursOptions
{
    public string Start { get; set; } = "08:00";

    public string End { get; set; } = "21:00";

    public bool TryParse(out TimeOnly start, out TimeOnly end)
    {
        end = default;
        return TryParseTime(Start, out start) & TryParseTime(End, out end) && start < end;
    }

    public RecipientNoticeQuietHours ToWindow() =>
        TryParse(out var start, out var end)
            ? new RecipientNoticeQuietHours(start, end)
            : throw new InvalidOperationException("Notifications:RecipientNotices:QuietHours is invalid.");

    private static bool TryParseTime(string? value, out TimeOnly time) =>
        TimeOnly.TryParseExact(value, ["HH:mm", "HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out time);
}

/// <summary>
/// Start validation of recipient notices (AI-24 <c>recipient_notices.switches</c> and <c>tracking_token.fail_closed</c>;
/// NTF-RECIPIENT-NOTICE-CAPS-2026-10-23 L4 choices 4 and 10). Real providers (Meta Cloud API, Azure Communication
/// Services) are refused in Development, Testing, DevSynthetic and any DEV_SYNTHETIC deployment, and anywhere unless
/// both gate decision ids name decision-log rows (checked by format, like <c>Voice:Twilio:Gate007DecisionId</c>).
/// Enabled requires the readers, the contact lookup key, a tracking link (configured, not synthetic, with a real
/// provider) and, for each channel whose provider is not Disabled, the recipient templates exactly as AI-24 records them.
/// No failure message carries a configured value.
/// </summary>
internal sealed partial class RecipientNoticesOptionsValidation(
    IOptions<MessagingOptions> messaging,
    IOptions<PublicTrackingOptions> tracking,
    IHostEnvironment environment,
    IRecipientContactLookup contactLookup,
    IRecipientNoticeTrackingLinks trackingLinks,
    IRecipientNoticeOrderReader orders,
    IRecipientNoticeContactReader contacts) : IValidateOptions<RecipientNoticesOptions>
{
    public ValidateOptionsResult Validate(string? name, RecipientNoticesOptions options)
    {
        var failures = new List<string>();
        if (!options.QuietHours.TryParse(out _, out _))
        {
            failures.Add("Notifications:RecipientNotices:QuietHours needs Start and End as HH:mm or HH:mm:ss with Start before End.");
        }

        if (options.MaximumNoticeDelayHours is < 1 or > 24 ||
            options.MaximumPerOrderAndChannel is < 1 or > 100 ||
            options.DailyLimitPerChannel is < 1 or > 100_000)
        {
            failures.Add("Notifications:RecipientNotices needs MaximumNoticeDelayHours 1-24, MaximumPerOrderAndChannel 1-100 and DailyLimitPerChannel 1-100000.");
        }

        var settings = messaging.Value;
        var realProvider = RecipientNoticeProviders.IsReal(settings, MessagingChannel.WhatsApp) ||
            RecipientNoticeProviders.IsReal(settings, MessagingChannel.Email);
        if (realProvider)
        {
            if (!IsLiveAllowed(environment))
            {
                failures.Add("A real messaging provider for recipient notices is refused in Development, Testing and DEV_SYNTHETIC.");
            }

            if (!Gate004Pattern().IsMatch(options.Gate004DecisionId ?? string.Empty) ||
                !Gate007Pattern().IsMatch(options.Gate007DecisionId ?? string.Empty))
            {
                failures.Add("A real messaging provider for recipient notices requires Notifications:RecipientNotices:Gate004DecisionId and Gate007DecisionId naming the decision-log rows that resolve GATE-004 and GATE-007.");
            }
        }

        foreach (var channel in new[] { MessagingChannel.WhatsApp, MessagingChannel.Email })
        {
            failures.AddRange(RecipientNoticeTemplates.Validate(settings, channel, options.Enabled && !RecipientNoticeProviders.IsDisabled(settings, channel)));
        }

        if (options.Enabled)
        {
            if (orders is UnavailableRecipientNoticeReaders || contacts is UnavailableRecipientNoticeReaders ||
                trackingLinks is UnavailableRecipientNoticeReaders)
            {
                failures.Add("Notifications:RecipientNotices:Enabled requires the Orders and Locations recipient notice readers.");
            }

            if (!contactLookup.IsAvailable)
            {
                failures.Add("Notifications:RecipientNotices:Enabled requires ContactLookup:Keys (the opt-out check and the unsubscribe link).");
            }

            var link = trackingLinks.Readiness;
            if (link == RecipientNoticeLinkReadiness.Unavailable ||
                (realProvider && link != RecipientNoticeLinkReadiness.Configured))
            {
                failures.Add("Notifications:RecipientNotices:Enabled requires PublicTracking:PublicBaseUrl and a valid PublicTracking:LinkKeys key ring (a configured key with a real provider).");
            }

            // AI-24 recipient_notices.templates.email.headers: the one-click List-Unsubscribe URL is https (RFC 8058),
            // so an e-mail channel that sends needs an https public origin even where http loopback is allowed.
            if (!RecipientNoticeProviders.IsDisabled(settings, MessagingChannel.Email) &&
                !(Uri.TryCreate(tracking.Value.PublicBaseUrl, UriKind.Absolute, out var origin) &&
                  origin.Scheme == Uri.UriSchemeHttps))
            {
                failures.Add("Notifications:RecipientNotices:Enabled with an e-mail provider requires an https PublicTracking:PublicBaseUrl (one-click unsubscribe, RFC 8058).");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    /// <summary>The Voice live-provider rule: never where the data is synthetic.</summary>
    internal static bool IsLiveAllowed(IHostEnvironment environment) =>
        !environment.IsDevelopment() &&
        !environment.IsEnvironment("Testing") &&
        !environment.IsEnvironment(SyntheticEnvironmentPolicy.EnvironmentName) &&
        !string.Equals(
            Environment.GetEnvironmentVariable(SyntheticEnvironmentPolicy.DeploymentClassVariable),
            SyntheticEnvironmentPolicy.DeploymentClass,
            StringComparison.Ordinal);

    [GeneratedRegex("^GATE-004-[A-Z0-9][A-Z0-9-]{2,76}$", RegexOptions.CultureInvariant)]
    internal static partial Regex Gate004Pattern();

    [GeneratedRegex("^GATE-007-[A-Z0-9][A-Z0-9-]{2,76}$", RegexOptions.CultureInvariant)]
    internal static partial Regex Gate007Pattern();
}

/// <summary>The provider kind of each recipient notice channel.</summary>
internal static class RecipientNoticeProviders
{
    public static bool IsDisabled(MessagingOptions options, MessagingChannel channel) => channel switch
    {
        MessagingChannel.WhatsApp => options.WhatsApp.Provider == WhatsAppProviderKind.Disabled,
        MessagingChannel.Email => options.Email.Provider == EmailProviderKind.Disabled,
        _ => true,
    };

    public static bool IsReal(MessagingOptions options, MessagingChannel channel) => channel switch
    {
        MessagingChannel.WhatsApp => options.WhatsApp.Provider == WhatsAppProviderKind.MetaCloudApi,
        MessagingChannel.Email => options.Email.Provider == EmailProviderKind.AzureCommunicationServices,
        _ => false,
    };

    public static bool IsSynthetic(MessagingOptions options, MessagingChannel channel) => channel switch
    {
        MessagingChannel.WhatsApp => options.WhatsApp.Provider == WhatsAppProviderKind.Synthetic,
        MessagingChannel.Email => options.Email.Provider == EmailProviderKind.Synthetic,
        _ => false,
    };
}

/// <summary>
/// AI-24 <c>recipient_notices.templates</c>: the configured recipient templates must be the AI-24 ones. A WhatsApp
/// template names the approved template with <c>es_MX</c> and one body parameter; an e-mail template is exactly the
/// subject and plain text of <see cref="RecipientNoticeTexts"/> with three parameters. When <c>required</c> (the channel
/// sends recipient notices) all four must be configured.
/// </summary>
internal static class RecipientNoticeTemplates
{
    public static IEnumerable<string> Validate(MessagingOptions options, MessagingChannel channel, bool required)
    {
        foreach (var kind in RecipientNoticeCatalog.All)
        {
            var key = RecipientNoticeCatalog.TemplateKey(kind);
            if (channel == MessagingChannel.WhatsApp)
            {
                if (!options.WhatsApp.Templates.TryGetValue(key, out var template))
                {
                    if (required)
                    {
                        yield return $"Messaging:WhatsApp:Templates:{key} is required while recipient notices are enabled.";
                    }

                    continue;
                }

                if (template.LanguageCode != RecipientNoticeTexts.WhatsAppLanguage ||
                    template.ParameterCount != RecipientNoticeTexts.WhatsAppParameterCount)
                {
                    yield return $"Messaging:WhatsApp:Templates:{key} must use LanguageCode es_MX and ParameterCount 1 (AI-24 recipient_notices.templates).";
                }
            }
            else
            {
                if (!options.Email.Templates.TryGetValue(key, out var template))
                {
                    if (required)
                    {
                        yield return $"Messaging:Email:Templates:{key} is required while recipient notices are enabled.";
                    }

                    continue;
                }

                if (template.Subject != RecipientNoticeTexts.EmailSubject(kind) ||
                    template.PlainText.ReplaceLineEndings("\n") != RecipientNoticeTexts.EmailPlainText(kind) ||
                    template.ParameterCount != RecipientNoticeTexts.EmailParameterCount)
                {
                    yield return $"Messaging:Email:Templates:{key} must be the AI-24 recipient_notices.templates subject and plain text with ParameterCount 3.";
                }
            }
        }
    }
}
