using Paqueteria.IntegrationTests.Security;
using Realtime.Application.Dispatching;
using Realtime.Infrastructure.Dispatching;

namespace Paqueteria.IntegrationTests.Realtime;

[Collection(RealtimePostgreSqlKestrelCollection.Name)]
public sealed class RealtimeOutboxEvidencePostgreSqlTests(
    PostgreSqlSecurityWebApplicationFactory database)
{
    [Fact]
    public async Task Own_active_assignment_with_only_active_driver_membership_is_authorized()
    {
        var scenario = await database.CreateAssignmentEvidenceScenarioAsync();
        try
        {
            var evidence = await ReadAsync(scenario.AssignmentId);

            Assert.NotNull(evidence);
            Assert.Equal(scenario.AssignmentId, evidence.AssignmentId);
            Assert.Equal(scenario.DriverId, evidence.DriverId);
            Assert.Equal(scenario.AggregateVersion, evidence.OrderVersion);
            Assert.Equal(scenario.OccurredAt, evidence.OccurredAt);
            Assert.True(evidence.DriverAudienceAuthorized);
        }
        finally
        {
            await database.RetireAssignmentEvidenceScenarioAsync(scenario.AssignmentId);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Active_driver_and_viewer_memberships_are_authorized_in_both_insert_orders(
        bool driverMembershipFirst)
    {
        var scenario = await database.CreateAssignmentEvidenceScenarioAsync(
            includeViewerMembership: true,
            driverMembershipFirst: driverMembershipFirst);
        try
        {
            var evidence = await ReadAsync(scenario.AssignmentId);

            Assert.NotNull(evidence);
            Assert.True(evidence.DriverAudienceAuthorized);
        }
        finally
        {
            await database.RetireAssignmentEvidenceScenarioAsync(scenario.AssignmentId);
        }
    }

    [Theory]
    [InlineData(false, true, "ACTIVE", "OWN", "ACTIVE", "ACTIVE", "ACTIVE")]
    [InlineData(true, true, "SUSPENDED", "OWN", "ACTIVE", "ACTIVE", "ACTIVE")]
    [InlineData(true, false, "ACTIVE", "EXTERNAL", "ACTIVE", "ACTIVE", "ACTIVE")]
    [InlineData(true, false, "ACTIVE", "OWN", "ACTIVE", "SUSPENDED", "ACTIVE")]
    [InlineData(true, false, "ACTIVE", "OWN", "ACTIVE", "ACTIVE", "SUSPENDED")]
    public async Task Invalid_driver_evidence_never_authorizes(
        bool includeDriverMembership,
        bool includeViewerMembership,
        string driverMembershipStatus,
        string assignmentType,
        string assignmentStatus,
        string profileStatus,
        string userStatus)
    {
        var scenario = await database.CreateAssignmentEvidenceScenarioAsync(
            includeDriverMembership,
            includeViewerMembership,
            driverMembershipStatus: driverMembershipStatus,
            assignmentType: assignmentType,
            assignmentStatus: assignmentStatus,
            profileStatus: profileStatus,
            userStatus: userStatus);
        try
        {
            var evidence = await ReadAsync(scenario.AssignmentId);

            Assert.NotNull(evidence);
            Assert.False(evidence.DriverAudienceAuthorized);
        }
        finally
        {
            await database.RetireAssignmentEvidenceScenarioAsync(scenario.AssignmentId);
        }
    }

    [Fact]
    public async Task Cross_tenant_assignment_read_returns_no_evidence()
    {
        var scenario = await database.CreateAssignmentEvidenceScenarioAsync();
        try
        {
            var evidence = await ReadAsync(
                scenario.AssignmentId,
                PostgreSqlSecurityWebApplicationFactory.OperationsOrganizationId);

            Assert.Null(evidence);
        }
        finally
        {
            await database.RetireAssignmentEvidenceScenarioAsync(scenario.AssignmentId);
        }
    }

    private async Task<AssignmentEvidence?> ReadAsync(
        Guid assignmentId,
        Guid? organizationId = null)
    {
        await using var connections = new RealtimeWorkerConnectionFactory(
            database.WorkerConnectionString);
        var reader = new PostgreSqlRealtimeOutboxEvidenceReader(connections);
        return await reader.ReadAssignmentAsync(
            organizationId ?? PostgreSqlSecurityWebApplicationFactory.ViewerOrganizationId,
            assignmentId,
            default);
    }
}
