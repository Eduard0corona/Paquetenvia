using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Paqueteria.Application.Auditing;

public sealed record AuditRedactionOptions(int MaximumDepth = 8, int MaximumUtf8Bytes = 16_384)
{
    public AuditRedactionOptions Validate()
    {
        if (MaximumDepth is < 1 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumDepth));
        }

        if (MaximumUtf8Bytes is < 2 or > 1_048_576)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumUtf8Bytes));
        }

        return this;
    }
}

public sealed class RedactedAuditPayload
{
    internal RedactedAuditPayload(string json) => Json = json;

    public static RedactedAuditPayload Empty { get; } = new("{}");

    public string Json { get; }
}

public interface IAuditPayloadRedactor
{
    RedactedAuditPayload Redact(JsonElement payload);
}

public sealed partial class AuditPayloadRedactor : IAuditPayloadRedactor
{
    public const string Replacement = "[REDACTED]";

    private static readonly HashSet<string> SensitiveNames = new(StringComparer.Ordinal)
    {
        "address",
        "apikey",
        "authorization",
        "authorizationheader",
        "connectionstring",
        "cookie",
        "cookies",
        "email",
        "emailaddress",
        "familyname",
        "firstname",
        "fullname",
        "givenname",
        "identitysubject",
        "legalname",
        "lastname",
        "mobile",
        "msisdn",
        "name",
        "password",
        "passwd",
        "phonenumber",
        "phone",
        "privatekey",
        "refreshtoken",
        "secret",
        "subject",
        "streetaddress",
        "tel",
        "telephone",
        "telefono",
        "celular",
        "movil",
        "whatsapp",
        "token",
        "accesstoken",
    };

    private readonly AuditRedactionOptions options;

    public AuditPayloadRedactor()
        : this(new AuditRedactionOptions())
    {
    }

    public AuditPayloadRedactor(AuditRedactionOptions options) => this.options = options.Validate();

