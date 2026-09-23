using System.Text;
using Orders.Application.Csv;
using Paqueteria.Application.Idempotency;

namespace Paqueteria.UnitTests.Orders;

public sealed class CsvOrderImportPrevalidatorTests
{
    private const string Header = "quote_id,payer_type,terms_version,privacy_version,accepted_at,acceptance_channel";
    private const string AcceptedAt = "2026-07-22T12:00:00.1234567Z";
    private static readonly Guid FirstQuote = Guid.Parse("81000000-0000-0000-0000-00000000000a");
    private static readonly Guid SecondQuote = Guid.Parse("81000000-0000-0000-0000-00000000000b");

    [Fact]
    public void Canonical_file_prevalidates_every_row_and_is_committable()
    {
        var result = Prevalidate($"""
            {Header}
            {FirstQuote:D},SENDER,terms-synthetic-v1,privacy-synthetic-v1,{AcceptedAt},WEB
            {SecondQuote:D},BUSINESS_ACCOUNT,terms-synthetic-v1,privacy-synthetic-v1,{AcceptedAt},API

            """);

        Assert.Empty(result.FileErrors);
        Assert.True(result.IsCommittable);
        Assert.Equal(2, result.TotalRows);
        Assert.Equal(2, result.ValidRowCount);
        Assert.Equal(0, result.InvalidRowCount);
        Assert.Equal([2, 3], result.Rows.Select(row => row.RowNumber));
        Assert.All(result.Rows, row => Assert.True(row.Valid));
        Assert.Equal(
            [FirstQuote, SecondQuote],
            result.ValidRows.Select(row => row.QuoteId));
        Assert.Equal(
            DateTimeOffset.Parse(AcceptedAt, System.Globalization.CultureInfo.InvariantCulture),
            result.ValidRows[0].AcceptedAt);
    }

    [Fact]
    public void Row_numbers_are_physical_csv_lines_so_the_header_is_line_one()
    {
        var result = Prevalidate($"""
            {Header}
            {FirstQuote:D},SENDER,terms-synthetic-v1,privacy-synthetic-v1,{AcceptedAt},WEB
            """);

        Assert.Equal(2, Assert.Single(result.Rows).RowNumber);
    }

    [Theory]
    [InlineData("not-a-guid", "quote_id", "QUOTE_ID_INVALID")]
    [InlineData("00000000-0000-0000-0000-000000000000", "quote_id", "QUOTE_ID_INVALID")]
    [InlineData("", "quote_id", "QUOTE_ID_INVALID")]
    public void Invalid_quote_identifiers_are_reported_per_row(string quoteId, string column, string code)
    {
        var result = Prevalidate($"""
            {Header}
            {quoteId},SENDER,terms-synthetic-v1,privacy-synthetic-v1,{AcceptedAt},WEB
            """);

        var row = Assert.Single(result.Rows);
        Assert.False(row.Valid);
        Assert.False(result.IsCommittable);
        Assert.Empty(result.ValidRows);
        var error = Assert.Single(row.Errors);
        Assert.Equal(column, error.Column);
        Assert.Equal(code, error.Code);
    }

    [Theory]
    [InlineData("INVALID,terms-synthetic-v1,privacy-synthetic-v1,2026-07-22T12:00:00Z,WEB", "payer_type", "PAYER_TYPE_INVALID")]
    [InlineData("SENDER, ,privacy-synthetic-v1,2026-07-22T12:00:00Z,WEB", "terms_version", "TERMS_VERSION_INVALID")]
    [InlineData("SENDER,terms-synthetic-v1,,2026-07-22T12:00:00Z,WEB", "privacy_version", "PRIVACY_VERSION_INVALID")]
    [InlineData("SENDER,terms-synthetic-v1,privacy-synthetic-v1,not-a-date,WEB", "accepted_at", "ACCEPTED_AT_INVALID")]
    [InlineData("SENDER,terms-synthetic-v1,privacy-synthetic-v1,2026-07-22T12:00:00Z,MOBILE", "acceptance_channel", "ACCEPTANCE_CHANNEL_INVALID")]
    public void Each_invalid_column_reports_its_own_code(string tail, string column, string code)
    {
        var result = Prevalidate($"""
            {Header}
            {FirstQuote:D},{tail}
            """);

        var row = Assert.Single(result.Rows);
        Assert.False(row.Valid);
        var error = Assert.Single(row.Errors);
        Assert.Equal(column, error.Column);
        Assert.Equal(code, error.Code);
    }

