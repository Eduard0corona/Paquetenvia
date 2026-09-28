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

    [Theory]
    [InlineData("12345678-1234-4123-8123-123456789012")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("99999999-9999-9999-9999-999999999999")]
    [InlineData("3f2a1b4c-5512-4345-8678-667123456789")]
    [InlineData("ABCDEF01-2345-6789-ABCD-6671234567AB")]
    [InlineData("{12345678-1234-4123-8123-123456789012}")]
    public void Uuids_with_digit_runs_are_preserved_under_any_key(string uuid)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["value"] = uuid,
            ["list"] = new[] { uuid },
            ["note"] = $"token {uuid} revoked",
        }));

        var result = redactor.Redact(document.RootElement);

        using var output = JsonDocument.Parse(result.Json);
        Assert.Equal(uuid, output.RootElement.GetProperty("value").GetString());
        Assert.Equal(uuid, output.RootElement.GetProperty("list")[0].GetString());
        Assert.Equal($"token {uuid} revoked", output.RootElement.GetProperty("note").GetString());
    }

    [Fact]
    public void Random_uuids_are_never_redacted()
    {
        for (var i = 0; i < 5_000; i++)
        {
            var id = Guid.NewGuid().ToString("D");
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { token_id = id, value = id }));

            var result = redactor.Redact(document.RootElement);

            using var output = JsonDocument.Parse(result.Json);
            Assert.Equal(id, output.RootElement.GetProperty("token_id").GetString());
            Assert.Equal(id, output.RootElement.GetProperty("value").GetString());
        }
    }

    [Theory]
    [InlineData("id")]
    [InlineData("ID")]
    [InlineData("request_id")]
    [InlineData("entity_id")]
    [InlineData("order-id")]
    [InlineData("orderId")]
    [InlineData("OrderID")]
    [InlineData("externalReferenceId")]
    [InlineData("token_ids")]
    [InlineData("orderIds")]
    public void Identifier_fields_keep_numeric_identifiers(string fieldName)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            [fieldName] = "6671234567",
            ["nested"] = new Dictionary<string, object?> { [fieldName] = new[] { "5512345678", "20260928001" } },
        }));

        var result = redactor.Redact(document.RootElement);

        using var output = JsonDocument.Parse(result.Json);
        Assert.Equal("6671234567", output.RootElement.GetProperty(fieldName).GetString());
        var nested = output.RootElement.GetProperty("nested").GetProperty(fieldName);
        Assert.Equal("5512345678", nested[0].GetString());
        Assert.Equal("20260928001", nested[1].GetString());
    }

    [Theory]
    [InlineData("paid")]
    [InlineData("valid")]
    [InlineData("note")]
    public void Words_ending_in_id_are_not_identifier_fields(string fieldName)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            [fieldName] = "667 123 4567",
        }));

        var result = redactor.Redact(document.RootElement);

        Assert.DoesNotContain("667 123 4567", result.Json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("request_id", "+52 667 123 4567")]
    [InlineData("orderId", "person@example.test")]
    [InlineData("entity_id", "aaaa.bbbb.cccc")]
    public void Identifier_fields_still_redact_explicit_phones_and_other_pii(string fieldName, string value)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            [fieldName] = value,
        }));

        var result = redactor.Redact(document.RootElement);

        Assert.DoesNotContain(value, result.Json, StringComparison.Ordinal);
        using var output = JsonDocument.Parse(result.Json);
        Assert.Equal(AuditPayloadRedactor.Replacement, output.RootElement.GetProperty(fieldName).GetString());
    }

    [Theory]
    [InlineData("+526671234567")]
    [InlineData("+52 667 123 4567")]
    [InlineData("+52 1 667 123 4567")]
    [InlineData("+52 (667) 123-4567")]
    [InlineData("+52-55-1234-5678")]
    [InlineData("+1 (555) 123-4567")]
    [InlineData("+44 20 7946 0958")]
    [InlineData("+34.612.345.678")]
    [InlineData("6671234567")]
    [InlineData("667 123 4567")]
    [InlineData("667-123-4567")]
    [InlineData("667.123.4567")]
    [InlineData("(667) 123-4567")]
    [InlineData("(55) 1234 5678")]
    [InlineData("55 1234 5678")]
    [InlineData("55-1234-5678")]
    [InlineData("667 123 45 67")]
    [InlineData("66 71 23 45 67")]
    [InlineData("52 667 123 4567")]
    [InlineData("521 667 123 4567")]
    [InlineData("526671234567")]
    [InlineData("5216671234567")]
    public void Phone_numbers_in_free_text_are_redacted(string phone)
    {
        var text = $"Llamar al {phone} antes de entregar";
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["reason"] = text,
            ["list"] = new[] { phone },
            ["label"] = $"tel:{phone}",
        }));

        var result = redactor.Redact(document.RootElement);

        Assert.DoesNotContain(phone, result.Json, StringComparison.Ordinal);
        using var output = JsonDocument.Parse(result.Json);
        Assert.Equal(AuditPayloadRedactor.Replacement, output.RootElement.GetProperty("reason").GetString());
        Assert.Equal(AuditPayloadRedactor.Replacement, output.RootElement.GetProperty("list")[0].GetString());
        Assert.Equal(AuditPayloadRedactor.Replacement, output.RootElement.GetProperty("label").GetString());
    }

    [Fact]
    public void Phone_next_to_a_uuid_in_free_text_is_still_redacted()
    {
        const string text = "orden 12345678-1234-4123-8123-123456789012 contacto 667 123 4567";
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { reason = text }));

        var result = redactor.Redact(document.RootElement);

        Assert.DoesNotContain("667 123 4567", result.Json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("phone")]
    [InlineData("contact_phone")]
    [InlineData("contactPhoneNumber")]
    [InlineData("telefono")]
    [InlineData("telefono_contacto")]
    [InlineData("contactoTelefono")]
    [InlineData("tel")]
    [InlineData("whatsapp")]
    [InlineData("whatsapp_number")]
    [InlineData("mobile")]
    [InlineData("mobileNumber")]
    [InlineData("celular")]
    [InlineData("telefonoCelular")]
    [InlineData("movil")]
    [InlineData("msisdn")]
    public void Phone_field_names_are_redacted_whatever_the_value_shape(string fieldName)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            [fieldName] = "12345",
        }));

        var result = redactor.Redact(document.RootElement);

        using var output = JsonDocument.Parse(result.Json);
        Assert.Equal(AuditPayloadRedactor.Replacement, output.RootElement.GetProperty(fieldName).GetString());
    }

    [Theory]
    [InlineData("2026-09-28")]
    [InlineData("2026-09-28T10:15:30.1234567+00:00")]
    [InlineData("2026-09-28 10:15:30")]
    [InlineData("12345678")]
    [InlineData("123456789")]
    [InlineData("12345678901")]
    [InlineData("1234-5678-9012-3456")]
    [InlineData("3f2a1b4c55124345866766712345678a")]
    [InlineData("trk002-contract-issue-0001")]
    [InlineData("CLN-7FK3")]
    public void Non_phone_digit_runs_are_preserved(string value)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { value }));

        var result = redactor.Redact(document.RootElement);

        using var output = JsonDocument.Parse(result.Json);
        Assert.Equal(value, output.RootElement.GetProperty("value").GetString());
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
