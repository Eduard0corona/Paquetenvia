using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Paqueteria.Application.Idempotency;

namespace Orders.Application.Csv;

/// <summary>
/// Derives the idempotency identities of a CSV-001 batch: the batch reservation the commit takes
/// out for its <c>Idempotency-Key</c>, and the per-row ORD-001 key each confirmed row is created
/// under.
/// </summary>
/// <remarks>
/// The key is a pure function of tenant, batch key, content digest and row number, so replaying a
/// commit replays every row against the existing <c>ORD-001:CREATE_ORDER</c> idempotency records
/// and creates nothing new. The tenant is part of the pre-image so two organizations that pick the
/// same batch key never share a row key. ORD-001 keeps its own backstop: <c>orders.quote_id</c> is
/// unique, so a quote can yield at most one order regardless of how a batch is replayed.
/// </remarks>
public static class CsvOrderImportIdempotency
{
    public const string RowKeyPrefix = "CSV1.";

    /// <summary>The <c>platform.idempotency_keys</c> scope of the batch reservation.</summary>
    public const string BatchScope = "CSV-001:COMMIT_ORDER_CSV";

    /// <summary>The HTTP status the commit endpoint replays for a completed batch.</summary>
    public const int BatchResponseStatus = 200;

    /// <summary>
    /// The request hash the batch reservation is bound to: the tenant and the canonical batch, the
    /// batch being the digest of the exact uploaded bytes plus the number of rows derived from
    /// them. The actor is deliberately excluded so a colleague's retry replays instead of
    /// conflicting, and the key itself is excluded because it is the record's own identity.
    /// </summary>
    public static byte[] ComputeBatchRequestHash(CsvOrderImportBatchIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return SHA256.HashData(Encoding.UTF8.GetBytes(BatchPreImage(identity)));
    }

    /// <summary>
    /// The synthetic identifier of the batch, so the reservation names a resource the way every
    /// other <c>platform.idempotency_keys</c> record does. It is a pure function of the same
    /// pre-image as the request hash, so a replay resolves to the same batch.
    /// </summary>
    public static Guid DeriveBatchId(CsvOrderImportBatchIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(identity.IdempotencyKey + '\n' + BatchPreImage(identity)), hash);
        return new Guid(hash[..16]);
    }

    private static string BatchPreImage(CsvOrderImportBatchIdentity identity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.ContentDigest);
        ArgumentOutOfRangeException.ThrowIfNegative(identity.RowCount);
        return string.Join(
            '\n',
            BatchScope,
            identity.OrganizationId.ToString("D", CultureInfo.InvariantCulture),
            identity.ContentDigest,
            identity.RowCount.ToString(CultureInfo.InvariantCulture));
    }

    public static string DeriveRowKey(
        Guid organizationId,
        string batchIdempotencyKey,
        string contentDigest,
        int rowNumber)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(batchIdempotencyKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentDigest);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rowNumber);

        var preImage = string.Join(
            '\n',
            organizationId.ToString("D", CultureInfo.InvariantCulture),
            batchIdempotencyKey,
            contentDigest,
            rowNumber.ToString(CultureInfo.InvariantCulture));
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(preImage), hash);
        var key = RowKeyPrefix + Base64Url.EncodeToString(hash);
        return IdempotencyKeyPolicy.IsValid(key)
            ? key
            : throw new InvalidOperationException("The derived CSV row idempotency key is invalid.");
    }
}
