using System.Net.Mail;
using System.Text.RegularExpressions;
using Paqueteria.Application.Messaging;

namespace Paqueteria.Infrastructure.Messaging;

public enum WhatsAppProviderKind
{
    Disabled,
    Synthetic,
    MetaCloudApi,
}

public enum EmailProviderKind
{
    Disabled,
    Synthetic,
    AzureCommunicationServices,
}

/// <summary>
/// <c>Messaging</c>: GATE-004-CHANNELS provider selection. Every channel is <c>Disabled</c> unless the
/// deployment names a provider. Secrets (the Meta access token and phone number id) arrive through
/// the allowlisted <c>KeyVaultSecrets:Mappings</c> source; ACS uses the workload managed identity.
/// Template names, languages, subjects and texts are owner decisions and have no defaults.
/// </summary>
public sealed class MessagingOptions
{
    public const string SectionName = "Messaging";

    public WhatsAppChannelOptions WhatsApp { get; set; } = new();

    public EmailChannelOptions Email { get; set; } = new();
}

public sealed class WhatsAppChannelOptions
{
    public WhatsAppProviderKind Provider { get; set; }

    public MessagingOutcome SyntheticOutcome { get; set; } = MessagingOutcome.Accepted;

    public MetaCloudApiOptions MetaCloudApi { get; set; } = new();

    /// <summary>Logical template key → owner-approved Meta template.</summary>
    public Dictionary<string, WhatsAppTemplateOptions> Templates { get; set; } = new(StringComparer.Ordinal);
}

public sealed class MetaCloudApiOptions
{
    public string BaseUri { get; set; } = "https://graph.facebook.com";

    /// <summary>Graph API version segment, for example <c>v23.0</c>.</summary>
    public string ApiVersion { get; set; } = "v23.0";

    /// <summary>WhatsApp Business phone number id. Key Vault mapping.</summary>
    public string PhoneNumberId { get; set; } = string.Empty;

    /// <summary>System-user access token. Key Vault mapping; never logged.</summary>
    public string AccessToken { get; set; } = string.Empty;

    public int TimeoutSeconds { get; set; } = 10;
}

public sealed class WhatsAppTemplateOptions
{
    /// <summary>Approved template name in WhatsApp Manager.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Template language code as approved, for example <c>es_MX</c>.</summary>
    public string LanguageCode { get; set; } = string.Empty;

    /// <summary>Number of body parameters (<c>{{1}}</c>…) the approved template declares.</summary>
    public int ParameterCount { get; set; }
}

public sealed class EmailChannelOptions
{
    public EmailProviderKind Provider { get; set; }

    public MessagingOutcome SyntheticOutcome { get; set; } = MessagingOutcome.Accepted;

    public AzureCommunicationEmailOptions AzureCommunicationServices { get; set; } = new();

    /// <summary>Logical template key → owner-approved subject and plain-text body.</summary>
    public Dictionary<string, EmailTemplateOptions> Templates { get; set; } = new(StringComparer.Ordinal);
}

public sealed class AzureCommunicationEmailOptions
{
    /// <summary><c>https://&lt;resource&gt;.&lt;region&gt;.communication.azure.com</c>, no path.</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>Verified sender (MailFrom) address on the owner's verified domain.</summary>
    public string SenderAddress { get; set; } = string.Empty;

    public string ApiVersion { get; set; } = "2023-03-31";

    public int TimeoutSeconds { get; set; } = 15;
}

public sealed class EmailTemplateOptions
{
    /// <summary>Subject with positional placeholders <c>{{1}}</c>…; owner-provided text.</summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>Plain-text body with positional placeholders <c>{{1}}</c>…; owner-provided text.</summary>
    public string PlainText { get; set; } = string.Empty;

    public int ParameterCount { get; set; }
}

internal static partial class MessagingOptionsValidator
{
    public const int MaximumParameters = 20;

