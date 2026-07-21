using System.Text.Json;
using Npgsql;
using Paqueteria.ContractTests.PostgreSql.Fixtures;
using Paqueteria.ContractTests.Support;
using YamlDotNet.RepresentationModel;

namespace Paqueteria.ContractTests.PostgreSql.Tracking;

[Collection(PostgreSqlContractCollection.Name)]
[Trait("Category", "PostgreSqlContract")]
public sealed class PublicTrackingContractTests(PostgreSqlContractFixture fixture)
{
    [PostgreSqlContractFact]
    public async Task Sql_and_csharp_map_exactly_all_17_internal_states_and_fail_closed_for_unknown()
    {
        var domain = YamlNodes.LoadMapping(RepositoryPaths.Normative("specs", "AI-04_DOMAIN_MODEL.yaml"));
        var normativeMapping = domain.Mapping("public_tracking_contract")
            .Mapping("internal_to_public")
            .Children
            .ToDictionary(
                pair => ((YamlScalarNode)pair.Key).Value!,
                pair => ((YamlScalarNode)pair.Value).Value!,
                StringComparer.Ordinal);

        Assert.Equal(17, PublicStatusMappingReference.All.Count);
        Assert.Equal(
            normativeMapping.OrderBy(pair => pair.Key, StringComparer.Ordinal),
            PublicStatusMappingReference.All.OrderBy(pair => pair.Key, StringComparer.Ordinal));
        await using var command = fixture.AdminDataSource.CreateCommand(
            "SELECT security.map_public_order_status(@status)");
        var statusParameter = command.Parameters.Add("status", NpgsqlTypes.NpgsqlDbType.Text);
        foreach (var mapping in PublicStatusMappingReference.All)
        {
            statusParameter.Value = mapping.Key;
            Assert.Equal(mapping.Value, await command.ExecuteScalarAsync());
            Assert.Equal(mapping.Value, PublicStatusMappingReference.Map(mapping.Key));
        }

        statusParameter.Value = "UNMAPPED_SYNTHETIC_STATE";
        var unmappedStatus = await command.ExecuteScalarAsync();
        Assert.True(unmappedStatus is null or DBNull);
        Assert.Throws<PublicStatusMappingException>(() => PublicStatusMappingReference.Map("UNMAPPED_SYNTHETIC_STATE"));
    }

    [PostgreSqlContractFact]
    public async Task Public_tracking_projection_is_minimal_ordered_and_indistinguishably_fail_closed()
    {
        await using var scenario = new SyntheticOrderScenario(fixture);
        await scenario.InitializeAsync(orderStatus: "DELIVERING");
        const string validToken = "AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA";
        const string expiredToken = "expired-token-arc002";
        const string revokedToken = "revoked-token-arc002";
        var validTokenId = Guid.NewGuid();
        var expiredTokenId = Guid.NewGuid();
        var revokedTokenId = Guid.NewGuid();
        await scenario.ExecuteAdminAsync("""
            INSERT INTO orders.public_tracking_tokens(id,order_id,owner_org_id,token_hash,expires_at,revoked_at) VALUES
              (@valid_id,@order,@org,extensions.digest(pg_catalog.convert_to(@valid,'UTF8'),'sha256'),clock_timestamp()+interval '1 day',NULL),
              (@expired_id,@order,@org,extensions.digest(pg_catalog.convert_to(@expired,'UTF8'),'sha256'),clock_timestamp()-interval '1 second',NULL),
              (@revoked_id,@order,@org,extensions.digest(pg_catalog.convert_to(@revoked,'UTF8'),'sha256'),clock_timestamp()+interval '1 day',clock_timestamp());
            INSERT INTO orders.order_events(id,order_id,owner_org_id,aggregate_version,event_type,public_event_code,payload,occurred_at) VALUES
              (@private_event,@order,@org,1,'INTERNAL_DIAGNOSTIC',NULL,'{"private":"must-not-leak"}',clock_timestamp()-interval '3 minutes'),
              (@public_event_2,@order,@org,3,'OUT_FOR_DELIVERY','OUT_FOR_DELIVERY','{"private":"must-not-leak"}',clock_timestamp()-interval '1 minute'),
              (@public_event_1,@order,@org,2,'PICKED_UP','PICKED_UP','{"private":"must-not-leak"}',clock_timestamp()-interval '2 minutes');
            """,
            SyntheticOrderScenario.P("valid_id", validTokenId), SyntheticOrderScenario.P("expired_id", expiredTokenId),
            SyntheticOrderScenario.P("revoked_id", revokedTokenId), SyntheticOrderScenario.P("order", scenario.OrderId),
            SyntheticOrderScenario.P("org", scenario.OrganizationId), SyntheticOrderScenario.P("valid", validToken),
            SyntheticOrderScenario.P("expired", expiredToken), SyntheticOrderScenario.P("revoked", revokedToken),
            SyntheticOrderScenario.P("private_event", Guid.NewGuid()), SyntheticOrderScenario.P("public_event_1", Guid.NewGuid()),
            SyntheticOrderScenario.P("public_event_2", Guid.NewGuid()));

        var projection = Assert.IsType<JsonDocument>(await ReadProjectionAsAppAsync(validToken));
        using (projection)
        {
            var root = projection.RootElement;
            Assert.Equal(scenario.PublicOrderId, root.GetProperty("public_id").GetString());
            Assert.Equal("OUT_FOR_DELIVERY", root.GetProperty("public_status").GetString());
            var timeline = root.GetProperty("timeline").EnumerateArray().ToArray();
            Assert.Equal(2, timeline.Length);
            Assert.Equal("PICKED_UP", timeline[0].GetProperty("code").GetString());
            Assert.Equal("OUT_FOR_DELIVERY", timeline[1].GetProperty("code").GetString());
            Assert.All(timeline, item => Assert.Equal(2, item.EnumerateObject().Count()));
            Assert.DoesNotContain("private", root.GetRawText(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(scenario.OrganizationId.ToString(), root.GetRawText(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(scenario.OrderId.ToString(), root.GetRawText(), StringComparison.OrdinalIgnoreCase);
        }

        Assert.Null(await ReadProjectionAsAppAsync(validToken[..^1] + "B"));
        Assert.Null(await ReadProjectionAsAppAsync("nonexistent-token-arc002"));
        Assert.Null(await ReadProjectionAsAppAsync(expiredToken));
        Assert.Null(await ReadProjectionAsAppAsync(revokedToken));
    }

    private async Task<JsonDocument?> ReadProjectionAsAppAsync(string token)
    {
        await using var connection = await fixture.AppDataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var role = new NpgsqlCommand("SET LOCAL ROLE paqueteria_app", connection, transaction))
        {
            await role.ExecuteNonQueryAsync();
        }

        await using var command = new NpgsqlCommand(
            "SELECT security.get_public_tracking_projection(@token)::text",
            connection,
            transaction);
        command.Parameters.AddWithValue("token", token);
        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? null : JsonDocument.Parse((string)result);
    }
}
