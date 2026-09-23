using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Paqueteria.Application.Idempotency;

namespace Orders.Application.Csv;

/// <summary>
/// Derives the per-row ORD-001 idempotency key of a CSV-001 batch.
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
