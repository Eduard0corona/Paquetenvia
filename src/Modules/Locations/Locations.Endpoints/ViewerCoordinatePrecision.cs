namespace Locations.Endpoints;

/// <summary>
/// D5-VIEWER-LOCATION-PRECISION-2026-09-27 (owner: "Coordenadas redondeadas ~1 km"): a VIEWER never receives
/// exact coordinates. The server rounds lat and lng to <see cref="DecimalPlaces"/> decimal places (about 1.1 km)
/// before serialization.
/// </summary>
/// <remarks>
/// The rule is deterministic and free of binary float drift: the double is converted to <see cref="decimal"/>
/// (which keeps the shortest decimal form of values such as 24.805), rounded half away from zero on that decimal
/// value (24.805 → 24.81, -106.095 → -106.1), and converted back. The result is the double nearest to a decimal
/// with at most two places, which System.Text.Json writes in its shortest round-trip form, so the serialized value
/// never carries more than two decimals.
/// </remarks>
public static class ViewerCoordinatePrecision
{
    public const int DecimalPlaces = 2;

    public static double Round(double coordinate)
    {
        if (double.IsNaN(coordinate) || double.IsInfinity(coordinate))
        {
            throw new ArgumentOutOfRangeException(nameof(coordinate), "A coordinate must be finite.");
        }

        var rounded = (double)Math.Round((decimal)coordinate, DecimalPlaces, MidpointRounding.AwayFromZero);

        // A value that rounds to zero is written as 0, never as -0.
        return rounded == 0d ? 0d : rounded;
    }
}
