using TruckScale.Core.Data;

namespace TruckScale.Core;

/// <summary>Filter of the Áttekintés (summary). Dates are whole days, both inclusive.</summary>
public sealed record SummaryFilter(DateTime From, DateTime To, Direction? Direction = null, long? PartnerId = null, long? MaterialId = null);

public sealed record SummaryGroup(string Material, Direction Direction, int Count, long Gross, long Tare, long Net)
{
    public string DirectionText => Direction == Direction.In ? "Beszállítás" : "Kiszállítás";
}

public sealed record SummaryTotal(int Count, long Gross, long Tare, long Net);

public static class Summary
{
    /// <summary>Closed (not cancelled) weighings matching the filter, oldest first.</summary>
    public static List<Weighing> Select(IEnumerable<Weighing> all, SummaryFilter f)
    {
        var from = f.From.Date;
        var to = f.To.Date.AddDays(1);
        return all.Where(w => w.Status == WeighingStatus.Done && w.ClosedAt >= from && w.ClosedAt < to
                              && (f.Direction is null || w.Direction == f.Direction)
                              && (f.PartnerId is null || w.PartnerId == f.PartnerId)
                              && (f.MaterialId is null || w.MaterialId == f.MaterialId))
            .OrderBy(w => w.ClosedAt).ThenBy(w => w.TicketNo)
            .ToList();
    }

    /// <summary>
    /// Totals per direction and material name (as written on the tickets). Names differing only in
    /// case/accents are one group, shown with their most frequent spelling.
    /// </summary>
    public static List<SummaryGroup> ByMaterial(IEnumerable<Weighing> rows) =>
        rows.GroupBy(w => (w.Direction, Key: Text.Normalize.NameKey(w.MaterialName)))
            .Select(g => new SummaryGroup(
                g.Key.Key.Length == 0 ? "(nincs megadva)" : g.GroupBy(w => Text.Normalize.Clean(w.MaterialName)).OrderByDescending(x => x.Count()).First().Key,
                g.Key.Direction, g.Count(),
                g.Sum(w => (long)(w.Gross ?? 0)), g.Sum(w => (long)(w.Tare ?? 0)), g.Sum(w => (long)(w.Net ?? 0))))
            .OrderBy(g => g.Direction).ThenBy(g => g.Material, StringComparer.Create(System.Globalization.CultureInfo.GetCultureInfo("hu-HU"), true))
            .ToList();

    public static SummaryTotal Total(IEnumerable<Weighing> rows)
    {
        var list = rows as IReadOnlyCollection<Weighing> ?? rows.ToList();
        return new SummaryTotal(list.Count, list.Sum(w => (long)(w.Gross ?? 0)), list.Sum(w => (long)(w.Tare ?? 0)), list.Sum(w => (long)(w.Net ?? 0)));
    }
}
