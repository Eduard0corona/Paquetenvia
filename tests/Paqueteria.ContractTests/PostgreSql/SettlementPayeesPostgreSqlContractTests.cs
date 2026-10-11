using Finance.Application;
using Finance.Application.Settlements;
using Npgsql;
using Paqueteria.ContractTests.PostgreSql.Fixtures;

namespace Paqueteria.ContractTests.PostgreSql;

/// <summary>
/// UI-PHASE3-DRIVERS-2026-10-11 (listSettlementPayees) on real PostgreSQL as the NOBYPASSRLS runtime role: every
/// driver profile of the organization - the payees createSettlement accepts, whatever their type or status - and
/// never another organization's; exactly the createSettlement roles; cursor pages; nothing written.
/// </summary>
public sealed partial class SettlementWorkflowPostgreSqlContractTests
{
    [PostgreSqlContractFact]
    public async Task Payees_are_every_driver_profile_createSettlement_accepts_and_no_other_organizations()
    {
        await using var scenario = await SettlementScenario.CreateAsync(fixture);
        var otherOrganizationId = Guid.NewGuid();
        var users = new List<Guid>();
        var drivers = new List<Guid>();
        try
        {
            async Task<Guid> AddDriverAsync(Guid organizationId, string type, string status, string vehicle)
            {
                var user = Guid.NewGuid();
                var driver = Guid.NewGuid();
                users.Add(user);
                drivers.Add(driver);
                await ExecuteAdminAsync(
                    """
                    INSERT INTO identity.users(id,identity_subject,status) VALUES (@user,'payee-' || @user::text,'ACTIVE');
                    INSERT INTO drivers.driver_profiles(id,user_id,org_id,home_city_id,driver_type,vehicle_type,status)
                    VALUES (@driver,@user,@org,@city,@type,@vehicle,@status);
                    """,
                    new NpgsqlParameter("user", user),
                    new NpgsqlParameter("driver", driver),
                    new NpgsqlParameter("org", organizationId),
                    new NpgsqlParameter("city", scenario.CityId),
                    new NpgsqlParameter("type", type),
                    new NpgsqlParameter("vehicle", vehicle),
                    new NpgsqlParameter("status", status));
                return driver;
            }

            var suspended = await AddDriverAsync(scenario.OrganizationId, "OWN", "SUSPENDED", "VAN");
            var retired = await AddDriverAsync(scenario.OrganizationId, "OWN", "INACTIVE", "BICYCLE");
            var pending = await AddDriverAsync(scenario.OrganizationId, "OWN", "PENDING", "WALKER");
            var ally = await AddDriverAsync(scenario.OrganizationId, "ALLY", "ACTIVE", "CAR");
            await ExecuteAdminAsync(
                """
                INSERT INTO organizations.organizations(id,legal_name,display_name,organization_type)
                VALUES (@other,'Payee other','Payee other','ALLY');
                """,
                new NpgsqlParameter("other", otherOrganizationId));
            var foreign = await AddDriverAsync(otherOrganizationId, "OWN", "ACTIVE", "MOTORCYCLE");

            var service = CreateService();
            var page = await service.ListPayeesAsync(Payees(scenario.FinanceUserId, scenario), default);

            Guid[] expected = [scenario.DriverId, scenario.OtherDriverId, suspended, retired, pending, ally];
            Assert.Null(page.NextCursor);
            Assert.Equal(
                expected.OrderBy(id => id.ToString("D"), StringComparer.Ordinal),
                page.Items.Select(item => item.DriverId));
            Assert.DoesNotContain(page.Items, item => item.DriverId == foreign);
            Assert.All(page.Items, item => Assert.Equal(
                Paqueteria.Application.Privacy.DriverReference.From(item.DriverId), item.DriverReference));
            Assert.Equal(
                ["MOTORCYCLE", "CAR", "VAN", "BICYCLE", "WALKER", "CAR"],
                expected.Select(id => page.Items.Single(item => item.DriverId == id).VehicleType));

            // Every listed payee is one createSettlement accepts - a retired driver can still be paid for past work
            // (here an empty settlement, total 0) - and the other organization's driver is the uniform not-found.
            var settled = await service.CreateAsync(
                scenario.Create("payee-retired") with { DriverId = retired }, default);
            Assert.Equal((retired, 0L), (settled.PayeeId, settled.TotalCents));
            await Assert.ThrowsAsync<FinanceNotFoundException>(() => service.CreateAsync(
                scenario.Create("payee-foreign") with { DriverId = foreign }, default));

            // Listing wrote nothing: the only Finance evidence is the one settlement created above.
            Assert.Equal(["finance.settlement.calculated"], await scenario.AllAuditActionsAsync());
        }
        finally
        {
            await ExecuteAdminAsync(
                """
                DELETE FROM drivers.driver_profiles WHERE id=ANY(@drivers);
                DELETE FROM identity.users WHERE id=ANY(@users);
                DELETE FROM organizations.organizations WHERE id=@other;
                """,
                new NpgsqlParameter("drivers", drivers.ToArray()),
                new NpgsqlParameter("users", users.ToArray()),
                new NpgsqlParameter("other", otherOrganizationId));
        }
    }

