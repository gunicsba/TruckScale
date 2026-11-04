using TruckScale.Core.Data;
using TruckScale.Core.Ticket;

namespace TruckScale.Core.Tests;

/// <summary>The sample layouts in forms/ must parse and only use known field types.</summary>
public class FormFileTests
{
    private static readonly int[] KnownTypes = [4, 5, 6, 14, 15, 21, 22, 24, 25, 26, 27, 28, 30, 31, 32, 33, 34, 35, 36, 43, 44, 90, 91, 92];

    private static string FormsDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TruckScale.slnx"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "forms");
    }

    public static TheoryData<string> Files()
    {
        var d = new TheoryData<string>();
        foreach (var f in Directory.GetFiles(FormsDir(), "*.form")) d.Add(Path.GetFileName(f));
        return d;
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void Sample_form_is_valid(string file)
    {
        var form = TicketForm.Load(Path.Combine(FormsDir(), file));
        Assert.True(form.Count >= 40, $"{file}: {form.Count} fields");
        Assert.All(form, f => Assert.Contains(f.Type, KnownTypes));
        Assert.All(form, f => Assert.InRange(f.X, 0, 210));
        Assert.All(form, f => Assert.InRange(f.Y, 0, 297));

        var w = new Weighing
        {
            TicketNo = 12, Direction = Direction.In, Status = WeighingStatus.Done, PartnerName = "Teszt Ügyfél", Plate = "ABC-123",
            MaterialName = "Búza", MaterialNote = "vetőmag", Note = "Szárító", Gross = 30000, Tare = 12000, Net = 18000,
            FirstAt = new DateTime(2026, 7, 1, 8, 0, 0), ClosedAt = new DateTime(2026, 7, 1, 8, 20, 0),
        };
        var texts = TicketForm.Layout(form, w, new Dictionary<string, string?> { ["company.name"] = "Minta Kft.", ["company.note"] = "0123/4 Hrsz" })
            .OfType<TicketText>().Select(t => t.Text).ToList();
        Assert.Contains("Minta Kft.", texts);
        Assert.Contains("0123/4 Hrsz", texts);
        Assert.Contains("vetőmag – Szárító", texts);   // material note + weighing note
        Assert.Contains("ABC-123", texts);
    }
}
