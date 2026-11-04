using TruckScale.Core.Data;
using TruckScale.Core.Ticket;

namespace TruckScale.Core.Tests;

public class TicketTests
{
    // Same format as the old program's .form files.
    private const string Sample = """
        <START>
        *HU*
        <FORM>
        <001><21><105><010><M><16><000><000><Nadpis listok><Mérlegjegy>
        <003><24><015><027><L><02><180><000><Ciara><>
        <034><22><050><030><L><10><000><000><Listok cislo><>
        <006><33><050><045><L><10><000><000><DatumD><>
        <012><36><150><050><L><10><000><000><CasD><>
        <018><30><050><065><L><10><000><000><SpzD><>
        <027><04><015><095><L><10><000><000><BruttoD><>
        <028><06><105><095><M><10><000><000><TaraD><>
        <029><05><195><095><R><10><000><000><NettoD><>
        <046><43><150><075><L><10><000><000><Strata><>
        <033><32><105><120><M><10><000><000><Poznamka><>
        <END>
        """;

    private static readonly Weighing W = new()
    {
        TicketNo = 417, Direction = Direction.In, Status = WeighingStatus.Done, Plate = "ABC123/XYZ789",
        FirstWeight = 26440, FirstAt = new DateTime(2026, 7, 1, 8, 5, 0),
        SecondWeight = 11300, SecondAt = new DateTime(2026, 7, 1, 8, 20, 0), ClosedAt = new DateTime(2026, 7, 1, 8, 20, 0),
        Gross = 26440, Tare = 11300, Net = 15140,
    };

    [Fact]
    public void Parses_fields()
    {
        var f = TicketForm.Parse(Sample);
        Assert.Equal(11, f.Count);
        Assert.Equal(new FormField(1, 21, 105, 10, TextAlign.Center, 16, 0, "Nadpis listok", "Mérlegjegy"), f[0]);
        Assert.Equal(180, f[1].Width);
        Assert.Equal(TextAlign.Right, f[8].Align);
    }

    [Fact]
    public void Fills_in_the_weighing()
    {
        var items = TicketForm.Layout(TicketForm.Parse(Sample), W, new Dictionary<string, string?> { ["company.note"] = "Falu 0123/4 Hrsz" });
        var texts = items.OfType<TicketText>().Select(t => t.Text.Replace(' ', ' ')).ToList();
        Assert.Contains("Mérlegjegy", texts);
        Assert.Contains("417", texts);
        Assert.Contains("2026.07.01.", texts);
        Assert.Contains("08:20", texts);
        Assert.Contains("ABC123/XYZ789", texts);
        Assert.Contains("26 440 kg", texts);
        Assert.Contains("11 300 kg", texts);
        Assert.Contains("15 140 kg", texts);
        Assert.Contains("Falu 0123/4 Hrsz", texts);
        Assert.DoesNotContain(texts, t => t.Length == 0);   // stored tare not used -> field omitted
        Assert.Single(items.OfType<TicketLine>());
    }

    [Fact]
    public void Default_form_is_valid() =>
        Assert.True(TicketForm.Parse(TicketForm.DefaultForm).Count > 40);
}
