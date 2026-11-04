using TruckScale.Core.Data;

namespace TruckScale.Core.Tests;

public class FilterTests
{
    private static readonly Weighing W = new()
    {
        TicketNo = 417, ClosedAt = new DateTime(2023, 7, 14, 9, 5, 0), Direction = Direction.In, Status = WeighingStatus.Done,
        Plate = "NLB-381/YKK-063", PartnerName = "Kiss Ádám", DriverName = "Gunics Zsolt", MaterialName = "Búza", MaterialNote = "euró",
        Marking = "Tábla 3",
    };

    [Theory]
    [InlineData("417", null, null, null, null, null, true)]
    [InlineData("41", null, null, null, null, null, true)]
    [InlineData("418", null, null, null, null, null, false)]
    [InlineData(null, "2023.07", null, null, null, null, true)]
    [InlineData(null, "2023.08", null, null, null, null, false)]
    [InlineData(null, null, "nlb381", null, null, null, true)]
    [InlineData(null, null, "ykk 063", null, null, null, true)]
    [InlineData(null, null, null, "kiss adam", null, null, true)]
    [InlineData(null, null, null, "kiss", "buza", null, true)]
    [InlineData(null, null, null, "kiss", "árpa", null, false)]
    [InlineData(null, null, null, null, "euro", null, true)]
    [InlineData(null, null, null, null, null, "tabla", true)]
    public void Column_filters(string? ticket, string? date, string? plate, string? partner, string? material, string? marking, bool expected) =>
        Assert.Equal(expected, new WeighingFilter(ticket, date, null, plate, partner, null, material, marking).Matches(W));

    [Fact]
    public void Direction_and_empty()
    {
        Assert.True(new WeighingFilter().IsEmpty);
        Assert.True(new WeighingFilter().Matches(W));
        Assert.False(new WeighingFilter(Direction: Direction.Out).Matches(W));
        Assert.True(new WeighingFilter(Driver: "gunics").Matches(W));
    }
}
