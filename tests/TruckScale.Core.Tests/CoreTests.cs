using TruckScale.Core.Scale;
using TruckScale.Core.Text;

namespace TruckScale.Core.Tests;

public class ScaleReadingTests
{
    [Theory]
    [InlineData("ST,GL,       0kg", ScaleStatus.Stable, "GL", 0)]
    [InlineData("ST,GL,   23480kg", ScaleStatus.Stable, "GL", 23480)]
    [InlineData("US,GL,   23460kg", ScaleStatus.Unstable, "GL", 23460)]
    [InlineData("ST,NT,-     40kg", ScaleStatus.Stable, "NT", -40)]
    [InlineData("ST,GS,+  12000 kg", ScaleStatus.Stable, "GS", 12000)]
    public void Parses_valid_lines(string line, ScaleStatus status, string mode, int weight)
    {
        var r = ScaleReading.TryParse(line);
        Assert.NotNull(r);
        Assert.Equal(status, r!.Status);
        Assert.Equal(mode, r.Mode);
        Assert.Equal(weight, r.Weight);
        Assert.Equal("kg", r.Unit);
    }

    [Fact]
    public void Only_ST_is_stable()
    {
        Assert.True(ScaleReading.TryParse("ST,GL,     100kg")!.IsStable);
        Assert.False(ScaleReading.TryParse("US,GL,     100kg")!.IsStable);
    }

    [Fact]
    public void Overload_is_recognised() =>
        Assert.Equal(ScaleStatus.Overload, ScaleReading.TryParse("OL,GL,")!.Status);

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("ST,GL,  12a40kg")]
    [InlineData(null)]
    public void Rejects_invalid_lines(string? line) => Assert.Null(ScaleReading.TryParse(line));
}

public class NormalizeTests
{
    [Theory]
    [InlineData("DEF-456/UVW-694", "DEF456/UVW694")]
    [InlineData("DEF456-UVW694", "DEF456 UVW694")]
    [InlineData("ABC123/XYZO63", "ABC-123/XYZ-063")]
    [InlineData("mkh-197", "MKH197")]
    public void Plate_spellings_match(string a, string b) =>
        Assert.Equal(Normalize.PlateKey(a), Normalize.PlateKey(b));

    [Fact]
    public void Different_plates_do_not_match() =>
        Assert.NotEqual(Normalize.PlateKey("ABC123/UVW694"), Normalize.PlateKey("ABC213/UVW694"));

    [Theory]
    [InlineData("Gunics Zsolt", "GUNICS   ZSOLT")]
    [InlineData("Kéri Ödön", "keri odon")]
    [InlineData("Árpa                ", "árpa")]
    public void Names_match_ignoring_case_accents_and_spaces(string a, string b) =>
        Assert.Equal(Normalize.NameKey(a), Normalize.NameKey(b));

    [Fact]
    public void Clean_trims_and_collapses() =>
        Assert.Equal("PIB 808 YIR502", Normalize.Clean("  PIB 808  YIR502 "));
}
