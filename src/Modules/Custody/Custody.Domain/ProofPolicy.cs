using System.Security.Cryptography;
using System.Text;

namespace Custody.Domain;

public enum ProofType
{
    PickupPhoto,
    DeliveryPhoto,
    Signature,
    DeliveryCode,
    ReturnPhoto,
}

public enum ProofUploadSessionStatus
{
    Created,
    Uploaded,
    Validating,
    Ready,
    Rejected,
    Expired,
    Consumed,
}

public static class ProofContract
{
    public const string PickupPhoto = "PICKUP_PHOTO";
    public const string DeliveryPhoto = "DELIVERY_PHOTO";
    public const string Signature = "SIGNATURE";
    public const string DeliveryCode = "DELIVERY_CODE";
    public const string ReturnPhoto = "RETURN_PHOTO";

    public static bool TryParse(string? value, out ProofType type)
    {
        type = value switch
        {
            PickupPhoto => ProofType.PickupPhoto,
            DeliveryPhoto => ProofType.DeliveryPhoto,
            Signature => ProofType.Signature,
            DeliveryCode => ProofType.DeliveryCode,
            ReturnPhoto => ProofType.ReturnPhoto,
            _ => default,
        };
        return value is PickupPhoto or DeliveryPhoto or Signature or DeliveryCode or ReturnPhoto;
    }

    public static string ToContractValue(this ProofType type) => type switch
    {
        ProofType.PickupPhoto => PickupPhoto,
        ProofType.DeliveryPhoto => DeliveryPhoto,
        ProofType.Signature => Signature,
        ProofType.DeliveryCode => DeliveryCode,
        ProofType.ReturnPhoto => ReturnPhoto,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown proof type."),
    };

    public static bool IsAllowedOrderState(ProofType type, string orderStatus) => type switch
    {
        ProofType.PickupPhoto => orderStatus == "AT_PICKUP",
        ProofType.DeliveryPhoto or ProofType.Signature or ProofType.DeliveryCode =>
            orderStatus == "DELIVERING",
        ProofType.ReturnPhoto => orderStatus == "RETURNING",
        _ => false,
    };
}

public static class ProofUploadSessionTransitions
{
    public static bool IsAllowed(ProofUploadSessionStatus current, ProofUploadSessionStatus next) =>
        (current, next) switch
        {
            (ProofUploadSessionStatus.Created, ProofUploadSessionStatus.Uploaded) => true,
            (ProofUploadSessionStatus.Created, ProofUploadSessionStatus.Expired) => true,
            (ProofUploadSessionStatus.Uploaded, ProofUploadSessionStatus.Validating) => true,
            (ProofUploadSessionStatus.Uploaded, ProofUploadSessionStatus.Expired) => true,
            (ProofUploadSessionStatus.Validating, ProofUploadSessionStatus.Ready) => true,
            (ProofUploadSessionStatus.Validating, ProofUploadSessionStatus.Rejected) => true,
            (ProofUploadSessionStatus.Validating, ProofUploadSessionStatus.Expired) => true,
            (ProofUploadSessionStatus.Ready, ProofUploadSessionStatus.Consumed) => true,
            (ProofUploadSessionStatus.Ready, ProofUploadSessionStatus.Expired) => true,
            _ => false,
        };
}

public static class ProofContentPolicy
{
    public const string Jpeg = "image/jpeg";
    public const string Png = "image/png";
    public const string Text = "text/plain";

    public static bool IsAllowed(ProofType proofType, string contentType) => proofType switch
    {
        ProofType.PickupPhoto or ProofType.DeliveryPhoto or ProofType.ReturnPhoto =>
            contentType is Jpeg or Png,
        ProofType.Signature => contentType == Png,
        ProofType.DeliveryCode => contentType == Text,
        _ => false,
    };

    public static bool MatchesMagicBytes(string contentType, ReadOnlySpan<byte> bytes)
    {
        if (contentType == Jpeg)
        {
            return bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff;
        }

        if (contentType == Png)
        {
            ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
            return bytes.Length >= signature.Length && bytes[..signature.Length].SequenceEqual(signature);
        }

        return contentType == Text &&
            bytes.Length > 0 &&
            !bytes.Contains((byte)0) &&
            IsValidUtf8(bytes);
    }

    private static bool IsValidUtf8(ReadOnlySpan<byte> bytes)
    {
        try
        {
            _ = new System.Text.UTF8Encoding(false, true).GetCharCount(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}

public static class ProofSha256
{
    public const int ByteLength = 32;

    public static bool TryParseHex(string? value, out byte[] bytes)
    {
        bytes = [];
        if (value is null || value.Length != ByteLength * 2)
        {
            return false;
        }

        try
        {
            bytes = Convert.FromHexString(value);
            return bytes.Length == ByteLength;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static bool FixedTimeEquals(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right) =>
        left.Length == ByteLength &&
        right.Length == ByteLength &&
        CryptographicOperations.FixedTimeEquals(left, right);
}

public static class ProofCapturedAtPolicy
{
    private const long TicksPerMicrosecond = TimeSpan.TicksPerMillisecond / 1000;

    public static bool TryNormalizeUtc(DateTimeOffset value, out DateTimeOffset normalized)
    {
        normalized = default;
        if (value.Offset != TimeSpan.Zero)
        {
            return false;
        }

        normalized = new DateTimeOffset(
            value.UtcTicks - value.UtcTicks % TicksPerMicrosecond,
            TimeSpan.Zero);
        return true;
    }
}

public static class ProofLocationPolicy
{
    public static bool IsValid(double? latitude, double? longitude) =>
        (latitude is null) == (longitude is null) &&
        latitude is not (< -90 or > 90) &&
        longitude is not (< -180 or > 180);
}

public static class ProofObjectKeys
{
    public static string Quarantine(Guid ownerOrganizationId, Guid orderId, Guid sessionId) =>
        $"quarantine/{ownerOrganizationId:D}/{orderId:D}/{sessionId:D}";

    public static string Final(Guid ownerOrganizationId, Guid orderId, Guid sessionId) =>
        $"proofs/{ownerOrganizationId:D}/{orderId:D}/{sessionId:D}";
}
