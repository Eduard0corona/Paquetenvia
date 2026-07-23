using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace Paqueteria.Contracts.Legal;

public sealed record OrderAcceptanceEvidence(
    Guid OrderId,
    Guid QuoteId,
    Guid OwnerOrganizationId,
    Guid? ActorId,
    string TermsVersion,
    string PrivacyVersion,
    DateTimeOffset AcceptedAtClient,
    string AcceptanceChannel);

public static class OrderAcceptanceCanonicalizer
{
    public const string SchemaVersion = "order-acceptance-v1";

    public static ReadOnlyMemory<byte> Canonicalize(OrderAcceptanceEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidence.TermsVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidence.PrivacyVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidence.AcceptanceChannel);

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteString("schema_version", SchemaVersion);
            writer.WriteString("order_id", Format(evidence.OrderId));
            writer.WriteString("quote_id", Format(evidence.QuoteId));
            writer.WriteString("owner_org_id", Format(evidence.OwnerOrganizationId));
            if (evidence.ActorId is { } actorId)
            {
                writer.WriteString("actor_id", Format(actorId));
            }
            else
            {
                writer.WriteNull("actor_id");
            }

            writer.WriteString("terms_version", evidence.TermsVersion);
            writer.WriteString("privacy_version", evidence.PrivacyVersion);
            writer.WriteString(
                "accepted_at_client",
                evidence.AcceptedAtClient.UtcDateTime.ToString(
                    "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
                    CultureInfo.InvariantCulture));
            writer.WriteString("acceptance_channel", evidence.AcceptanceChannel.ToUpperInvariant());
            writer.WriteEndObject();
        }

        return buffer.WrittenMemory.ToArray();
    }

    public static byte[] ComputeSha256(OrderAcceptanceEvidence evidence) =>
        SHA256.HashData(Canonicalize(evidence).Span);

    private static string Format(Guid value) =>
        value.ToString("D", CultureInfo.InvariantCulture).ToLowerInvariant();
}
