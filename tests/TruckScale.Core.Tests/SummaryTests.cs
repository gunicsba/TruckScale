using TruckScale.Core.Data;

namespace TruckScale.Core.Tests;

public class SummaryTests
{
    private static Weighing W(int ticket, DateTime at, Direction dir, string material, int gross, int tare, long partner = 1,
        WeighingStatus status = WeighingStatus.Done) => new()
    {
        TicketNo = ticket, ClosedAt = at, Direction = dir, MaterialName = material, MaterialId = material == "Árpa" ? 2 : 1,
        PartnerId = partner, Status = status, Gross = gross, Tare = tare, Net = gross - tare,
    };

    private static readonly List<Weighing> Data =
    [
        W(1, new(2026, 7, 1, 8, 0, 0), Direction.In, "Búza", 26000, 11000),
        W(2, new(2026, 7, 1, 23, 59, 0), Direction.In, "Búza", 25000, 11000, partner: 2),
        W(3, new(2026, 7, 2, 0, 1, 0), Direction.In, "Árpa", 20000, 10000),
        W(4, new(2026, 7, 1, 9, 0, 0), Direction.Out, "búza", 30000, 12000),
        W(5, new(2026, 7, 1, 10, 0, 0), Direction.In, "Búza", 99999, 1, status: WeighingStatus.Void),
    ];

    [Fact]
    public void Day_range_is_inclusive_and_void_is_excluded()
    {
        var rows = Summary.Select(Data, new SummaryFilter(new(2026, 7, 1), new(2026, 7, 1)));
        Assert.Equal([1, 4, 2], rows.Select(r => r.TicketNo!.Value));
    }

    [Fact]
    public void Filters_by_direction_partner_material()
    {
        var all = new SummaryFilter(new(2026, 1, 1), new(2026, 12, 31));
        Assert.Equal(3, Summary.Select(Data, all with { Direction = Direction.In }).Count);
        Assert.Single(Summary.Select(Data, all with { PartnerId = 2 }));
        Assert.Single(Summary.Select(Data, all with { MaterialId = 2 }));
    }

    [Fact]
    public void Groups_and_totals()
    {
        var rows = Summary.Select(Data, new SummaryFilter(new(2026, 1, 1), new(2026, 12, 31)));
        var g = Summary.ByMaterial(rows);
        Assert.Equal(3, g.Count);
        Assert.Equal(new SummaryGroup("Árpa", Direction.In, 1, 20000, 10000, 10000), g[0]);
        Assert.Equal(new SummaryGroup("Búza", Direction.In, 2, 51000, 22000, 29000), g[1]);
        Assert.Equal(new SummaryGroup("búza", Direction.Out, 1, 30000, 12000, 18000), g[2]);
        Assert.Equal(new SummaryTotal(4, 101000, 44000, 57000), Summary.Total(rows));
    }
}