    [Fact]
    public void An_offset_less_timestamp_is_rejected_so_the_acceptance_instant_stays_unambiguous()
    {
        var result = Prevalidate($"""
            {Header}
            {FirstQuote:D},SENDER,terms-synthetic-v1,privacy-synthetic-v1,2026-07-22T12:00:00,WEB
            """);

        Assert.Equal("ACCEPTED_AT_INVALID", Assert.Single(Assert.Single(result.Rows).Errors).Code);
    }

    [Fact]
    public void An_explicit_offset_is_preserved_as_the_same_instant()
    {
        var result = Prevalidate($"""
            {Header}
            {FirstQuote:D},SENDER,terms-synthetic-v1,privacy-synthetic-v1,2026-07-22T05:00:00.1234567-07:00,WEB
            """);

        Assert.True(result.IsCommittable);
        Assert.Equal(
            DateTimeOffset.Parse(AcceptedAt, System.Globalization.CultureInfo.InvariantCulture).UtcDateTime,
            result.ValidRows[0].AcceptedAt.UtcDateTime);
    }

    [Fact]
    public void A_row_can_accumulate_several_column_errors()
    {
        var result = Prevalidate($"""
            {Header}
            bad,INVALID,terms-synthetic-v1,privacy-synthetic-v1,nope,MOBILE
            """);

        var row = Assert.Single(result.Rows);
        Assert.Equal(
            ["quote_id", "payer_type", "accepted_at", "acceptance_channel"],
            row.Errors.Select(error => error.Column));
    }

    [Fact]
    public void A_repeated_quote_identifier_is_reported_on_the_later_row_only()
    {
        var result = Prevalidate($"""
            {Header}
            {FirstQuote:D},SENDER,terms-synthetic-v1,privacy-synthetic-v1,{AcceptedAt},WEB
            {FirstQuote:D},SENDER,terms-synthetic-v1,privacy-synthetic-v1,{AcceptedAt},WEB
            """);

        Assert.True(result.Rows[0].Valid);
        Assert.False(result.Rows[1].Valid);
        Assert.Equal("QUOTE_ID_DUPLICATED", Assert.Single(result.Rows[1].Errors).Code);
        Assert.False(result.IsCommittable);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(7)]
    public void A_row_with_the_wrong_number_of_columns_is_reported_once(int columns)
    {
        var fields = Enumerable.Repeat("x", columns);
        var result = Prevalidate($"{Header}\n{string.Join(',', fields)}\n");

        var row = Assert.Single(result.Rows);
        var error = Assert.Single(row.Errors);
        Assert.Equal("file", error.Column);
        Assert.Equal("COLUMN_COUNT_INVALID", error.Code);
    }

    [Theory]
    [InlineData("quote_id,payer_type,terms_version,privacy_version,accepted_at")]
    [InlineData("payer_type,quote_id,terms_version,privacy_version,accepted_at,acceptance_channel")]
    [InlineData("quote_id,payer_type,terms_version,privacy_version,accepted_at,acceptance_channel,extra")]
    public void A_non_canonical_header_fails_the_whole_file(string header)
    {
        var result = Prevalidate($"{header}\n{FirstQuote:D},SENDER,t,p,{AcceptedAt},WEB\n");

        Assert.Equal(["HEADER_INVALID"], result.FileErrors);
        Assert.Empty(result.Rows);
        Assert.False(result.IsCommittable);
    }

    [Fact]
    public void The_header_is_matched_case_insensitively_and_ignoring_surrounding_spaces()
    {
        var result = Prevalidate(
            $" QUOTE_ID , Payer_Type ,terms_version,privacy_version,accepted_at,acceptance_channel\n" +
            $"{FirstQuote:D},SENDER,terms-synthetic-v1,privacy-synthetic-v1,{AcceptedAt},WEB\n");

        Assert.Empty(result.FileErrors);
        Assert.True(result.IsCommittable);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData(Header)]
    [InlineData(Header + "\n\n")]
    public void A_file_without_data_rows_is_empty(string content)
    {
        Assert.Equal(["FILE_EMPTY"], Prevalidate(content).FileErrors);
    }

    [Fact]
    public void More_than_the_contracted_row_limit_fails_the_whole_file()
    {
        var builder = new StringBuilder(Header).Append('\n');
        for (var index = 0; index <= CsvOrderImportContract.MaximumDataRows; index++)
        {
            builder.Append(Guid.NewGuid().ToString("D"))
                .Append(",SENDER,terms-synthetic-v1,privacy-synthetic-v1,")
                .Append(AcceptedAt)
                .Append(",WEB\n");
        }

        var result = Prevalidate(builder.ToString());

        Assert.Equal(["ROW_LIMIT_EXCEEDED"], result.FileErrors);
        Assert.Empty(result.Rows);
    }