    public RedactedAuditPayload Redact(JsonElement payload)
    {
        try
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
            {
                Indented = false,
                SkipValidation = false,
            }))
            {
                WriteElement(writer, payload, 0);
            }

            if (buffer.WrittenCount > options.MaximumUtf8Bytes)
            {
                throw new AuditRedactionException();
            }

            return new RedactedAuditPayload(Encoding.UTF8.GetString(buffer.WrittenSpan));
        }
        catch (AuditRedactionException)
        {
            throw;
        }
        catch
        {
            throw new AuditRedactionException();
        }
    }

    private void WriteElement(Utf8JsonWriter writer, JsonElement element, int depth)
    {
        if (depth > options.MaximumDepth)
        {
            throw new AuditRedactionException();
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    if (IsSensitiveName(property.Name))
                    {
                        writer.WriteStringValue(Replacement);
                    }
                    else
                    {
                        WriteElement(writer, property.Value, depth + 1);
                    }
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteElement(writer, item, depth + 1);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                var value = element.GetString() ?? string.Empty;
                writer.WriteStringValue(IsSensitiveValue(value) ? Replacement : value);
                break;
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                element.WriteTo(writer);
                break;
            default:
                throw new AuditRedactionException();
        }
    }

    private static bool IsSensitiveName(string name)
    {
        var normalized = new string(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        return SensitiveNames.Contains(normalized) ||
            normalized.EndsWith("password", StringComparison.Ordinal) ||
            normalized.EndsWith("token", StringComparison.Ordinal) ||
            normalized.EndsWith("secret", StringComparison.Ordinal) ||
            normalized.EndsWith("cookie", StringComparison.Ordinal) ||
            normalized.EndsWith("email", StringComparison.Ordinal) ||
            normalized.EndsWith("phone", StringComparison.Ordinal) ||
            normalized.EndsWith("phonenumber", StringComparison.Ordinal) ||
            normalized.EndsWith("mobilenumber", StringComparison.Ordinal) ||
            normalized.StartsWith("telefono", StringComparison.Ordinal) ||
            normalized.EndsWith("telefono", StringComparison.Ordinal) ||
            normalized.EndsWith("celular", StringComparison.Ordinal) ||
            normalized.EndsWith("whatsappnumber", StringComparison.Ordinal) ||
            normalized.EndsWith("address", StringComparison.Ordinal) ||
            normalized.EndsWith("fullname", StringComparison.Ordinal);
    }

    private static bool IsSensitiveValue(string value) =>
        EmailPattern().IsMatch(value) ||
        ContainsPhoneNumber(value) ||
        JwtPattern().IsMatch(value) ||
        value.Contains("Bearer ", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("PRIVATE KEY", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("Password=", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("Connection String=", StringComparison.OrdinalIgnoreCase) ||
        AddressPattern().IsMatch(value) ||
        CredentialPattern().IsMatch(value) ||
        CiphertextPattern().IsMatch(value);

    /// <summary>
    /// Phone detection by shape. Well-formed UUIDs (8-4-4-4-12 hex) are removed from the text first, so a digit run
    /// inside an identifier never looks like a phone. What remains is redacted when it holds an E.164 number (a
    /// <c>+</c> followed by 8 to 15 digits with optional separators) or a Mexican 10-digit number with an optional
    /// <c>+52</c>/<c>52</c> and mobile <c>1</c> prefix and optional spaces, dashes, dots or parentheses. This applies
    /// under every key, identifier keys included. Boundaries are digits only, so a phone glued to letters is still
    /// found; as a result a non-UUID token that is mostly digits (a hex hash, base64) can be over-redacted, which is
    /// intentional. A bare run of digits of any other length is not a phone.
    /// </summary>
    private static bool ContainsPhoneNumber(string value)
    {
        if (value.Length < 8)
        {
            return false;
        }

        var text = UuidPattern().Replace(value, " ");
        return E164PhonePattern().IsMatch(text) || MexicanPhonePattern().IsMatch(text);
    }

    [GeneratedRegex(@"[^\s@]+@[^\s@]+\.[^\s@]+", RegexOptions.CultureInvariant)]
    private static partial Regex EmailPattern();

    [GeneratedRegex(
        @"(?<![0-9A-Fa-f])[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}(?![0-9A-Fa-f])",
        RegexOptions.CultureInvariant)]
    private static partial Regex UuidPattern();

    // Boundaries are digits only (and "+" before a number): a phone glued to a word ("llamar667-123-4567",
    // "6671234567antes") is still a phone. Only UUIDs are exempt; they are stripped before these patterns run.
    // "+" then 8 to 15 digits; between digits at most one separator (space, dot, dash) and optional parentheses.
    [GeneratedRegex(
        @"(?<![0-9+])\+[ ]?\(?[0-9](?:[ .\-]?\)?[ .\-]?\(?[0-9]){7,14}(?![0-9])",
        RegexOptions.CultureInvariant)]
    private static partial Regex E164PhonePattern();

    // Optional +52 / 52 and mobile 1, then exactly ten digits in the usual Mexican groupings:
    // 6671234567, 55 1234 5678, (667) 123-4567, 667.123.45.67, 66 71 23 45 67.
    [GeneratedRegex(
        @"(?<![0-9+])(?:\+?52[ .\-]?(?:1[ .\-]?)?)?" +
        @"(?:[0-9]{10}" +
        @"|(?:\([0-9]{2}\)|[0-9]{2})[ .\-]?[0-9]{4}[ .\-]?[0-9]{4}" +
        @"|(?:\([0-9]{3}\)|[0-9]{3})[ .\-]?[0-9]{3}[ .\-]?[0-9]{4}" +
        @"|(?:\([0-9]{3}\)|[0-9]{3})[ .\-]?[0-9]{3}([ .\-]?)[0-9]{2}\1[0-9]{2}" +
        @"|[0-9]{2}([ .\-]?)[0-9]{2}\2[0-9]{2}\2[0-9]{2}\2[0-9]{2})" +
        @"(?![0-9])",
        RegexOptions.CultureInvariant)]
    private static partial Regex MexicanPhonePattern();

    [GeneratedRegex(@"[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+", RegexOptions.CultureInvariant)]
    private static partial Regex JwtPattern();

    [GeneratedRegex(
        @"\b(?:calle|avenida|av\.?|boulevard|blvd\.?|carretera|camino|street|st\.?|avenue|ave\.?|road|rd\.?)\s+[\p{L}0-9][\p{L}0-9 .#-]{2,}",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex AddressPattern();

    [GeneratedRegex(
        @"\b(?:access[_ -]?token|api[_ -]?key|client[_ -]?secret|password|refresh[_ -]?token|secret|token)\s*[:=]\s*\S+",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex CredentialPattern();

    [GeneratedRegex(@"\b(?:ciphertext|encrypted[_ -]?value)\s*[:=]\s*\S+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex CiphertextPattern();
}

public sealed class AuditRedactionException : Exception
{
    public AuditRedactionException()
        : base("The audit payload could not be redacted safely.")
    {
    }
}
