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
}
