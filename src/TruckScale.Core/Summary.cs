using TruckScale.Core.Data;

namespace TruckScale.Core;

/// <summary>Filter of the Áttekintés (summary). Dates are whole days, both inclusive.</summary>
public sealed record SummaryFilter(DateTime From, DateTime To, Direction? Direction = null, long? PartnerId = null, long? MaterialId = null,
    WeighingFilter? Text = null);

public sealed record SummaryGroup(string Material, Direction Direction, int Count, long Gross, long Tare, long Net)
{
    public string DirectionText => Direction == Direction.In ? "Beszállítás" : "Kiszállítás";
}

public sealed record SummaryTotal(int Count, long Gross, long Tare, long Net);

public enum SummaryGrouping { Material, MaterialPartner, PartnerMaterial }

/// <summary>One line of the summary table; subtotal lines close a group in the two-level groupings.</summary>
public sealed record SummaryRow(Direction Direction, string Level1, string? Level2, bool IsSubtotal, int Count, long Gross, long Tare, long Net)
{
    public string DirectionText => Direction == Direction.In ? "Beszállítás" : "Kiszállítás";
}

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
                              && (f.MaterialId is null || w.MaterialId == f.MaterialId)
                              && (f.Text is null || f.Text.Matches(w)))
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

    private static readonly StringComparer Hu = StringComparer.Create(System.Globalization.CultureInfo.GetCultureInfo("hu-HU"), true);

    /// <summary>Most frequent spelling of a name within a group ("" -> "(nincs megadva)").</summary>
    private static string Spelling(IEnumerable<Weighing> g, Func<Weighing, string?> name)
    {
        var best = g.GroupBy(w => Text.Normalize.Clean(name(w))).OrderByDescending(x => x.Count()).First().Key;
        return best.Length == 0 ? "(nincs megadva)" : best;
    }

    private static string MaterialKey(Weighing w) => Text.Normalize.NameKey(w.MaterialName);
    private static string PartnerKey(Weighing w) => w.PartnerId?.ToString() ?? Text.Normalize.NameKey(w.PartnerName);

    /// <summary>The summary table for the chosen grouping. Two-level groups are ordered by net weight (largest first).</summary>
    public static List<SummaryRow> Build(IEnumerable<Weighing> rows, SummaryGrouping grouping)
    {
        if (grouping == SummaryGrouping.Material)
            return ByMaterial(rows).Select(g => new SummaryRow(g.Direction, g.Material, null, false, g.Count, g.Gross, g.Tare, g.Net)).ToList();

        bool materialFirst = grouping == SummaryGrouping.MaterialPartner;
        var list = rows as IReadOnlyCollection<Weighing> ?? rows.ToList();
        // One spelling per material / customer for the whole period.
        var materialName = list.GroupBy(MaterialKey).ToDictionary(g => g.Key, g => Spelling(g, w => w.MaterialName));
        var partnerName = list.GroupBy(PartnerKey).ToDictionary(g => g.Key, g => Spelling(g, w => w.PartnerName));
        Func<Weighing, string> key1 = materialFirst ? MaterialKey : PartnerKey, key2 = materialFirst ? PartnerKey : MaterialKey;
        var name1 = materialFirst ? materialName : partnerName;
        var name2 = materialFirst ? partnerName : materialName;

        static SummaryRow Row(Direction d, string l1, string? l2, bool sub, IEnumerable<Weighing> g) =>
            new(d, l1, l2, sub, g.Count(), g.Sum(w => (long)(w.Gross ?? 0)), g.Sum(w => (long)(w.Tare ?? 0)), g.Sum(w => (long)(w.Net ?? 0)));

        var result = new List<SummaryRow>();
        foreach (var outer in list.GroupBy(w => (w.Direction, Key: key1(w)))
                     .Select(g => (g.Key.Direction, Name: name1[g.Key.Key], Items: g.ToList()))
                     .OrderBy(g => g.Direction).ThenBy(g => g.Name, Hu))
        {
            foreach (var inner in outer.Items.GroupBy(key2).OrderByDescending(g => g.Sum(w => (long)(w.Net ?? 0))))
                result.Add(Row(outer.Direction, outer.Name, name2[inner.Key], false, inner));
            result.Add(Row(outer.Direction, outer.Name, "Összesen", true, outer.Items));
        }
        return result;
    }

    public static SummaryTotal Total(IEnumerable<Weighing> rows)
    {
        var list = rows as IReadOnlyCollection<Weighing> ?? rows.ToList();
        return new SummaryTotal(list.Count, list.Sum(w => (long)(w.Gross ?? 0)), list.Sum(w => (long)(w.Tare ?? 0)), list.Sum(w => (long)(w.Net ?? 0)));
    }
}
