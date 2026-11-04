using TruckScale.Core.Scale;

namespace TruckScale.Core.Tests;

public class ScaleProtocolTests
{
    private static readonly ScaleProtocol Default = new(); // the old program's settings: ST at 0/2, weight at 6/8, 0 decimals

    [Theory]
    [InlineData("ST,GL,       0kg", true, 0)]
    [InlineData("ST,GL,   23480kg", true, 23480)]
    [InlineData("US,GL,   23460kg", false, 23460)]
    [InlineData("ST,NT,-     40kg", true, -40)]
    [InlineData("st,GL,     120kg", true, 120)]
    public void Fixed_positions_like_the_old_program(string line, bool stable, int weight)
    {
        var r = Default.Parse(line)!;
        Assert.Equal(stable, r.IsStable);
        Assert.Equal(weight, r.Weight);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ST,GL")]
    [InlineData("ST,GL,  abcdef kg")]
    [InlineData(null)]
    public void Fixed_rejects_lines_without_a_weight(string? line) => Assert.Null(Default.Parse(line));

    [Fact]
    public void Decimals_and_other_positions()
    {
        var p = new ScaleProtocol(StableStart: 10, StableLength: 1, StableText: "S", WeightStart: 0, WeightLength: 7, Decimals: 1);
        var r = p.Parse(" 123456   S")!;   // 12345.6 kg
        Assert.True(r.IsStable);
        Assert.Equal(12346, r.Weight);
        Assert.False(p.Parse(" 123456   M")!.IsStable);
    }

    [Fact]
    public void Auto_mode_uses_the_built_in_recognition()
    {
        var p = new ScaleProtocol(Mode: ParseMode.Auto);
        Assert.Equal(23480, p.Parse("ST,GL,   23480kg")!.Weight);
        Assert.Equal(12000, p.Parse("ST,GS,+  12000 kg")!.Weight);
    }

    [Fact]
    public void Settings_round_trip_and_defaults()
    {
        var p = new ScaleProtocol(ParseMode.Auto, 1, 3, "STB", 7, 9, 2, "CR");
        var back = ScaleProtocol.From(p.ToSettings().ToDictionary(x => x.Key, x => (string?)x.Value));
        Assert.Equal(p, back);
        Assert.Equal("\r", back.Terminator);
        Assert.Equal(new ScaleProtocol(), ScaleProtocol.From(new Dictionary<string, string?>()));
    }

    [Fact]
    public void Display_helpers()
    {
        Assert.Equal("ST,GL<CR><LF>", ScaleProtocol.Visible("ST,GL\r\n"));
        Assert.Equal("<02>12", ScaleProtocol.Visible("\u000212"));
        Assert.Equal("0123456789012", ScaleProtocol.Ruler(13));
        Assert.Equal("SS    WWWWWWWW", Default.Marks(16));
    }
}

public class BaudDetectorTests
{
    [Fact]
    public void Good_lines_score_highest()
    {
        var p = new ScaleProtocol();
        Assert.Equal(30, BaudDetector.Score("ST,GL,     100kg\r\nST,GL,     100kg\r\nUS,GL,     120kg\r\nST,G", p));
        Assert.Equal(1, BaudDetector.Score("hello world, readable text\r\n", p));
        Assert.Equal(0, BaudDetector.Score("ÿþ\u0001\u0002\u0080\u0081ÿþ\u0001\u0002\u0080\u0081", p));
        Assert.Equal(0, BaudDetector.Score("", p));
    }
}
