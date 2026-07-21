using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace Paqueteria.ContractTests.Cryptography;

internal sealed record OrderAcceptanceEvidence(
    Guid OrderId,
    Guid QuoteId,
    Guid OwnerOrganizationId,
    Guid ActorId,
    string TermsVersion,
    string PrivacyVersion,
    DateTimeOffset AcceptedAtClient,
    string AcceptanceChannel);

internal static class OrderAcceptanceCanonicalizer
{
    public const string SchemaVersion = "order-acceptance-v1";

    public static byte[] Canonicalize(OrderAcceptanceEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteString("schema_version", SchemaVersion);
            writer.WriteString("order_id", evidence.OrderId.ToString("D", CultureInfo.InvariantCulture).ToLowerInvariant());
            writer.WriteString("quote_id", evidence.QuoteId.ToString("D", CultureInfo.InvariantCulture).ToLowerInvariant());
            writer.WriteString("owner_org_id", evidence.OwnerOrganizationId.ToString("D", CultureInfo.InvariantCulture).ToLowerInvariant());
            writer.WriteString("actor_id", evidence.ActorId.ToString("D", CultureInfo.InvariantCulture).ToLowerInvariant());
            writer.WriteString("terms_version", evidence.TermsVersion);
            writer.WriteString("privacy_version", evidence.PrivacyVersion);
            writer.WriteString(
                "accepted_at_client",
                evidence.AcceptedAtClient.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture));
            writer.WriteString("acceptance_channel", evidence.AcceptanceChannel.ToUpperInvariant());
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    public static byte[] Hash(OrderAcceptanceEvidence evidence) => SHA256.HashData(Canonicalize(evidence));
}
