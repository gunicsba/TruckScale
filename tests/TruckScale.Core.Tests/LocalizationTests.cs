using System.Text.RegularExpressions;
using TruckScale.Core.Data;
using TruckScale.Core.Localization;
using TruckScale.Core.Ticket;

namespace TruckScale.Core.Tests;

/// <summary>Changes the global language, so it must not run in parallel with other tests.</summary>
[CollectionDefinition(nameof(LanguageCollection), DisableParallelization = true)]
public sealed class LanguageCollection;

[Collection(nameof(LanguageCollection))]
public class LocalizationTests
{
    public static TheoryData<string> OtherLanguages()
    {
        var d = new TheoryData<string>();
        foreach (var l in L.Languages.Where(l => l.Code != "hu")) d.Add(l.Code);
        return d;
    }

    [Theory]
    [MemberData(nameof(OtherLanguages))]
    public void Every_language_has_every_key(string code)
    {
        var texts = L.Languages.Single(l => l.Code == code).Texts;
        Assert.Empty(Strings.Hu.Keys.Except(texts.Keys));   // missing translations
        Assert.Empty(texts.Keys.Except(Strings.Hu.Keys));   // unknown (misspelled) keys
    }

    [Theory]
    [MemberData(nameof(OtherLanguages))]
    public void Placeholders_match(string code)
    {
        var texts = L.Languages.Single(l => l.Code == code).Texts;
        static string Args(string s) => string.Join(",", Regex.Matches(s, @"\{(\d+)").Select(m => m.Groups[1].Value).Distinct().Order());
        foreach (var (key, hu) in Strings.Hu)
            Assert.True(Args(hu) == Args(texts[key]), $"{code}: {key}");
    }

    [Fact]
    public void English_ticket_and_numbers()
    {
        try
        {
            L.Set("en");
            var w = new Weighing
            {
                TicketNo = 7, Direction = Direction.Out, Status = WeighingStatus.Done,
                FirstAt = new DateTime(2026, 7, 1, 8, 0, 0), ClosedAt = new DateTime(2026, 7, 1, 8, 20, 0),
                Gross = 26440, Tare = 11300, Net = 15140,
            };
            var texts = TicketForm.Layout(TicketForm.Parse(TicketForm.DefaultForm), w, new Dictionary<string, string?>())
                .OfType<TicketText>().Select(t => t.Text).ToList();
            Assert.Contains("Weighing ticket", texts);
            Assert.Contains("Outbound", texts);
            Assert.Contains("01/07/2026", texts);
            Assert.Contains("15,140 kg", texts);
            Assert.Equal("Cancelled", (w with { Status = WeighingStatus.Void }).StatusText);
        }
        finally
        {
            L.Set("hu");
        }
    }

    [Fact]
    public void Unknown_language_falls_back_to_Hungarian()
    {
        try
        {
            L.Set("xx");
            Assert.Equal("hu", L.Current.Code);
            Assert.Equal("Beszállítás", L.T("dir.in"));
            Assert.Equal("no.such.key", L.T("no.such.key"));
        }
        finally
        {
            L.Set("hu");
        }
    }
}
