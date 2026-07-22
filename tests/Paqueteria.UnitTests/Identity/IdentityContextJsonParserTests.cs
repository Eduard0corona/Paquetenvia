using Identity.Application.Bootstrap;
using Identity.Infrastructure.Bootstrap;

namespace Paqueteria.UnitTests.Identity;

public sealed class IdentityContextJsonParserTests
{
    [Fact]
    public void Valid_context_is_immutable_and_preserves_distinct_roles_and_default()
    {
        var context = IdentityContextJsonParser.Parse("""
            {"memberships":[
              {"role":"VIEWER","is_default":true,"organization_id":"11111111-1111-1111-1111-111111111111"},
              {"organization_id":"11111111-1111-1111-1111-111111111111","role":"FINANCE","is_default":false}],
             "status":"ACTIVE","user_id":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"}
            """);

        Assert.Equal(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), context.UserId);
        Assert.Equal(IdentityContextStatus.Active, context.Status);
        Assert.Collection(context.Memberships,
            first => Assert.True(first.IsDefault),
            second => Assert.Equal(OrganizationRole.Finance, second.Role));
    }

    [Fact]
    public void Empty_membership_array_is_valid()
    {
        var context = IdentityContextJsonParser.Parse(
            """{"user_id":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","status":"ACTIVE","memberships":[]}""");
        Assert.Empty(context.Memberships);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"user_id\":\"bad\",\"status\":\"ACTIVE\",\"memberships\":[]}")]
    [InlineData("{\"user_id\":\"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\",\"status\":\"SUSPENDED\",\"memberships\":[]}")]
    [InlineData("{\"user_id\":\"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\",\"status\":\"ACTIVE\",\"memberships\":{},\"extra\":true}")]
    public void Invalid_contract_shapes_fail_as_technical_errors(string json)
    {
        Assert.Throws<IdentityContextInfrastructureException>(() => IdentityContextJsonParser.Parse(json));
    }

    [Fact]
    public void Unknown_or_duplicate_membership_fails_closed()
    {
        const string unknown = """{"user_id":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","status":"ACTIVE","memberships":[{"organization_id":"11111111-1111-1111-1111-111111111111","role":"OWNER","is_default":true}]}""";
        const string duplicate = """{"user_id":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","status":"ACTIVE","memberships":[{"organization_id":"11111111-1111-1111-1111-111111111111","role":"VIEWER","is_default":true},{"organization_id":"11111111-1111-1111-1111-111111111111","role":"VIEWER","is_default":false}]}""";

        Assert.Throws<IdentityContextInfrastructureException>(() => IdentityContextJsonParser.Parse(unknown));
        Assert.Throws<IdentityContextInfrastructureException>(() => IdentityContextJsonParser.Parse(duplicate));
    }
}