    [PostgreSqlContractFact]
    public async Task Payees_admit_exactly_the_createSettlement_roles_before_reading()
    {
        await using var scenario = await SettlementScenario.CreateAsync(fixture);
        var service = CreateService();

        Assert.Equal(2, (await service.ListPayeesAsync(Payees(scenario.FinanceUserId, scenario), default)).Items.Count);
        Assert.Equal(2, (await service.ListPayeesAsync(
            Payees(scenario.AdminUserId, scenario) with { MfaSatisfied = true }, default)).Items.Count);

        foreach (var (actor, mfa) in new[]
                 {
                     (scenario.AdminUserId, false), (scenario.DispatcherUserId, true), (scenario.ViewerUserId, true),
                     (scenario.DriverUserId, true), (Guid.NewGuid(), true),
                 })
        {
            await Assert.ThrowsAsync<FinanceForbiddenException>(() => service.ListPayeesAsync(
                Payees(actor, scenario) with { MfaSatisfied = mfa }, default));
        }

        // A suspended FINANCE membership no longer reads the payees.
        await scenario.SuspendAsync(scenario.FinanceUserId, "FINANCE");
        await Assert.ThrowsAsync<FinanceForbiddenException>(() => service.ListPayeesAsync(
            Payees(scenario.FinanceUserId, scenario) with { MfaSatisfied = true }, default));

        // A request outside the contract never opens a transaction.
        var invalid = await Assert.ThrowsAsync<SettlementConflictException>(() => service.ListPayeesAsync(
            Payees(scenario.FinanceUserId, scenario) with { AfterDriverId = Guid.Empty }, default));
        Assert.Equal(SettlementConflictCode.InvalidRequest, invalid.Code);
    }

    [PostgreSqlContractFact]
    public async Task Payees_page_by_cursor_without_overlap()
    {
        await using var scenario = await SettlementScenario.CreateAsync(fixture);
        var prefix = $"payee-page-{Guid.NewGuid():N}-";
        try
        {
            // The scenario has two drivers; these make one more than a page.
            await ExecuteAdminAsync(
                """
                WITH created AS (
                  SELECT gen_random_uuid() AS user_id,gen_random_uuid() AS driver_id,n
                  FROM generate_series(1,@count) n
                ), users AS (
                  INSERT INTO identity.users(id,identity_subject,status)
                  SELECT user_id,@prefix||n,'ACTIVE' FROM created
                )
                INSERT INTO drivers.driver_profiles(id,user_id,org_id,home_city_id,driver_type,vehicle_type,status)
                SELECT driver_id,user_id,@org,@city,'OWN','MOTORCYCLE','ACTIVE' FROM created;
                """,
                new NpgsqlParameter("count", SettlementPayeePolicy.PageSize - 1),
                new NpgsqlParameter("prefix", prefix),
                new NpgsqlParameter("org", scenario.OrganizationId),
                new NpgsqlParameter("city", scenario.CityId));

            var service = CreateService();
            var first = await service.ListPayeesAsync(Payees(scenario.FinanceUserId, scenario), default);
            Assert.Equal(SettlementPayeePolicy.PageSize, first.Items.Count);
            Assert.True(SettlementPayeeCursorCodec.TryDecode(first.NextCursor, out var after));
            Assert.Equal(first.Items[^1].DriverId, after);
            var second = await service.ListPayeesAsync(
                Payees(scenario.FinanceUserId, scenario) with { AfterDriverId = after }, default);
            Assert.Single(second.Items);
            Assert.Null(second.NextCursor);

            var all = first.Items.Concat(second.Items).Select(item => item.DriverId).ToArray();
            Assert.Equal(SettlementPayeePolicy.PageSize + 1, all.Distinct().Count());
            Assert.Equal(all.OrderBy(id => id.ToString("D"), StringComparer.Ordinal), all);
        }
        finally
        {
            await ExecuteAdminAsync(
                """
                DELETE FROM drivers.driver_profiles p USING identity.users u
                WHERE p.user_id=u.id AND u.identity_subject LIKE @pattern;
                DELETE FROM identity.users WHERE identity_subject LIKE @pattern;
                """,
                new NpgsqlParameter("pattern", prefix + "%"));
        }
    }

    private static ListSettlementPayeesQuery Payees(Guid actorId, SettlementScenario scenario) =>
        new(actorId, scenario.OrganizationId, false, null);

    private async Task ExecuteAdminAsync(string sql, params NpgsqlParameter[] parameters)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(sql);
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync();
    }
}
