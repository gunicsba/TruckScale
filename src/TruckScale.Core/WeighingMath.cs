using System.Globalization;
using TruckScale.Core.Localization;
namespace TruckScale.Core;

public readonly record struct WeightResult(int Gross, int Tare, int Net);

public static class WeighingMath
{
    /// <summary>
    /// Two weighings: the heavier is the gross, the lighter the tare. Normally Beszállítás weighs the loaded
    /// truck first and Kiszállítás the empty one first, but taking max/min keeps the ticket correct
    /// even if the wrong direction was chosen.
    /// </summary>
    public static WeightResult FromPair(int first, int second)
    {
        int gross = Math.Max(first, second), tare = Math.Min(first, second);
        return new WeightResult(gross, tare, gross - tare);
    }

    /// <summary>One weighing of the loaded truck with the vehicle's stored tare ("Használ").</summary>
    public static WeightResult FromStoredTare(int loaded, int storedTare)
    {
        if (storedTare <= 0) throw new ArgumentOutOfRangeException(nameof(storedTare), L.T("db.noStoredTare"));
        if (loaded <= storedTare) throw new InvalidOperationException(L.F("db.notAboveTare", L.Kg(loaded), L.Kg(storedTare)));
        return new WeightResult(loaded, storedTare, loaded - storedTare);
    }

    /// <summary>One weighing of the loaded truck with a tare typed in by hand (Kiszállítás, "Kézi tára").</summary>
    public static WeightResult FromManualTare(int loaded, int manualTare)
    {
        if (manualTare <= 0) throw new ArgumentOutOfRangeException(nameof(manualTare), L.T("db.noManualTare"));
        if (loaded <= manualTare) throw new InvalidOperationException(L.F("db.notAboveManualTare", L.Kg(loaded), L.Kg(manualTare)));
        return new WeightResult(loaded, manualTare, loaded - manualTare);
    }

    /// <summary>Reads a typed tare: whole kilograms, spaces and a trailing "kg" allowed ("12 300 kg"). Null if not a positive number.</summary>
    public static int? ParseTare(string? text)
    {
        var s = string.Concat((text ?? "").Where(c => !char.IsWhiteSpace(c)));
        if (s.EndsWith("kg", StringComparison.OrdinalIgnoreCase)) s = s[..^2];
        return int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : null;
    }
}