    [Fact]
    public void Exactly_the_contracted_row_limit_is_accepted()
    {
        var builder = new StringBuilder(Header).Append('\n');
        for (var index = 0; index < CsvOrderImportContract.MaximumDataRows; index++)
        {
            builder.Append(Guid.NewGuid().ToString("D"))
                .Append(",SENDER,terms-synthetic-v1,privacy-synthetic-v1,")
                .Append(AcceptedAt)
                .Append(",WEB\n");
        }

        var result = Prevalidate(builder.ToString());

        Assert.Empty(result.FileErrors);
        Assert.Equal(CsvOrderImportContract.MaximumDataRows, result.ValidRowCount);
    }

    [Fact]
    public void Quoted_fields_carry_commas_newlines_and_escaped_quotes()
    {
        var result = Prevalidate(
            $"{Header}\n" +
            $"{FirstQuote:D},SENDER,\"terms,\"\"v1\"\"\nline two\",privacy-synthetic-v1,{AcceptedAt},WEB\n" +
            $"{SecondQuote:D},SENDER,terms-synthetic-v1,privacy-synthetic-v1,{AcceptedAt},WEB\n");

        Assert.Empty(result.FileErrors);
        Assert.Equal("terms,\"v1\"\nline two", result.ValidRows[0].TermsVersion);
        Assert.Equal([2, 4], result.Rows.Select(row => row.RowNumber));
    }

    [Fact]
    public void An_unterminated_quoted_field_fails_the_whole_file()
    {
        var result = Prevalidate(
            $"{Header}\n{FirstQuote:D},SENDER,\"terms,privacy-synthetic-v1,{AcceptedAt},WEB\n");

        Assert.Equal(["MALFORMED_QUOTING"], result.FileErrors);
    }

    [Fact]
    public void Crlf_line_endings_and_a_byte_order_mark_are_accepted()
    {
        var text = $"﻿{Header}\r\n{FirstQuote:D},SENDER,terms-synthetic-v1,privacy-synthetic-v1,{AcceptedAt},WEB\r\n";

        var result = Prevalidate(text);

        Assert.Empty(result.FileErrors);
        Assert.True(result.IsCommittable);
        Assert.Equal(2, Assert.Single(result.Rows).RowNumber);
    }

    [Fact]
    public void Invalid_utf8_bytes_fail_the_whole_file()
    {
        var result = CsvOrderImportPrevalidator.Prevalidate([0x71, 0xC3, 0x28, 0x0A]);

        Assert.Equal(["ENCODING_INVALID"], result.FileErrors);
        Assert.Empty(result.Rows);
    }

    [Fact]
    public void The_content_digest_is_a_stable_url_safe_sha256_of_the_raw_bytes()
    {
        var content = Encoding.UTF8.GetBytes($"{Header}\n{FirstQuote:D},SENDER,t,p,{AcceptedAt},WEB\n");

        var digest = CsvOrderImportPrevalidator.ComputeContentDigest(content);

        Assert.Equal(43, digest.Length);
        Assert.DoesNotContain('=', digest);
        Assert.DoesNotContain('+', digest);
        Assert.DoesNotContain('/', digest);
        Assert.Equal(digest, CsvOrderImportPrevalidator.Prevalidate(content).ContentDigest);
        Assert.NotEqual(digest, CsvOrderImportPrevalidator.ComputeContentDigest([.. content, 0x20]));
    }

    [Fact]
    public void Row_idempotency_keys_are_deterministic_and_scoped_to_tenant_digest_and_row()
    {
        var tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var other = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var key = CsvOrderImportIdempotency.DeriveRowKey(tenant, "batch-key-0123456789", "digest-a", 2);

        Assert.Equal(key, CsvOrderImportIdempotency.DeriveRowKey(tenant, "batch-key-0123456789", "digest-a", 2));
        Assert.True(IdempotencyKeyPolicy.IsValid(key));
        Assert.StartsWith(CsvOrderImportIdempotency.RowKeyPrefix, key, StringComparison.Ordinal);
        Assert.NotEqual(key, CsvOrderImportIdempotency.DeriveRowKey(other, "batch-key-0123456789", "digest-a", 2));
        Assert.NotEqual(key, CsvOrderImportIdempotency.DeriveRowKey(tenant, "batch-key-9876543210", "digest-a", 2));
        Assert.NotEqual(key, CsvOrderImportIdempotency.DeriveRowKey(tenant, "batch-key-0123456789", "digest-b", 2));
        Assert.NotEqual(key, CsvOrderImportIdempotency.DeriveRowKey(tenant, "batch-key-0123456789", "digest-a", 3));
    }

    private static CsvOrderImportPrevalidation Prevalidate(string content) =>
        CsvOrderImportPrevalidator.Prevalidate(Encoding.UTF8.GetBytes(content));
}
