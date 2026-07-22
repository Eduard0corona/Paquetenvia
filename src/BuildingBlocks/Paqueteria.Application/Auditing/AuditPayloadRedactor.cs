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
        "telephone",
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
            normalized.EndsWith("address", StringComparison.Ordinal) ||
            normalized.EndsWith("fullname", StringComparison.Ordinal);
    }

    private static bool IsSensitiveValue(string value) =>
        EmailPattern().IsMatch(value) ||
        PhonePattern().IsMatch(value) ||
        JwtPattern().IsMatch(value) ||
        value.Contains("Bearer ", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("PRIVATE KEY", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("Password=", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("Connection String=", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^[^\s@]+@[^\s@]+\.[^\s@]+$", RegexOptions.CultureInvariant)]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"^\+?[0-9][0-9 ()-]{6,}[0-9]$", RegexOptions.CultureInvariant)]
    private static partial Regex PhonePattern();

    [GeneratedRegex(@"^[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex JwtPattern();
}

public sealed class AuditRedactionException : Exception
{
    public AuditRedactionException()
        : base("The audit payload could not be redacted safely.")
    {
    }
}
