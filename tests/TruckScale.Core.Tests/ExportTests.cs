using TruckScale.Core.Data;

namespace TruckScale.Core.Tests;

[Collection(nameof(LanguageCollection))]
public sealed class ExportTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ts-export-").FullName;

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void Csv_fields_are_quoted_when_needed()
    {
        Assert.Equal("abc", Csv.Field("abc"));
        Assert.Equal("\"a;b\"", Csv.Field("a;b"));
        Assert.Equal("\"say \"\"hi\"\"\"", Csv.Field("say \"hi\""));
        Assert.Equal("23480", Csv.Field(23480));                       // no thousands separator: summable
        Assert.Equal("2025-07-01 14:05", Csv.Field(new DateTime(2025, 7, 1, 14, 5, 0)));
        Assert.Equal("", Csv.Field(null));
        Assert.Equal("h1;h2\r\n1;\"x;y\"\r\n", Csv.Build(["h1", "h2"], [[1, "x;y"]]));
    }

    [Fact]
    public void Csv_summary_and_weighings()
    {
        var rows = new List<SummaryRow>
        {
            new(Direction.In, "Búza", "Minta Kft.", false, 2, 50000, 20000, 30000),
            new(Direction.In, "Búza", null, true, 2, 50000, 20000, 30000),
        };
        var lines = Csv.Summary(rows, "Anyag", "Ügyfél").Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.Equal(7, lines[0].Split(';').Length);
        Assert.EndsWith(";Búza;Minta Kft.;2;50000;20000;30000", lines[1]);
        Assert.Equal(6, Csv.Summary(rows, "Anyag", null).Split("\r\n")[0].Split(';').Length);

        var w = new Weighing { TicketNo = 7, Plate = "ABC-123", PartnerName = "Minta; Kft.", Gross = 30000, Tare = 10000, Net = 20000, ClosedAt = new DateTime(2025, 7, 1) };
        var csv = Csv.Weighings([w]).Split("\r\n");
        Assert.Equal(15, csv[0].Split(';').Length);
        Assert.StartsWith("7;", csv[1]);
        Assert.Contains("\"Minta; Kft.\"", csv[1]);
        Assert.Contains(";30000;10000;20000;", csv[1]);
    }

    [Fact]
    public void Settings_file_round_trip_with_the_ticket_form()
    {
        var aDir = Path.Combine(_dir, "a");
        Directory.CreateDirectory(aDir);
        File.WriteAllText(Path.Combine(aDir, "minta.form"), "1;21;10;10;L;10;0;0;x;Jegy\n");
        string json;
        using (var a = TruckScaleDb.OpenOrCreate(Path.Combine(aDir, "truckscale.db")))
        {
            a.SetSetting("company.name", "Minta Gazdaság Kft.");
            a.SetSetting("serial.port", "COM3");
            a.SetSetting("ticket.form", "minta.form");
            a.SetSetting("ticket.next_no", "500");        // state of the data: not saved
            json = SettingsFile.Save(a);
        }
        Assert.Contains("Minta Gazdaság Kft.", json);       // readable accents
        Assert.DoesNotContain("next_no", json);

        using var b = TruckScaleDb.OpenOrCreate(Path.Combine(_dir, "b", "truckscale.db"));
        b.SetSetting("ticket.next_no", "12");
        b.SetSetting("operator.name", "marad");
        int n = SettingsFile.Load(b, json);
        Assert.Equal(3, n);
        Assert.Equal("Minta Gazdaság Kft.", b.GetSetting("company.name"));
        Assert.Equal("COM3", b.GetSetting("serial.port"));
        Assert.Equal("12", b.GetSetting("ticket.next_no"));
        Assert.Equal("marad", b.GetSetting("operator.name"));
        Assert.Equal("minta.form", b.GetSetting("ticket.form"));
        Assert.Equal("1;21;10;10;L;10;0;0;x;Jegy\n", File.ReadAllText(Path.Combine(_dir, "b", "minta.form")));

        Assert.Throws<InvalidDataException>(() => SettingsFile.Load(b, "{\"foo\":1}"));
        Assert.Throws<InvalidDataException>(() => SettingsFile.Load(b, "not json"));
    }
}