    /// <summary>Returns the failures; never includes a configured value.</summary>
    public static IReadOnlyList<string> Validate(MessagingOptions options, bool syntheticAllowed)
    {
        var failures = new List<string>();
        var whatsApp = options.WhatsApp;
        if (!Enum.IsDefined(whatsApp.Provider) || !Enum.IsDefined(whatsApp.SyntheticOutcome))
        {
            failures.Add("Messaging:WhatsApp:Provider or SyntheticOutcome is not a known value.");
        }

        if (whatsApp.Provider == WhatsAppProviderKind.Synthetic && !syntheticAllowed)
        {
            failures.Add("Messaging:WhatsApp:Provider=Synthetic is allowed only in Development, Testing or DEV_SYNTHETIC.");
        }

        if (whatsApp.Provider == WhatsAppProviderKind.MetaCloudApi)
        {
            var meta = whatsApp.MetaCloudApi;
            if (!IsHttpsRoot(meta.BaseUri))
            {
                failures.Add("Messaging:WhatsApp:MetaCloudApi:BaseUri must be an https URI without path, query or credentials.");
            }

            if (!ApiVersionPattern().IsMatch(meta.ApiVersion ?? string.Empty))
            {
                failures.Add("Messaging:WhatsApp:MetaCloudApi:ApiVersion must look like v23.0.");
            }

            if (!PhoneNumberIdPattern().IsMatch(meta.PhoneNumberId ?? string.Empty))
            {
                failures.Add("Messaging:WhatsApp:MetaCloudApi:PhoneNumberId is missing or not numeric (map it from Key Vault).");
            }

            if (string.IsNullOrWhiteSpace(meta.AccessToken) || meta.AccessToken.Any(char.IsWhiteSpace))
            {
                failures.Add("Messaging:WhatsApp:MetaCloudApi:AccessToken is missing or malformed (map it from Key Vault).");
            }

            if (meta.TimeoutSeconds is < 1 or > 60)
            {
                failures.Add("Messaging:WhatsApp:MetaCloudApi:TimeoutSeconds must be between 1 and 60.");
            }
        }

        foreach (var (key, template) in whatsApp.Templates)
        {
            if (!TemplateKeyPattern().IsMatch(key) || template is null ||
                !WhatsAppTemplateNamePattern().IsMatch(template.Name ?? string.Empty) ||
                !LanguageCodePattern().IsMatch(template.LanguageCode ?? string.Empty) ||
                template.ParameterCount is < 0 or > MaximumParameters)
            {
                failures.Add($"Messaging:WhatsApp:Templates entry '{SafeKey(key)}' needs Name ([a-z0-9_]), LanguageCode (es_MX) and ParameterCount 0-{MaximumParameters}.");
            }
        }

        var email = options.Email;
        if (!Enum.IsDefined(email.Provider) || !Enum.IsDefined(email.SyntheticOutcome))
        {
            failures.Add("Messaging:Email:Provider or SyntheticOutcome is not a known value.");
        }

        if (email.Provider == EmailProviderKind.Synthetic && !syntheticAllowed)
        {
            failures.Add("Messaging:Email:Provider=Synthetic is allowed only in Development, Testing or DEV_SYNTHETIC.");
        }

        if (email.Provider == EmailProviderKind.AzureCommunicationServices)
        {
            var acs = email.AzureCommunicationServices;
            if (!IsHttpsRoot(acs.Endpoint))
            {
                failures.Add("Messaging:Email:AzureCommunicationServices:Endpoint must be an https URI without path, query or credentials.");
            }

            if (!IsEmailAddress(acs.SenderAddress))
            {
                failures.Add("Messaging:Email:AzureCommunicationServices:SenderAddress must be a single email address.");
            }

            if (!AcsApiVersionPattern().IsMatch(acs.ApiVersion ?? string.Empty))
            {
                failures.Add("Messaging:Email:AzureCommunicationServices:ApiVersion must look like 2023-03-31.");
            }

            if (acs.TimeoutSeconds is < 1 or > 60)
            {
                failures.Add("Messaging:Email:AzureCommunicationServices:TimeoutSeconds must be between 1 and 60.");
            }
        }

        foreach (var (key, template) in email.Templates)
        {
            if (!TemplateKeyPattern().IsMatch(key) || template is null ||
                string.IsNullOrWhiteSpace(template.Subject) || template.Subject.Length > 998 ||
                template.Subject.Any(char.IsControl) ||
                string.IsNullOrWhiteSpace(template.PlainText) ||
                template.ParameterCount is < 0 or > MaximumParameters ||
                !PlaceholdersMatch(template.Subject + "\n" + template.PlainText, template.ParameterCount))
            {
                failures.Add($"Messaging:Email:Templates entry '{SafeKey(key)}' needs a single-line Subject, a PlainText and placeholders {{{{1}}}}..{{{{ParameterCount}}}} only.");
            }
        }

        return failures;
    }

    internal static bool IsEmailAddress(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 254 || value.Any(char.IsControl) ||
            value.Contains(',', StringComparison.Ordinal) || value.Contains(';', StringComparison.Ordinal))
        {
            return false;
        }

        return MailAddress.TryCreate(value.Trim(), out var parsed) &&
            string.Equals(parsed.Address, value.Trim(), StringComparison.Ordinal) &&
            string.IsNullOrEmpty(parsed.DisplayName);
    }

    /// <summary>The template uses exactly {{1}}..{{count}} and no other placeholder.</summary>
    internal static bool PlaceholdersMatch(string text, int count)
    {
        var used = PlaceholderPattern().Matches(text)
            .Select(match => int.TryParse(match.Groups[1].Value, out var index) ? index : -1)
            .ToHashSet();
        return used.SetEquals(Enumerable.Range(1, count)) &&
            !PlaceholderPattern().Replace(text, string.Empty).Contains("{{", StringComparison.Ordinal);
    }

    private static bool IsHttpsRoot(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        uri.AbsolutePath == "/" &&
        string.IsNullOrEmpty(uri.Query) &&
        string.IsNullOrEmpty(uri.Fragment);

    private static string SafeKey(string key) => TemplateKeyPattern().IsMatch(key) ? key : "(invalid key)";

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,99}$", RegexOptions.CultureInvariant)]
    internal static partial Regex TemplateKeyPattern();

    [GeneratedRegex("^[a-z0-9_]{1,512}$", RegexOptions.CultureInvariant)]
    private static partial Regex WhatsAppTemplateNamePattern();

    [GeneratedRegex("^[a-z]{2,3}(_[A-Z]{2})?$", RegexOptions.CultureInvariant)]
    private static partial Regex LanguageCodePattern();

    [GeneratedRegex(@"^v\d{1,3}\.\d{1,3}$", RegexOptions.CultureInvariant)]
    private static partial Regex ApiVersionPattern();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}(-preview)?$", RegexOptions.CultureInvariant)]
    private static partial Regex AcsApiVersionPattern();

    [GeneratedRegex(@"^\d{5,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex PhoneNumberIdPattern();

    [GeneratedRegex(@"\{\{(\d{1,2})\}\}", RegexOptions.CultureInvariant)]
    internal static partial Regex PlaceholderPattern();
}
