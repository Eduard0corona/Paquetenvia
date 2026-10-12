using System.Text;
using Dispatch.Application.Stops;
using Dispatch.Domain;

namespace Paqueteria.UnitTests.Dispatch;

/// <summary>
/// UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11: the contact name rule per stop type, the text normalization and bounds
/// the driver's stop details are sent with, and the redaction of every result that carries them.
/// </summary>
public sealed class DriverStopDetailsTests
{
    [Theory]
    [InlineData("María López", "M. López")]
    [InlineData("María López Pérez", "M. Pérez")]
    [InlineData("  maría   del   carmen  lópez ", "M. lópez")]
    [InlineData("Ana María Ruiz", "A. Ruiz")]
    [InlineData("José-Luis Ñúñez", "J. Ñúñez")]
    [InlineData("álvaro\tGarcía", "Á. García")]
    [InlineData("María", "María")]
    [InlineData("Ñ", "Ñ")]
    [InlineData("María​López", "M. López")]
    [InlineData("﻿María López﻿", "M. López")]
    public void The_short_name_is_the_first_initial_and_the_last_word(string contact, string expected)
    {
        Assert.Equal(expected, DriverStopContactName.Shorten(contact));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("﻿")]
    [InlineData("﻿​ ⁠\t")]
    public void A_name_blank_after_normalization_is_absent(string? contact)
    {
        Assert.Null(DriverStopContactName.Shorten(contact));
        Assert.Null(DriverStopContactName.Full(contact));
        foreach (var stopType in Enum.GetValues<DriverStopType>())
        {
            Assert.Equal(new DriverStopContact(null, null), DriverStopContactName.ForStop(stopType, contact));
        }
    }

