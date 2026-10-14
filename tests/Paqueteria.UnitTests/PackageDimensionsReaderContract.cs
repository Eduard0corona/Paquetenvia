namespace Paqueteria.UnitTests;

/// <summary>
/// The cases every reader of <c>orders.package_items.dimensions_mm</c> must agree on. Orders, Dispatch and Routing
/// each keep their own reader (module boundaries), so each module derives from this contract and runs the same
/// cases against its own copy.
/// <para>
/// A JSON <c>null</c> is the same as a missing key: no measurement. Order creation stores a package without
/// measurements as <c>{"length_mm":null,"width_mm":null,"height_mm":null}</c>, and such an order must stay
/// assignable and routable. Every other value is as strict as before.
/// </para>
/// </summary>
public abstract class PackageDimensionsReaderContract
{
    protected abstract bool TryRead(string json, out int? length, out int? width, out int? height);

    public static TheoryData<string, int?, int?, int?> ReadableDocuments => new()
    {
        // What order creation writes for a package without measurements.
        { """{"length_mm":null,"width_mm":null,"height_mm":null}""", null, null, null },
        // Some measurements, some JSON nulls.
        { """{"length_mm":120,"width_mm":null,"height_mm":60}""", 120, null, 60 },
        { """{"length_mm":null,"width_mm":80,"height_mm":null}""", null, 80, null },
        // A missing key.
        { """{"length_mm":120,"height_mm":60}""", 120, null, 60 },
        { """{"width_mm":null}""", null, null, null },
        // The AI-06 column default.
        { "{}", null, null, null },
        // Every measurement present.
        { """{"length_mm":100,"width_mm":80,"height_mm":60}""", 100, 80, 60 },
        { """{"length_mm":1,"width_mm":2147483647,"height_mm":1}""", 1, int.MaxValue, 1 },
    };

    public static TheoryData<string> InvalidValues => new()
    {
        // A string, zero, a negative, a fraction and an int overflow stay invalid.
        """{"length_mm":"100","width_mm":80,"height_mm":60}""",
        """{"length_mm":100,"width_mm":0,"height_mm":60}""",
        """{"length_mm":100,"width_mm":80,"height_mm":-1}""",
        """{"length_mm":1.5,"width_mm":80,"height_mm":60}""",
        """{"length_mm":2147483648,"width_mm":80,"height_mm":60}""",
        """{"length_mm":100,"width_mm":80,"height_mm":-2147483649}""",
        // Other JSON kinds are not a measurement either.
        """{"length_mm":true}""",
        """{"width_mm":{}}""",
        """{"height_mm":[]}""",
        """{"length_mm":""}""",
        // A null never hides an invalid neighbour.
        """{"length_mm":null,"width_mm":"80","height_mm":null}""",
        """{"length_mm":null,"width_mm":null,"height_mm":0}""",
    };

    public static TheoryData<string> InvalidDocuments => new()
    {
        // Only an object is a dimensions document.
        "null",
        "[]",
        """[{"length_mm":100}]""",
        "\"length_mm\"",
        "42",
        "true",
        // Malformed JSON.
        "",
        "{",
        """{"length_mm":null""",
    };

    [Theory]
    [MemberData(nameof(ReadableDocuments))]
    public void A_null_or_missing_measurement_is_read_as_no_measurement(
        string json,
        int? expectedLength,
        int? expectedWidth,
        int? expectedHeight)
    {
        Assert.True(TryRead(json, out var length, out var width, out var height));
        Assert.Equal(expectedLength, length);
        Assert.Equal(expectedWidth, width);
        Assert.Equal(expectedHeight, height);
    }

    [Theory]
    [MemberData(nameof(InvalidValues))]
    public void A_value_that_is_not_a_positive_int_is_still_rejected(string json)
    {
        Assert.False(TryRead(json, out _, out _, out _));
    }

    [Theory]
    [MemberData(nameof(InvalidDocuments))]
    public void A_document_that_is_not_an_object_is_still_rejected(string json)
    {
        Assert.False(TryRead(json, out _, out _, out _));
    }
}
