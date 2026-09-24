using System.Reflection;
using Finance.Domain;

namespace Paqueteria.UnitTests.Finance;

/// <summary>FIN-001 money precision fixtures: integer cents only, and overflow is never silent.</summary>
public sealed class MoneyCentsTests
{
    [Fact]
    public void Fin001_money_is_integer_cents_in_mxn()
    {
        Assert.Equal("MXN", MoneyCents.Currency);
        Assert.Equal(typeof(long), typeof(MoneyCents).GetProperty(nameof(MoneyCents.AmountCents))!.PropertyType);
        Assert.Equal(0, MoneyCents.Zero.AmountCents);
    }

    [Fact]
    public void Fin001_no_finance_type_exposes_a_binary_floating_point_amount()
    {
        var offenders = typeof(MoneyCents).Assembly.GetTypes()
            .Where(type => type.IsPublic)
            .SelectMany(type => type
                .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Select(property => (Type: type, Member: property.Name, property.PropertyType))
                .Concat(type
                    .GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                    .Select(field => (Type: type, Member: field.Name, PropertyType: field.FieldType))))
            .Where(member =>
                Nullable.GetUnderlyingType(member.PropertyType) is { } underlying
                    ? underlying == typeof(float) || underlying == typeof(double) || underlying == typeof(decimal)
                    : member.PropertyType == typeof(float) ||
                        member.PropertyType == typeof(double) ||
                        member.PropertyType == typeof(decimal))
            .Select(member => $"{member.Type.Name}.{member.Member}")
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Fin001_addition_and_subtraction_are_checked()
    {
        Assert.Equal(1_500, (new MoneyCents(1_000) + new MoneyCents(500)).AmountCents);
        Assert.Equal(-500, (new MoneyCents(1_000) - new MoneyCents(1_500)).AmountCents);
        Assert.Throws<OverflowException>(() => new MoneyCents(long.MaxValue) + new MoneyCents(1));
        Assert.Throws<OverflowException>(() => new MoneyCents(long.MinValue) - new MoneyCents(1));
    }

    [Fact]
    public void Fin001_non_negative_construction_rejects_negative_amounts()
    {
        Assert.Equal(7, MoneyCents.FromNonNegative(7).AmountCents);
        Assert.Throws<ArgumentOutOfRangeException>(() => MoneyCents.FromNonNegative(-1));
    }

    [Fact]
    public void Fin001_basis_points_never_overflow_and_truncate_toward_zero()
    {
        Assert.Equal(5_000, new MoneyCents(50).BasisPointsOf(new(100)));
        Assert.Equal(3_333, new MoneyCents(1).BasisPointsOf(new(3)));
        Assert.Equal(-3_333, new MoneyCents(-1).BasisPointsOf(new(3)));
        Assert.Null(new MoneyCents(10).BasisPointsOf(MoneyCents.Zero));
        Assert.Equal(10_000, new MoneyCents(long.MaxValue).BasisPointsOf(new(long.MaxValue)));
    }

    [Fact]
    public void Fin001_large_synthetic_amounts_stay_exact()
    {
        var revenue = new MoneyCents(3_000_000_000L);
        var cost = new MoneyCents(2_999_999_999L);

        Assert.Equal(1, (revenue - cost).AmountCents);
        Assert.Equal(0, (revenue - cost).BasisPointsOf(revenue));
    }
}
