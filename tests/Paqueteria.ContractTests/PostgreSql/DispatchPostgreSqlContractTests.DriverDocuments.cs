using Drivers.Application.Documents;
using Drivers.Infrastructure;
using Drivers.Infrastructure.Documents;
using Drivers.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Paqueteria.ContractTests.PostgreSql.Fixtures;
using Paqueteria.Application.Auditing;
using Paqueteria.Infrastructure.Auditing;
using Paqueteria.Infrastructure.Tenancy;

namespace Paqueteria.ContractTests.PostgreSql;

/// <summary>
/// DRV-DOC-001-IN-PERSON-2026-11-02 (AI-08 DRV-DOC-001 required test "DSP-001 eligibility end to end"): documents recorded
/// in person through the real Drivers service - no key, no hash - make the driver eligible in the dispatcher's picker
/// (listAssignableDrivers, whose array reader receives NULL key and hash elements) and in assignDriver; a missing,
/// REJECTED or REVOKED one keeps its DSP-001 rejection.
/// </summary>
public sealed partial class DispatchPostgreSqlContractTests
{
    [PostgreSqlContractFact]
    public async Task In_person_documents_drive_the_picker_and_assignDriver_through_DSP001()
    {
        await using var scenario = await DispatchScenario.CreateAsync(fixture);
        try
        {
            // Only in-person documents from here on.
            await scenario.ExecuteAdminAsync(
                "DELETE FROM drivers.driver_documents WHERE driver_id=@driver",
                P("driver", scenario.DriverId));
            Assert.Equal(["REQUIRED_DOCUMENT_MISSING"], await PickerReasonsAsync(scenario));

            var documents = CreateInPersonDocumentService();
            // OccurredAt is 15:00 in Culiacán on 2026-07-23: a document printed for that day is valid all of it (Y12).
            var identity = await documents.RecordAsync(InPerson(scenario, new DateOnly(2026, 7, 23)), default);
            Assert.Equal(new DateTimeOffset(2026, 7, 24, 7, 0, 0, TimeSpan.Zero), identity.ExpiresAt);
            Assert.Empty(await PickerReasonsAsync(scenario));
            Assert.Equal(
                "IN_PERSON|t|t",
                await DocumentScalarAsync<string>(
                    $"SELECT evidence_kind || '|' || (object_key IS NULL)::text::char || '|' || (sha256 IS NULL)::text::char FROM drivers.driver_documents WHERE id='{identity.Id}'"));

            var rejected = await documents.RecordAsync(
                InPerson(scenario, null) with
                {
                    Decision = DriverDocumentVocabulary.Rejected,
                    ExpiryBasis = null,
                    RejectionCode = "ILLEGIBLE",
                },
                default);
            Assert.Equal("REJECTED", rejected.Status);
            // A rejection carries no expiry, so the picker also reports the missing expiry (the DSP-001 rule).
            Assert.Equal(["DOCUMENT_STATUS_NOT_VALID", "DOCUMENT_EXPIRY_MISSING"], await PickerReasonsAsync(scenario));
            await Assert.ThrowsAsync<Dispatch.Application.Assignments.AssignmentConflictException>(() =>
                CreateAssignmentService(fixture.AppDataSource).CreateOwnDriverAssignmentAsync(Command(scenario), default));

            var renewed = await documents.RecordAsync(InPerson(scenario, new DateOnly(2027, 1, 31)), default);
            Assert.Empty(await PickerReasonsAsync(scenario));
            await documents.RevokeAsync(
                new RevokeDriverDocumentCommand(
                    scenario.DispatcherUserId, scenario.OrganizationId, scenario.DriverId, renewed.Id, "req-revoke"),
                default);
            Assert.Equal(["DOCUMENT_STATUS_NOT_VALID"], await PickerReasonsAsync(scenario));

            await documents.RecordAsync(InPerson(scenario, new DateOnly(2027, 1, 31)), default);
            Assert.Empty(await PickerReasonsAsync(scenario));
            var created = await CreateAssignmentService(fixture.AppDataSource)
                .CreateOwnDriverAssignmentAsync(Command(scenario), default);
            Assert.Equal(scenario.DriverId, created.DriverId);
        }
        finally
        {
            await scenario.ExecuteAdminAsync(
                "DELETE FROM dispatch.assignments WHERE driver_id=@driver; DELETE FROM drivers.driver_documents WHERE driver_id=@driver;",
                P("driver", scenario.DriverId));
        }
    }

    private async Task<string[]> PickerReasonsAsync(DispatchScenario scenario)
    {
        var page = await CreateAssignableDriversQuery().ListAsync(PickerQuery(scenario), default);
        return page.Items.Single(item => item.DriverId == scenario.DriverId).IneligibilityReasons.ToArray();
    }

    private static RecordDriverDocumentCommand InPerson(DispatchScenario scenario, DateOnly? printed) => new(
        scenario.DispatcherUserId,
        scenario.OrganizationId,
        scenario.DriverId,
        "IDENTITY",
        DriverDocumentVocabulary.Valid,
        DriverDocumentVocabulary.PrintedExpiry,
        printed,
        null,
        "drv-doc-001-dispatch-" + Guid.NewGuid().ToString("N"),
        "req-record");

    private PostgreSqlDriverDocumentService CreateInPersonDocumentService()
    {
        var state = new TenantDatabaseExecutionState();
        var dbOptions = new DbContextOptionsBuilder<DriversDbContext>()
            .UseNpgsql(fixture.AppDataSource, postgres =>
            {
                postgres.UseNetTopologySuite();
                postgres.EnableRetryOnFailure();
            })
            .AddInterceptors(new TenantTransactionGuardInterceptor(state), new TenantSaveChangesGuardInterceptor(state))
            .Options;
        return new PostgreSqlDriverDocumentService(
            new TenantTransactionContext<DriversDbContext>(new DriversDbContext(dbOptions, state), state),
            Options.Create(new DriversOptions
            {
                Provider = DriversProviderKind.PostgreSql,
                Documents = new DriverDocumentsOptions { Enabled = true },
                Eligibility = new DriverEligibilityOptions
                {
                    // The picker's own document policy (EligibilityOptions): a MOTORCYCLE needs IDENTITY.
                    RequiredDocumentTypesByVehicleType = new(StringComparer.Ordinal) { ["MOTORCYCLE"] = ["IDENTITY"] },
                },
            }),
            new PostgreSqlAppendOnlyAuditWriter(state),
            new AuditPayloadRedactor(),
            new FixedClock(OccurredAt),
            NullLogger<PostgreSqlDriverDocumentService>.Instance);
    }

    private async Task<T> DocumentScalarAsync<T>(string sql)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(sql);
        return (T)(await command.ExecuteScalarAsync())!;
    }
}
