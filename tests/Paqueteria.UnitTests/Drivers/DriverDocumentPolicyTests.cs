using Drivers.Application.Documents;
using Drivers.Infrastructure.Documents;

namespace Paqueteria.UnitTests.Drivers;

/// <summary>
/// DRV-DOC-001-IN-PERSON-2026-11-02: the calendar of an in-person document (Y5, Y12), the published vocabulary (Y7,
/// Y11), the service's own shape check and the request identity of the record idempotency.
/// </summary>
public sealed class DriverDocumentPolicyTests
{
    private static readonly DateOnly Today = new(2026, 11, 2);

    [Fact]
    public void A_printed_date_is_its_own_last_valid_day()
    {
        var printed = new DateOnly(2027, 3, 1);
        Assert.Equal(printed, DriverDocumentCalendar.ValidThrough(Today, DriverDocumentVocabulary.PrintedExpiry, printed));
        Assert.Throws<ArgumentNullException>(
            () => DriverDocumentCalendar.ValidThrough(Today, DriverDocumentVocabulary.PrintedExpiry, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => DriverDocumentCalendar.ValidThrough(Today, "OTHER", printed));
    }

    [Theory]
    [InlineData(2026, 11, 2, 2027, 11, 2)]
    [InlineData(2028, 2, 29, 2029, 2, 28)]
    [InlineData(2026, 12, 31, 2027, 12, 31)]
    public void A_permanent_license_is_reviewed_twelve_months_after_the_review_day(
        int year, int month, int day, int reviewYear, int reviewMonth, int reviewDay)
    {
        Assert.Equal(12, DriverDocumentCalendar.PermanentLicenseReviewMonths);
        Assert.Equal(
            new DateOnly(reviewYear, reviewMonth, reviewDay),
            DriverDocumentCalendar.ValidThrough(
                new DateOnly(year, month, day), DriverDocumentVocabulary.PermanentLicenseReview, null));
    }

    [Fact]
    public void An_approval_must_be_valid_today_and_at_most_ten_years_ahead()
    {
        Assert.True(DriverDocumentCalendar.IsAcceptable(Today, Today));
        Assert.True(DriverDocumentCalendar.IsAcceptable(Today, Today.AddYears(10)));
        Assert.False(DriverDocumentCalendar.IsAcceptable(Today, Today.AddDays(-1)));
        Assert.False(DriverDocumentCalendar.IsAcceptable(Today, Today.AddYears(10).AddDays(1)));
        Assert.Equal("America/Mazatlan", DriverDocumentCalendar.TimeZone);
    }

    [Fact]
    public void The_vocabulary_is_the_decided_one()
    {
        Assert.Equal(["IDENTITY", "DRIVER_LICENSE", "VEHICLE_CARD", "INSURANCE"], DriverDocumentVocabulary.RecordableTypes.ToArray());
        Assert.DoesNotContain("BACKGROUND_CHECK", DriverDocumentVocabulary.RecordableTypes.ToArray());
        Assert.DoesNotContain("OTHER", DriverDocumentVocabulary.RecordableTypes.ToArray());
        Assert.Equal(
            ["ILLEGIBLE", "EXPIRED", "WRONG_DOCUMENT", "HOLDER_MISMATCH", "INCOMPLETE", "NOT_ACCEPTED"],
            DriverDocumentVocabulary.RejectionCodes.ToArray());
        Assert.Equal(["PRINTED_EXPIRY", "PERMANENT_LICENSE_REVIEW"], DriverDocumentVocabulary.ExpiryBases.ToArray());
        Assert.Equal(["VALID", "REJECTED"], DriverDocumentVocabulary.Decisions.ToArray());
        Assert.Equal(["PENDING", "VALID", "REJECTED", "EXPIRED", "REVOKED"], DriverDocumentVocabulary.Statuses.ToArray());
        Assert.Equal(
            ["INVALID_REQUEST", "IDEMPOTENCY_CONFLICT", "DOCUMENT_TYPE_NOT_REQUIRED", "EXPIRY_DATE_OUT_OF_RANGE"],
            DriverDocumentConflictCodes.Record.ToArray());
        Assert.Equal(["INVALID_REQUEST", "DOCUMENT_NOT_REVOCABLE"], DriverDocumentConflictCodes.Revoke.ToArray());
        Assert.Equal(100, DriverDocumentPaging.PageSize);
    }

    [Fact]
    public void The_cursor_is_bound_to_this_list()
    {
        var driver = Guid.Parse("7b0e1d2c-3f4a-4b5c-8d6e-7f8091a2b3c4");
        var cursor = DriverDocumentCursorCodec.Encode(driver);
        Assert.True(DriverDocumentCursorCodec.TryDecode(cursor, out var decoded));
        Assert.Equal(driver, decoded);
        Assert.False(DriverDocumentCursorCodec.TryDecode(
            Paqueteria.Application.Paging.IdKeysetCursorCodec.Encode(2, driver), out _));
        Assert.False(DriverDocumentCursorCodec.TryDecode("not-a-cursor", out _));
    }

    [Fact]
    public void The_service_accepts_only_the_two_published_shapes()
    {
        Assert.True(PostgreSqlDriverDocumentService.IsWellFormed(Valid()));
        Assert.True(PostgreSqlDriverDocumentService.IsWellFormed(Permanent()));
        Assert.True(PostgreSqlDriverDocumentService.IsWellFormed(Rejected()));

        foreach (var invalid in new[]
                 {
                     Valid() with { RejectionCode = "ILLEGIBLE" },
                     Valid() with { PrintedDate = null },
                     Valid() with { ExpiryBasis = null },
                     Permanent() with { DocumentType = "IDENTITY" },
                     Permanent() with { PrintedDate = Today },
                     Rejected() with { RejectionCode = null },
                     Rejected() with { RejectionCode = "OTHER_REASON" },
                     Rejected() with { ExpiryBasis = DriverDocumentVocabulary.PrintedExpiry },
                     Rejected() with { PrintedDate = Today },
                     Valid() with { Decision = "PENDING" },
                     Valid() with { DocumentType = "BACKGROUND_CHECK" },
                     Valid() with { DocumentType = "OTHER" },
                     Valid() with { IdempotencyKey = "short" },
                     Valid() with { DriverId = Guid.Empty },
                     Valid() with { ActorId = Guid.Empty },
                     Valid() with { OrganizationId = Guid.Empty },
                 })
        {
            Assert.False(PostgreSqlDriverDocumentService.IsWellFormed(invalid), invalid.ToString());
        }
    }

    [Fact]
    public void The_request_identity_binds_the_person_the_driver_and_the_body()
    {
        var hash = PostgreSqlDriverDocumentService.RequestHash(Valid());
        Assert.Equal(32, hash.Length);
        Assert.Equal(hash, PostgreSqlDriverDocumentService.RequestHash(Valid() with { RequestId = "another-trace" }));
        foreach (var other in new[]
                 {
                     Valid() with { ActorId = Guid.NewGuid() },
                     Valid() with { OrganizationId = Guid.NewGuid() },
                     Valid() with { DriverId = Guid.NewGuid() },
                     Valid() with { DocumentType = "IDENTITY" },
                     Valid() with { PrintedDate = Today.AddDays(1) },
                     Permanent(),
                     Rejected(),
                 })
        {
            Assert.NotEqual(hash, PostgreSqlDriverDocumentService.RequestHash(other));
        }
    }

    private static RecordDriverDocumentCommand Valid() => new(
        Guid.Parse("11111111-1111-4111-8111-111111111111"),
        Guid.Parse("22222222-2222-4222-8222-222222222222"),
        Guid.Parse("33333333-3333-4333-8333-333333333333"),
        "DRIVER_LICENSE",
        DriverDocumentVocabulary.Valid,
        DriverDocumentVocabulary.PrintedExpiry,
        new DateOnly(2027, 3, 1),
        null,
        "drv-doc-001-unit-key-0001",
        "trace");

    private static RecordDriverDocumentCommand Permanent() => Valid() with
    {
        ExpiryBasis = DriverDocumentVocabulary.PermanentLicenseReview,
        PrintedDate = null,
    };

    private static RecordDriverDocumentCommand Rejected() => Valid() with
    {
        Decision = DriverDocumentVocabulary.Rejected,
        ExpiryBasis = null,
        PrintedDate = null,
        RejectionCode = "ILLEGIBLE",
    };
}
