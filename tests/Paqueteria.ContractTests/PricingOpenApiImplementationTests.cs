using System.Globalization;
using System.Reflection;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Paqueteria.Application.Contacts;
using Paqueteria.Application.Idempotency;
using Paqueteria.ContractTests.Support;
using Pricing.Application.Quotes;
using Pricing.Endpoints;

namespace Paqueteria.ContractTests;

public sealed class PricingOpenApiImplementationTests
{
    [Fact]
    public void Implementation_exposes_only_the_two_normative_quote_operations()
    {
        var source = ReadRepositoryFile("src", "Modules", "Pricing", "Pricing.Endpoints", "QuoteEndpoints.cs");
        Assert.Equal(1, Count(source, "endpoints.MapPost("));
        Assert.Equal(1, Count(source, "endpoints.MapGet("));
        Assert.Contains("MapPost(\"/api/v1/quotes\"", source, StringComparison.Ordinal);
        Assert.Contains("MapGet(\"/api/v1/quotes/{quoteId:guid}\"", source, StringComparison.Ordinal);
        Assert.Equal(2, Count(source, ".RequireTenantContext(StatusCodes.Status403Forbidden)"));
        Assert.Contains("request.Headers[\"Idempotency-Key\"]", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Request_and_response_DTOs_match_AI05_without_internal_or_PII_fields()
    {
        AssertJsonProperties<CreateQuoteRequest>(
            "client_account_id", "consolidated_route", "destination", "low_price_authorization", "origin", "packages",
            "service_type");
        AssertJsonProperties<LowPriceAuthorizationInput>("reason");
        AssertJsonProperties<LowPriceAuthorizationResponse>("actor_id", "reason", "valid_until");
        AssertJsonProperties<AddressInput>("address_text", "contact_name", "lat", "lng", "phone", "references");
        // NTF-RECIPIENT-NOTICES-2026-10-20 (AI-08 NTF-002): only the destination accepts the optional e-mail.
        AssertJsonProperties<DestinationAddressInput>(
            "address_text", "contact_name", "email", "lat", "lng", "phone", "references");
        Assert.Equal(typeof(AddressInput), typeof(CreateQuoteRequest).GetProperty(nameof(CreateQuoteRequest.Origin))!.PropertyType);
        Assert.Equal(
            typeof(DestinationAddressInput),
            typeof(CreateQuoteRequest).GetProperty(nameof(CreateQuoteRequest.Destination))!.PropertyType);
        AssertJsonProperties<PackageInput>(
            "declared_value_cents", "description", "height_mm", "length_mm", "weight_grams", "width_mm");
        AssertJsonProperties<QuoteResponse>(
            "breakdown", "city_id", "consolidated_route", "destination_location_id", "expires_at", "id",
            "low_price_authorization", "minimum_total_cents_snapshot", "net", "origin_location_id", "package_snapshot", "pricing_policy_version",
            "pricing_tier", "request_snapshot_redacted", "rule_ids", "service_area_id", "service_type", "status", "tax", "total");

        var response = typeof(QuoteResponse).GetProperties().Select(property => property.Name).ToArray();
        Assert.DoesNotContain(response, name => name.Contains("Owner", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(response, name => name.Contains("ClientAccount", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(response, name => name.Contains("Hash", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(response, name => name.Contains("Cipher", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(response, name => name.Contains("Financial", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(response, name => name.Contains("Email", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(response, name => name.Contains("Notice", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// NTF-RECIPIENT-NOTICES-2026-10-20 (AI-05 x-pilot-contract-deltas NTF-RECIPIENT-NOTICES, part NTF-002): the
    /// destination is DestinationAddressInput (AddressInput plus the optional email), the origin stays AddressInput,
    /// and the e-mail is never part of a quote answer.
    /// </summary>
    [Fact]
    public void Only_the_destination_of_createQuote_declares_the_optional_email()
    {
        var root = YamlNodes.LoadMapping(RepositoryPaths.Normative("contracts", "AI-05_OPENAPI.yaml"));
        var schemas = root.Mapping("components").Mapping("schemas");
        var request = schemas.Mapping("CreateQuoteRequest").Mapping("properties");
        Assert.Equal("#/components/schemas/AddressInput", request.Mapping("origin").Scalar("$ref"));
        Assert.Equal("#/components/schemas/DestinationAddressInput", request.Mapping("destination").Scalar("$ref"));

        var parts = schemas.Mapping("DestinationAddressInput").Sequence("allOf").Children.OfType<YamlDotNet.RepresentationModel.YamlMappingNode>().ToArray();
        Assert.Equal(2, parts.Length);
        Assert.Equal("#/components/schemas/AddressInput", parts[0].Scalar("$ref"));
        var extension = parts[1].Mapping("properties");
        Assert.Equal(["email"], extension.Children.Keys.Select(key => key.ToString()));
        Assert.False(parts[1].Children.ContainsKey(new YamlDotNet.RepresentationModel.YamlScalarNode("required")));
        Assert.False(schemas.Mapping("AddressInput").Mapping("properties").Children.ContainsKey(
            new YamlDotNet.RepresentationModel.YamlScalarNode("email")));
        Assert.DoesNotContain(
            "email",
            schemas.Mapping("Quote").Mapping("properties").Children.Keys.Select(key => key.ToString()));
    }

    /// <summary>
    /// The AI-05 DestinationAddressInput.email pattern and length bound describe exactly what
    /// <see cref="RecipientEmailPolicy"/> accepts (on the raw text, before trimming).
    /// </summary>
    [Theory]
    [InlineData("destinatario@example.invalid", true)]
    [InlineData("  Destinatario@Example.INVALID\t", true)]
    [InlineData("\r\n a.b+c@sub.example.mx \n", true)]
    [InlineData("x@a.b", true)]
    [InlineData("a@b..c", true)]
    [InlineData("o'brien!#$%&*+-/=?^_`{|}~@example.com", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("destinatario", false)]
    [InlineData("@example.com", false)]
    [InlineData("destinatario@", false)]
    [InlineData("destinatario@example", false)]
    [InlineData("destinatario@.com", false)]
    [InlineData("destinatario@example.", false)]
    [InlineData("destinatario@@example.com", false)]
    [InlineData("des@tinatario@example.com", false)]
    [InlineData("desti natario@example.com", false)]
    [InlineData("destinat\u00e1rio@example.com", false)]
    [InlineData("\u00a0destinatario@example.com", false)]
    [InlineData("destinatario@example.com\u0000", false)]
    [InlineData("destinatario@example.com\u2028", false)]
    public void Email_contract_pattern_matches_the_recipient_email_policy(string email, bool valid)
    {
        var root = YamlNodes.LoadMapping(RepositoryPaths.Normative("contracts", "AI-05_OPENAPI.yaml"));
        var schema = root.Mapping("components").Mapping("schemas").Mapping("DestinationAddressInput")
            .Sequence("allOf").Children.OfType<YamlDotNet.RepresentationModel.YamlMappingNode>().Last()
            .Mapping("properties").Mapping("email");
        var pattern = new Regex(schema.Scalar("pattern"), RegexOptions.CultureInvariant);
        var maximumLength = int.Parse(schema.Scalar("maxLength"), CultureInfo.InvariantCulture);

        Assert.Equal(RecipientEmailPolicy.MaximumLength, maximumLength);
        Assert.Equal(RecipientEmailPolicy.Pattern, schema.Scalar("pattern"));
        Assert.Equal(valid, RecipientEmailPolicy.IsValid(email));
        Assert.Equal(valid, email.Length <= maximumLength && pattern.IsMatch(email));
    }

    [Fact]
    public void The_email_length_bound_of_the_contract_is_the_policy_bound()
    {
        var atLimit = new string('a', 254 - "@example.com".Length) + "@example.com";
        Assert.True(RecipientEmailPolicy.IsValid(atLimit));
        Assert.False(RecipientEmailPolicy.IsValid("a" + atLimit));
    }

    [Fact]
    public void Quote_request_DTOs_never_print_personal_values()
    {
        var destination = new DestinationAddressInput(
            "Calle Sintetica 200", "Destino Sintetico", "6672222222", 24.81, -107.41, null, "destinatario@example.invalid");
        foreach (var printed in new[] { destination.ToString(), destination.ToAddress().ToString() })
        {
            Assert.DoesNotContain("destinatario@example.invalid", printed, StringComparison.Ordinal);
            Assert.DoesNotContain("6672222222", printed, StringComparison.Ordinal);
            Assert.DoesNotContain("Calle Sintetica", printed, StringComparison.Ordinal);
            Assert.DoesNotContain("Destino Sintetico", printed, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Shared_idempotency_policy_matches_normative_limits()
    {
        Assert.Equal(16, IdempotencyKeyPolicy.MinimumLength);
        Assert.Equal(128, IdempotencyKeyPolicy.MaximumLength);
    }

    /// <summary>
    /// ORD-PROHIBITED-GOODS-PHONE-MX-2026-10-02 and ORD-PHONE-PLUS52-LOCATIONS-2026-10-03: the AI-05
    /// AddressInput.phone pattern and length bound describe exactly what <see cref="QuotePhonePolicy"/> accepts.
    /// </summary>
    [Theory]
    [InlineData("6671234567", true)]
    [InlineData("667 123 4567", true)]
    [InlineData("667-123-4567", true)]
    [InlineData(" 667 - 123 - 4567 ", true)]
    [InlineData("+52 667 123 4567", true)]
    [InlineData("+52-6671234567", true)]
    [InlineData("+526671234567", true)]
    [InlineData(" +52 - 667 123 4567", true)]
    [InlineData("+1 667 123 4567", false)]
    [InlineData("+ 52 667 123 4567", false)]
    [InlineData("+52 1 667 123 4567", false)]
    [InlineData("+52+52 667 123 4567", false)]
    [InlineData("667 123 4567 +52", false)]
    [InlineData("+52", false)]
    [InlineData("+52--------------------6671234567", false)]
    [InlineData("526671234567", false)]
    [InlineData("667123456", false)]
    [InlineData("66712345678", false)]
    [InlineData("(667) 123 4567", false)]
    [InlineData("667.123.4567", false)]
    [InlineData("667\t123\t4567", false)]
    [InlineData("６６７１２３４５６７", false)]
    [InlineData("", false)]
    [InlineData("-------------------------6671234567", false)]
    public void Phone_contract_pattern_matches_the_quote_phone_policy(string phone, bool valid)
    {
        var root = YamlNodes.LoadMapping(RepositoryPaths.Normative("contracts", "AI-05_OPENAPI.yaml"));
        var schema = root.Mapping("components").Mapping("schemas").Mapping("AddressInput")
            .Mapping("properties").Mapping("phone");
        var pattern = new Regex(schema.Scalar("pattern"), RegexOptions.CultureInvariant);
        var maximumLength = int.Parse(schema.Scalar("maxLength"), CultureInfo.InvariantCulture);

        Assert.Equal(QuotePhonePolicy.MaximumInputLength, maximumLength);
        Assert.Equal(valid, QuotePhonePolicy.IsValid(phone));
        Assert.Equal(valid, phone.Length <= maximumLength && pattern.IsMatch(phone));
    }

    private static void AssertJsonProperties<T>(params string[] expected)
    {
        var actual = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name)
            .Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(expected.Order(StringComparer.Ordinal), actual);
    }

    private static int Count(string value, string fragment) =>
        value.Split(fragment, StringSplitOptions.None).Length - 1;

    private static string ReadRepositoryFile(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine([directory.FullName, .. segments]);
            if (File.Exists(path)) return File.ReadAllText(path);
            directory = directory.Parent;
        }
        throw new FileNotFoundException(string.Join('/', segments));
    }
}
