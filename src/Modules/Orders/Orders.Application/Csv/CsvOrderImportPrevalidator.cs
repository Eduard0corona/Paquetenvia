using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Orders.Application.Orders;

namespace Orders.Application.Csv;

/// <summary>
/// Pure CSV-001 prevalidation. It has no dependency on <see cref="IOrderService"/> or on any
/// persistence, so preview provably cannot create an order. Every column rule delegates to the
/// authoritative ORD-001 input policies instead of restating them.
/// </summary>
public static class CsvOrderImportPrevalidator
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    /// <summary>
    /// The ISO-8601 forms AI-05 contracts for <c>accepted_at</c> (<c>format: date-time</c>): a
    /// calendar date, a 24-hour time, an optional fraction of up to seven digits, and either the
    /// UTC designator or a numeric offset. These are exactly the shapes ORD-001 already accepts on
    /// <c>POST /orders</c>, so a row that imports is a row the create endpoint would have taken.
    /// </summary>
    private static readonly string[] AcceptedAtFormats =
    [
        "yyyy'-'MM'-'dd'T'HH':'mm':'ss'Z'",
        "yyyy'-'MM'-'dd'T'HH':'mm':'ss.FFFFFFF'Z'",
        "yyyy'-'MM'-'dd'T'HH':'mm':'sszzz",
        "yyyy'-'MM'-'dd'T'HH':'mm':'ss.FFFFFFFzzz",
    ];

    public static CsvOrderImportPrevalidation Prevalidate(ReadOnlySpan<byte> content)
    {
        var digest = ComputeContentDigest(content);
        string text;
        try
        {
            text = StrictUtf8.GetString(content);
        }
        catch (DecoderFallbackException)
        {
            return FileFailure(digest, CsvOrderImportFileErrorCodes.EncodingInvalid);
        }

        if (text.Length > 0 && text[0] == '﻿')
        {
            text = text[1..];
        }

        if (!CsvRecordReader.TryReadRecords(text, out var records))
        {
            return FileFailure(digest, CsvOrderImportFileErrorCodes.MalformedQuoting);
        }

        if (records.Count == 0)
        {
            return FileFailure(digest, CsvOrderImportFileErrorCodes.FileEmpty);
        }

        if (!IsCanonicalHeader(records[0].Fields))
        {
            return FileFailure(digest, CsvOrderImportFileErrorCodes.HeaderInvalid);
        }

        var dataRecords = records.Skip(1).ToArray();
        if (dataRecords.Length == 0)
        {
            return FileFailure(digest, CsvOrderImportFileErrorCodes.FileEmpty);
        }

        if (dataRecords.Length > CsvOrderImportContract.MaximumDataRows)
        {
            return FileFailure(digest, CsvOrderImportFileErrorCodes.RowLimitExceeded);
        }

        var previews = new List<CsvOrderImportRowPreview>(dataRecords.Length);
        var validRows = new List<CsvOrderImportOrderRow>(dataRecords.Length);
        var seenQuoteIds = new HashSet<Guid>();
        foreach (var record in dataRecords)
        {
            previews.Add(ValidateRow(record, seenQuoteIds, validRows));
        }

        return new CsvOrderImportPrevalidation(digest, [], previews, validRows);
    }

    public static string ComputeContentDigest(ReadOnlySpan<byte> content)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(content, hash);
        return Base64Url.EncodeToString(hash);
    }

    private static CsvOrderImportRowPreview ValidateRow(
        CsvRecord record,
        HashSet<Guid> seenQuoteIds,
        List<CsvOrderImportOrderRow> validRows)
    {
        if (record.Fields.Count != CsvOrderImportContract.Header.Length)
        {
            return new CsvOrderImportRowPreview(
                record.LineNumber,
                null,
                null,
                false,
                [new CsvOrderImportRowError(
                    CsvOrderImportContract.ColumnFile,
                    CsvOrderImportRowErrorCodes.ColumnCountInvalid)]);
        }

        var quoteIdText = record.Fields[0].Trim();
        var payerType = record.Fields[1].Trim();
        var termsVersion = record.Fields[2].Trim();
        var privacyVersion = record.Fields[3].Trim();
        var acceptedAtText = record.Fields[4].Trim();
        var acceptanceChannel = record.Fields[5].Trim();
        var errors = new List<CsvOrderImportRowError>();

        var hasQuoteId = Guid.TryParseExact(quoteIdText, "D", out var quoteId) && quoteId != Guid.Empty;
        if (!hasQuoteId)
        {
            errors.Add(new CsvOrderImportRowError(
                CsvOrderImportContract.ColumnQuoteId,
                CsvOrderImportRowErrorCodes.QuoteIdInvalid));
        }
        else if (!seenQuoteIds.Add(quoteId))
        {
            errors.Add(new CsvOrderImportRowError(
                CsvOrderImportContract.ColumnQuoteId,
                CsvOrderImportRowErrorCodes.QuoteIdDuplicated));
        }

        if (!OrderInputPolicy.IsPayerType(payerType))
        {
            errors.Add(new CsvOrderImportRowError(
                CsvOrderImportContract.ColumnPayerType,
                CsvOrderImportRowErrorCodes.PayerTypeInvalid));
        }

        if (!IsVersion(termsVersion))
        {
            errors.Add(new CsvOrderImportRowError(
                CsvOrderImportContract.ColumnTermsVersion,
                CsvOrderImportRowErrorCodes.TermsVersionInvalid));
        }

        if (!IsVersion(privacyVersion))
        {
            errors.Add(new CsvOrderImportRowError(
                CsvOrderImportContract.ColumnPrivacyVersion,
                CsvOrderImportRowErrorCodes.PrivacyVersionInvalid));
        }

        if (!TryParseAcceptedAt(acceptedAtText, out var acceptedAt))
        {
            errors.Add(new CsvOrderImportRowError(
                CsvOrderImportContract.ColumnAcceptedAt,
                CsvOrderImportRowErrorCodes.AcceptedAtInvalid));
        }

        if (!OrderInputPolicy.IsAcceptanceChannel(acceptanceChannel))
        {
            errors.Add(new CsvOrderImportRowError(
                CsvOrderImportContract.ColumnAcceptanceChannel,
                CsvOrderImportRowErrorCodes.AcceptanceChannelInvalid));
        }

        var reportedQuoteId = hasQuoteId ? quoteId.ToString("D") : null;
        if (errors.Count > 0 ||
            !OrderAcceptanceInputPolicy.IsValid(
                termsVersion,
                privacyVersion,
                acceptedAt,
                acceptanceChannel))
        {
            return new CsvOrderImportRowPreview(record.LineNumber, reportedQuoteId, null, false, errors);
        }

        validRows.Add(new CsvOrderImportOrderRow(
            record.LineNumber,
            quoteId,
            payerType,
            termsVersion,
            privacyVersion,
            acceptedAt,
            acceptanceChannel));
        return new CsvOrderImportRowPreview(record.LineNumber, reportedQuoteId, payerType, true, []);
    }

    private static bool IsCanonicalHeader(IReadOnlyList<string> fields) =>
        fields.Count == CsvOrderImportContract.Header.Length &&
        !fields.Where((field, index) => !string.Equals(
            field.Trim(),
            CsvOrderImportContract.Header[index],
            StringComparison.OrdinalIgnoreCase)).Any();

    private static bool IsVersion(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= CsvOrderImportContract.MaximumVersionLength;

    /// <summary>
    /// Parses <c>accepted_at</c> against the explicit ISO-8601 shapes AI-05 contracts and nothing
    /// else. Only an explicit offset is accepted, because an offset-less timestamp would be
    /// resolved against the server clock zone and the legal acceptance instant would be ambiguous.
    /// Matching the format list exactly, rather than probing with a permissive parser, is what
    /// rejects near-misses such as <c>2026/07/22T12:00:00Z</c> instead of reinterpreting them.
    /// </summary>
    private static bool TryParseAcceptedAt(string value, out DateTimeOffset acceptedAt)
    {
        acceptedAt = default;
        return HasContractedOffset(value) &&
            DateTimeOffset.TryParseExact(
                value,
                AcceptedAtFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out acceptedAt) &&
            acceptedAt != default;
    }

    /// <summary>
    /// The <c>zzz</c> specifier also matches the colon-less basic offset (<c>-0700</c>), which the
    /// extended ISO-8601 profile ORD-001 reads on <c>POST /orders</c> rejects. Requiring the UTC
    /// designator or a full <c>±hh:mm</c> keeps the CSV row and the JSON body on one grammar.
    /// </summary>
    private static bool HasContractedOffset(string value) =>
        value.EndsWith('Z') ||
        (value.Length >= 6 &&
            value[^6] is '+' or '-' &&
            value[^3] == ':' &&
            char.IsAsciiDigit(value[^5]) &&
            char.IsAsciiDigit(value[^4]) &&
            char.IsAsciiDigit(value[^2]) &&
            char.IsAsciiDigit(value[^1]));

    private static CsvOrderImportPrevalidation FileFailure(string digest, string code) =>
        new(digest, [code], [], []);
}
