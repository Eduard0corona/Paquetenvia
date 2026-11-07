using System.Text;
using System.Text.Json.Nodes;

namespace Paqueteria.ContractTests;

/// <summary>
/// MDM-001-DRIVER-SUB-2026-11-06 (S7), the job's validator, no database: a driver-profile entry carries exactly one
/// of <c>user_id</c> or <c>identity_subject</c> (the AuthCenter <c>sub</c>, a lowercase UUID other than the nil UUID),
/// in the unchanged <c>paquetenvia.master-data.v1</c> format. Errors name the reference and a code, never the value.
/// The loader function repeats the format check and resolves the subject (<c>MasterDataLoaderPostgreSqlContractTests</c>).
/// </summary>
public sealed class MasterDataDriverIdentifierValidatorTests
{
    private static readonly Guid Organization = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private const string Subject = "6f1c2a4e-9b3d-4c5e-8f70-1a2b3c4d5e6f";

    [Fact]
    public void A_driver_named_by_user_id_is_still_valid()
    {
        Assert.Empty(Validate(Profile("user_id", "3b8f5a1c-2d4e-4f60-9a7b-8c9d0e1f2a3b")).Errors);
    }

    [Fact]
    public void A_driver_named_by_the_AuthCenter_sub_is_valid_in_format_v1()
    {
        var result = Validate(Profile("identity_subject", Subject));

        Assert.Empty(result.Errors);
        Assert.True(result.HasDriverProfiles);
        Assert.Equal("paquetenvia.master-data.v1", MasterDataDocumentValidator.Format);
    }

    [Fact]
    public void Both_identifiers_or_neither_are_refused()
    {
        var both = Profile("identity_subject", Subject);
        both["user_id"] = "3b8f5a1c-2d4e-4f60-9a7b-8c9d0e1f2a3b";
        var neither = Profile("identity_subject", Subject);
        neither.Remove("identity_subject");

        Assert.Equal(["driver_profiles[1]: MDM001_DRIVER_IDENTIFIER"], Validate(both).Errors);
        Assert.Equal(["driver_profiles[1]: MDM001_DRIVER_IDENTIFIER"], Validate(neither).Errors);
    }

    [Theory]
    [InlineData("\"6F1C2A4E-9B3D-4C5E-8F70-1A2B3C4D5E6F\"")]
    [InlineData("\"00000000-0000-0000-0000-000000000000\"")]
    [InlineData("\"6f1c2a4e9b3d4c5e8f701a2b3c4d5e6f\"")]
    [InlineData("\" 6f1c2a4e-9b3d-4c5e-8f70-1a2b3c4d5e6f\"")]
    [InlineData("\"auth0|synthetic-subject\"")]
    [InlineData("\"\"")]
    [InlineData("7")]
    [InlineData("null")]
    public void A_subject_other_than_a_lowercase_uuid_is_refused_without_echoing_it(string json)
    {
        var profile = Profile("identity_subject", Subject);
        profile["identity_subject"] = JsonNode.Parse(json);

        var errors = Validate(profile).Errors;

        Assert.Equal(["driver_profiles[1].identity_subject: MDM001_IDENTITY_SUBJECT"], errors);
        if (json.Length > 2 && json[0] == '"')
        {
            Assert.DoesNotContain(json.Trim('"').Trim(), string.Join('\n', errors), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_subject_named_twice_is_a_duplicate_and_unknown_properties_are_still_refused()
    {
        var twice = Validate(Profile("identity_subject", Subject), Profile("identity_subject", Subject));
        Assert.Equal(["driver_profiles[2]: MDM001_DUPLICATE_NATURAL_KEY"], twice.Errors);

        // The same UUID as a user_id in one entry and a subject in another is not a duplicate here: only the
        // loader function knows which user a subject names.
        Assert.Empty(Validate(Profile("user_id", Subject), Profile("identity_subject", Subject)).Errors);

        var extra = Profile("identity_subject", Subject);
        extra["nickname"] = "Sintetico";
        Assert.Equal(["driver_profiles[1]: MDM001_ENTRY_SHAPE"], Validate(extra).Errors);
    }

    private static JsonObject Profile(string identifier, string value) => new()
    {
        [identifier] = value,
        ["home_city"] = new JsonObject { ["country_code"] = "MX", ["state_code"] = "SIN", ["name"] = "Ciudad Sintetica MDM" },
        ["driver_type"] = "OWN",
        ["vehicle_type"] = "MOTORCYCLE",
        ["status"] = "PENDING",
        ["service_areas"] = new JsonArray(new JsonObject
        {
            ["city"] = new JsonObject { ["country_code"] = "MX", ["state_code"] = "SIN", ["name"] = "Ciudad Sintetica MDM" },
            ["name"] = "Area Sintetica MDM",
            ["status"] = "ACTIVE",
        }),
    };

    private static MasterDataValidationResult Validate(params JsonObject[] profiles)
    {
        var document = new JsonObject
        {
            ["format"] = MasterDataDocumentValidator.Format,
            ["classification"] = "SYNTHETIC",
            ["owner_org_id"] = Organization.ToString("D"),
            ["cities"] = new JsonArray(),
            ["service_areas"] = new JsonArray(),
            ["operating_zones"] = new JsonArray(),
            ["tariff_rules"] = new JsonArray(),
            ["driver_profiles"] = new JsonArray(profiles.Select(profile => (JsonNode)profile.DeepClone()).ToArray()),
        };
        return MasterDataDocumentValidator.Validate(Encoding.UTF8.GetBytes(document.ToJsonString()), Organization);
    }
}
