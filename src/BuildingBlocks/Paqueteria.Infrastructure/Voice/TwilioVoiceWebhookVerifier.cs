using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Paqueteria.Application.Voice;

namespace Paqueteria.Infrastructure.Voice;

/// <summary>
/// Twilio request validation: <c>X-Twilio-Signature</c> is the Base64 HMAC-SHA1, keyed with the account auth token,
/// of the full URL Twilio requested followed by every POST field name and value, sorted by name. The URL is rebuilt
/// from the configured <c>WebhookBaseUri</c> and the request path and query, never from <c>Host</c> or
/// <c>X-Forwarded-*</c>. The form must also name the configured account. Values are used only for the digest.
/// </summary>
internal sealed class TwilioVoiceWebhookVerifier(IOptions<VoiceBridgeOptions> options) : IVoiceWebhookVerifier
{
    private const int MaximumSignatureLength = 256;

    public VoiceWebhookVerdict Verify(VoiceWebhookRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var configured = options.Value;
        if (configured.Provider != VoiceBridgeProviderKind.Twilio)
        {
            return VoiceWebhookVerdict.NotConfigured;
        }

        var twilio = configured.Twilio;
        if (string.IsNullOrEmpty(request.Signature) || request.Signature.Length > MaximumSignatureLength ||
            string.IsNullOrEmpty(request.PathAndQuery) || request.PathAndQuery[0] != '/')
        {
            return VoiceWebhookVerdict.Rejected;
        }

        var accounts = request.Form.Where(field => string.Equals(field.Key, "AccountSid", StringComparison.Ordinal))
            .Select(field => field.Value)
            .ToArray();
        if (accounts.Length != 1 || !string.Equals(accounts[0], twilio.AccountSid, StringComparison.Ordinal))
        {
            return VoiceWebhookVerdict.Rejected;
        }

        var expected = ComputeSignature(
            twilio.AuthToken,
            twilio.WebhookBaseUri.TrimEnd('/') + request.PathAndQuery,
            request.Form);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected),
            Encoding.ASCII.GetBytes(request.Signature))
            ? VoiceWebhookVerdict.Verified
            : VoiceWebhookVerdict.Rejected;
    }

    public string BuildInboundCallResponse()
    {
        var twilio = options.Value.Twilio;
        if (!VoicePhoneNumber.TryParseE164(twilio.CompanyNumber, out var company))
        {
            throw new InvalidOperationException("The company number is not configured.");
        }

        VoicePhoneNumber? forward = VoicePhoneNumber.TryParseE164(twilio.InboundForwardNumber, out var parsed)
            ? parsed
            : null;
        return TwilioTwiml.Inbound(twilio, company, forward);
    }

    /// <summary>The Twilio signature algorithm (also used by the tests to sign synthetic callbacks).</summary>
    internal static string ComputeSignature(
        string authToken,
        string url,
        IReadOnlyList<KeyValuePair<string, string>> form)
    {
        var data = new StringBuilder(url);
        foreach (var group in form
                     .GroupBy(field => field.Key, StringComparer.Ordinal)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            foreach (var value in group.Select(field => field.Value).Distinct(StringComparer.Ordinal)
                         .Order(StringComparer.Ordinal))
            {
                data.Append(group.Key).Append(value);
            }
        }

        var key = Encoding.UTF8.GetBytes(authToken);
        var bytes = Encoding.UTF8.GetBytes(data.ToString());
        try
        {
#pragma warning disable CA5350 // Twilio's request validation is defined as HMAC-SHA1; it is not a choice of ours.
            return Convert.ToBase64String(HMACSHA1.HashData(key, bytes));
#pragma warning restore CA5350
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }
}
