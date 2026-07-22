using System.Text.Json;
using Paqueteria.Application.Auditing;

namespace Paqueteria.UnitTests.Auditing;

public sealed class AuditPayloadRedactorTests
{
    private readonly AuditPayloadRedactor redactor = new();

    [Theory]
    [InlineData("password")]
    [InlineData("AccessToken")]
    [InlineData("refresh_token")]
    [InlineData("AUTHORIZATION_HEADER")]
    [InlineData("cookies")]
    [InlineData("api-key")]
    [InlineData("privateKey")]
    [InlineData("clientSecret")]
    [InlineData("connection_string")]
    [InlineData("identitySubject")]
    [InlineData("email")]
    [InlineData("phoneNumber")]
    [InlineData("streetAddress")]
    [InlineData("fullName")]
    public void Sensitive_field_names_are_redacted_case_insensitively(string fieldName)
    {
        const string syntheticSecret = "synthetic-sensitive-value";
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            [fieldName] = syntheticSecret,
            ["safeField"] = "safe-value",
        }));

        var result = redactor.Redact(document.RootElement);

        Assert.DoesNotContain(syntheticSecret, result.Json, StringComparison.Ordinal);
        using var output = JsonDocument.Parse(result.Json);
        Assert.Equal(AuditPayloadRedactor.Replacement, output.RootElement.GetProperty(fieldName).GetString());
        Assert.Equal("safe-value", output.RootElement.GetProperty("safeField").GetString());
    }

    [Fact]
    public void Nested_objects_and_arrays_are_redacted_without_mutating_input()
    {
        const string input =
            """
            {"operation":"create","nested":{"PASSWORD":"synthetic-password"},"items":[{"Email":"person@example.test"},{"label":"safe"}]}
            """;
        using var document = JsonDocument.Parse(input);
        var before = document.RootElement.GetRawText();

        var result = redactor.Redact(document.RootElement);

        Assert.Equal(before, document.RootElement.GetRawText());
        Assert.DoesNotContain("synthetic-password", result.Json, StringComparison.Ordinal);
        Assert.DoesNotContain("person@example.test", result.Json, StringComparison.Ordinal);
        using var output = JsonDocument.Parse(result.Json);
        Assert.Equal(
            AuditPayloadRedactor.Replacement,
            output.RootElement.GetProperty("nested").GetProperty("PASSWORD").GetString());
        Assert.Equal(
            AuditPayloadRedactor.Replacement,
            output.RootElement.GetProperty("items")[0].GetProperty("Email").GetString());
    }

    [Fact]
    public void Output_is_deterministic_and_property_order_is_stable()
    {
        using var first = JsonDocument.Parse("{\"z\":2,\"password\":\"one\",\"a\":1}");
        using var second = JsonDocument.Parse("{\"a\":1,\"password\":\"two\",\"z\":2}");

        var firstResult = redactor.Redact(first.RootElement).Json;
        var secondResult = redactor.Redact(second.RootElement).Json;

        Assert.Equal(firstResult, secondResult);
        Assert.Equal("{\"a\":1,\"password\":\"[REDACTED]\",\"z\":2}", firstResult);
    }

    [Theory]
    [InlineData("person@example.test")]
    [InlineData("+52 667 000 0000")]
    [InlineData("Bearer synthetic-access-token")]
    [InlineData("aaaa.bbbb.cccc")]
    [InlineData("-----BEGIN PRIVATE KEY-----")]
    [InlineData("Host=database;Password=synthetic")]
    public void Sensitive_string_shapes_are_redacted_even_in_arrays(string value)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new[] { value }));

        var result = redactor.Redact(document.RootElement);

        Assert.DoesNotContain(value, result.Json, StringComparison.Ordinal);
        using var output = JsonDocument.Parse(result.Json);
        Assert.Equal(AuditPayloadRedactor.Replacement, output.RootElement[0].GetString());
    }

    [Fact]
    public void Empty_payload_scalars_and_unicode_remain_valid_json()
    {
        using var empty = JsonDocument.Parse("{}");
        using var scalar = JsonDocument.Parse("42");
        using var unicode = JsonDocument.Parse("{\"label\":\"entrega sintética 🚚\"}");

        Assert.Equal("{}", redactor.Redact(empty.RootElement).Json);
        Assert.Equal("42", redactor.Redact(scalar.RootElement).Json);
        var unicodeResult = redactor.Redact(unicode.RootElement).Json;
        using var parsed = JsonDocument.Parse(unicodeResult);
        Assert.Equal("entrega sintética 🚚", parsed.RootElement.GetProperty("label").GetString());
    }

    [Fact]
    public void Excessive_depth_fails_closed_without_echoing_sensitive_input()
    {
        const string syntheticSecret = "synthetic-depth-secret";
        using var document = JsonDocument.Parse($"{{\"a\":{{\"b\":{{\"c\":\"{syntheticSecret}\"}}}}}}");
        var limited = new AuditPayloadRedactor(new AuditRedactionOptions(MaximumDepth: 1));

        var exception = Assert.Throws<AuditRedactionException>(() => limited.Redact(document.RootElement));

        Assert.DoesNotContain(syntheticSecret, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Excessive_size_fails_closed_without_echoing_sensitive_input()
    {
        const string syntheticSecret = "synthetic-size-secret-that-must-not-escape";
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { value = syntheticSecret }));
        var limited = new AuditPayloadRedactor(new AuditRedactionOptions(MaximumUtf8Bytes: 8));

        var exception = Assert.Throws<AuditRedactionException>(() => limited.Redact(document.RootElement));

        Assert.DoesNotContain(syntheticSecret, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Undefined_json_fails_closed_with_a_generic_exception()
    {
        var exception = Assert.Throws<AuditRedactionException>(() => redactor.Redact(default));

        Assert.Equal("The audit payload could not be redacted safely.", exception.Message);
        Assert.Null(exception.InnerException);
    }
}