    [Fact]
    public void Pickups_and_returns_show_the_full_name_and_deliveries_only_the_short_one()
    {
        // The owner's answers "Completo en recolección" and "Completo, como recolección".
        Assert.Equal(
            new DriverStopContact("Farmacia Las Quintas", null),
            DriverStopContactName.ForStop(DriverStopType.Pickup, "  Farmacia \t Las\nQuintas "));
        Assert.Equal(
            new DriverStopContact("Farmacia Las Quintas", null),
            DriverStopContactName.ForStop(DriverStopType.Return, "Farmacia﻿ Las Quintas"));
        Assert.Equal(
            new DriverStopContact(null, "M. Pérez"),
            DriverStopContactName.ForStop(DriverStopType.Delivery, "María López Pérez"));
        Assert.DoesNotContain("Farmacia", new DriverStopContact("Farmacia Las Quintas", null).ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_long_full_name_is_cut_at_its_bound_with_an_ellipsis()
    {
        var full = DriverStopContactName.Full("Farmacia " + new string('q', 400));
        Assert.Equal(DriverStopContactName.MaximumFullLength, full!.Length);
        Assert.StartsWith("Farmacia qq", full, StringComparison.Ordinal);
        Assert.EndsWith("…", full, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(62)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(200)]
    [InlineData(5_000)]
    public void A_first_grapheme_of_any_length_never_breaks_the_short_name(int combiningMarks)
    {
        // "e" followed by combining acute accents is one text element of any length; it used to make the remaining
        // room negative and throw, failing the whole stop list with 500.
        var initial = "e" + new string('́', combiningMarks);
        var shortName = DriverStopContactName.Shorten(initial + " López");
        Assert.NotNull(shortName);
        Assert.True(shortName!.Length <= DriverStopContactName.MaximumLength);
        Assert.EndsWith("López", shortName, StringComparison.Ordinal);
        // A full name whose first grapheme alone exceeds the bound has nothing left to show: it is absent.
        var fullName = DriverStopContactName.Full(initial + " López");
        Assert.True(fullName is null || fullName.Length <= DriverStopContactName.MaximumFullLength);
        Assert.Equal(combiningMarks < 199, fullName is not null);
    }

    [Fact]
    public void Pathological_names_never_throw_and_always_fit()
    {
        var names = new List<string>
        {
            new('́', 300),
            "Ana " + new string('́', 300),
            "Ana " + string.Concat(Enumerable.Repeat("👩‍💻", 40)),
            string.Concat(Enumerable.Repeat("🇲🇽", 100)) + " López",
            "x " + string.Concat(Enumerable.Repeat("é̂̃", 100)),
            new string('x', 10_000),
            string.Join(' ', Enumerable.Repeat("palabra", 2_000)),
            "\uD83D",
            "\uDC00 \uD83D",
            "مريم الحسيني",
            "‮أحمد‬ علي",
        };
        var random = new Random(20261012);
        for (var sample = 0; sample < 500; sample++)
        {
            var builder = new StringBuilder();
            var length = random.Next(0, 300);
            for (var index = 0; index < length; index++)
            {
                builder.Append(random.Next(10) switch
                {
                    0 => ' ',
                    1 => '́',
                    2 => '﻿',
                    3 => (char)random.Next(0xD800, 0xE000),
                    _ => (char)random.Next(0x20, 0x2FFF),
                });
            }

            names.Add(builder.ToString());
        }

        foreach (var name in names)
        {
            var shortName = DriverStopContactName.Shorten(name);
            var fullName = DriverStopContactName.Full(name);
            var sample = string.Join(' ', name.Take(40).Select(character => ((int)character).ToString("X4")));
            Assert.True(
                shortName is null || (shortName.Length is > 0 and <= DriverStopContactName.MaximumLength),
                $"short {shortName?.Length}: {sample}");
            Assert.True(
                fullName is null || (fullName.Length is > 0 and <= DriverStopContactName.MaximumFullLength),
                $"full {fullName?.Length}: {sample}");
            Assert.True(shortName is null || shortName.Trim().Length > 0, $"blank short: {sample}");
        }
    }

    [Theory]
    [InlineData(10, false, "abc", "abc")]
    [InlineData(3, false, "abcdef", "abc")]
    [InlineData(4, true, "abcdef", "abc…")]
    [InlineData(0, false, "abc", "")]
    [InlineData(-5, true, "abc", "")]
    [InlineData(1, true, "abc", "")]
    public void Fit_cuts_on_whole_text_elements_and_never_throws(int bound, bool ellipsis, string value, string expected)
    {
        Assert.Equal(expected, DriverStopText.Fit(value, bound, ellipsis));
    }

    [Fact]
    public void Fit_never_splits_a_grapheme()
    {
        var family = "👩‍👩‍👧‍👦";
        Assert.Equal(string.Empty, DriverStopText.Fit(family, family.Length - 1, ellipsis: false));
        Assert.Equal("a", DriverStopText.Fit("a" + family, family.Length, ellipsis: false));
        Assert.Equal("a…", DriverStopText.Fit("a" + family + "b", family.Length + 1, ellipsis: true));
    }

    [Fact]
    public void Format_characters_count_as_white_space()
    {
        Assert.Equal("Tres Ríos", DriverStopText.NormalizeLine("﻿ Tres​​Ríos ⁠"));
        Assert.Null(DriverStopText.NormalizeLine("﻿​"));
        Assert.Equal("Calle 1\nInterior B", DriverStopText.NormalizeText("﻿ Calle 1\nInterior B ​"));
        Assert.Null(DriverStopText.NormalizeText(" \n﻿ "));
    }

    [Fact]
    public void Zone_names_are_normalized_and_bounded_like_the_driver_app_expects()
    {
        Assert.Equal("Tres Ríos", DriverStopZoneName.Normalize(" Tres﻿ Ríos "));
        Assert.Null(DriverStopZoneName.Normalize("​"));
        var longName = DriverStopZoneName.Normalize(new string('z', 900));
        Assert.Equal(DriverStopZoneName.MaximumLength, longName!.Length);
        Assert.EndsWith("…", longName, StringComparison.Ordinal);
    }

    [Fact]
    public void The_address_and_its_references_are_normalized_and_bounded()
    {
        Assert.True(DriverStopAddressText.TryNormalize(
            " Calle Sintética 100, Tres Ríos ", " Portón azul\nfrente al parque ", out var address, out var references));
        Assert.Equal("Calle Sintética 100, Tres Ríos", address);
        Assert.Equal("Portón azul\nfrente al parque", references);

        Assert.True(DriverStopAddressText.TryNormalize("Calle Sintética 200", "﻿  ", out address, out references));
        Assert.Equal("Calle Sintética 200", address);
        Assert.Null(references);

        Assert.True(DriverStopAddressText.TryNormalize(
            new string('a', 1_500), new string('r', 900), out address, out references));
        Assert.Equal(DriverStopAddressText.MaximumAddressLength, address.Length);
        Assert.EndsWith("…", address, StringComparison.Ordinal);
        Assert.Equal(DriverStopAddressText.MaximumReferencesLength, references!.Length);
        Assert.EndsWith("…", references, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("﻿​")]
    public void A_blank_address_is_reported_as_unavailable(string? addressText)
    {
        Assert.False(DriverStopAddressText.TryNormalize(addressText, "Portón", out _, out _));
    }

    [Fact]
    public void Completed_stops_are_the_three_statuses_that_close_a_stop_in_Mazatlan_time()
    {
        Assert.Equal(["PICKED_UP", "DELIVERED", "RETURNED"], DriverStopsCompletedToday.CompletingStatuses);
        Assert.Equal("America/Mazatlan", DriverStopsCompletedToday.TimeZone);
    }

    [Fact]
    public void Results_never_print_the_stop_details()
    {
        var stop = new DriverStopResult(
            Guid.NewGuid(),
            3,
            "ORD_SYNTHETIC",
            "DELIVERY",
            "DELIVERING",
            "Resumen",
            true,
            "M. López",
            "Farmacia Las Quintas",
            "Tres Ríos",
            null,
            new DriverStopCodResult(125_000, false),
            new DriverStopNavigationResult(24.8, -107.39, "Calle Sintética 1", "Portón"));
        var text = stop.ToString() + stop.Navigation + new DriverStopListResult([stop], 2);
        foreach (var value in new[] { "M. López", "Farmacia", "Tres Ríos", "Calle", "Portón", "24.8", "125000" })
        {
            Assert.DoesNotContain(value, text, StringComparison.Ordinal);
        }
    }
}
