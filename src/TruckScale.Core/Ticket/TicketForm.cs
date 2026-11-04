using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using TruckScale.Core.Data;

namespace TruckScale.Core.Ticket;

public enum TextAlign { Left, Center, Right }

/// <summary>Something to draw on the page; coordinates in millimetres from the top-left corner.</summary>
public abstract record TicketItem;
public sealed record TicketText(double X, double Y, TextAlign Align, double FontSize, string Text) : TicketItem;
public sealed record TicketLine(double X, double Y, double Length, double Thickness) : TicketItem;

/// <summary>
/// One line of the old TS.exe ticket form (*.form, Windows-1250):
/// <c>&lt;no&gt;&lt;type&gt;&lt;x mm&gt;&lt;y mm&gt;&lt;L|M|R&gt;&lt;font size&gt;&lt;width&gt;&lt;?&gt;&lt;description&gt;&lt;text&gt;</c>
/// </summary>
public sealed record FormField(int No, int Type, int X, int Y, TextAlign Align, int Size, int Width, string Description, string Text);

public static partial class TicketForm
{
    // Field types of the old program (worked out from the form and the printed tickets).
    public const int StaticText = 21, Line = 24, TicketNo = 22,
        DateIn = 33, DateOut = 34, TimeIn = 35, TimeOut = 36,
        PartnerName = 15, PartnerAddress = 14, Plate = 30, Driver = 31,
        Material = 26, MaterialNote = 27, Marking = 28,
        Gross = 4, Net = 5, Tare = 6, Operator = 25,
        CompanyNote = 32,    // printed between Átadó and Átvevő, e.g. "… 0130/3 Hrsz"
        StoredTare = 43, MeasuredWeight = 44,
        CompanyHeader = 90,  // new: company name and address from the settings
        DirectionText = 91;  // new: Beszállítás / Kiszállítás

    private static readonly CultureInfo Hu = CultureInfo.GetCultureInfo("hu-HU");

    [GeneratedRegex(@"^<(\d+)><(\d+)><(\d+)><(\d+)><([LMR])><(\d+)><(\d+)><(\d+)><([^>]*)><(.*)>\s*$")]
    private static partial Regex FieldRegex();

    public static List<FormField> Parse(string content)
    {
        var list = new List<FormField>();
        foreach (var raw in content.Split('\n'))
        {
            var m = FieldRegex().Match(raw.TrimEnd('\r'));
            if (!m.Success) continue;
            int I(int g) => int.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture);
            var align = m.Groups[5].Value switch { "M" => TextAlign.Center, "R" => TextAlign.Right, _ => TextAlign.Left };
            list.Add(new FormField(I(1), I(2), I(3), I(4), align, I(6), I(7), m.Groups[9].Value, m.Groups[10].Value));
        }
        return list;
    }

    /// <summary>Reads a .form file (the old program saved them as Windows-1250).</summary>
    public static List<FormField> Load(string path)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var bytes = File.ReadAllBytes(path);
        bool utf8 = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        return Parse((utf8 ? Encoding.UTF8 : Encoding.GetEncoding(1250)).GetString(bytes));
    }

    /// <summary>Fills the form with the data of a closed weighing.</summary>
    public static List<TicketItem> Layout(IEnumerable<FormField> form, Weighing w, IReadOnlyDictionary<string, string?> settings)
    {
        static string Kg(int? v) => v is int x ? x.ToString("N0", Hu) + " kg" : "";
        static string D(DateTime? t) => t?.ToString("yyyy.MM.dd.", Hu) ?? "";
        static string T(DateTime? t) => t?.ToString("HH:mm", Hu) ?? "";
        var company = string.Join(", ", new[]
        {
            settings.GetValueOrDefault("company.name"),
            string.Join(' ', new[] { settings.GetValueOrDefault("company.zip"), settings.GetValueOrDefault("company.town") }.Where(s => !string.IsNullOrWhiteSpace(s))),
            settings.GetValueOrDefault("company.street"),
        }.Where(s => !string.IsNullOrWhiteSpace(s)));

        var items = new List<TicketItem>();
        foreach (var f in form)
        {
            if (f.Type == Line)
            {
                items.Add(new TicketLine(f.X, f.Y, f.Width, Math.Max(0.2, f.Size / 10.0)));
                continue;
            }
            string text = f.Type switch
            {
                StaticText => f.Text,
                TicketNo => w.TicketNo?.ToString(Hu) ?? "",
                DateIn => D(w.FirstAt), DateOut => D(w.ClosedAt),
                TimeIn => T(w.FirstAt), TimeOut => T(w.ClosedAt),
                PartnerName => w.PartnerName ?? "",
                PartnerAddress => w.PartnerAddress ?? "",
                Plate => w.Plate ?? "",
                Driver => w.DriverName ?? "",
                Material => w.MaterialName ?? "",
                MaterialNote => w.MaterialNote ?? "",
                Marking => w.Marking ?? "",
                Gross => Kg(w.Gross), Net => Kg(w.Net), Tare => Kg(w.Tare),
                Operator => settings.GetValueOrDefault("operator.name") ?? "",
                CompanyNote => settings.GetValueOrDefault("company.note") ?? "",
                StoredTare => w.StoredTare ? Kg(w.Tare) : "",
                MeasuredWeight => Kg(w.SecondWeight ?? w.FirstWeight),
                CompanyHeader => company,
                DirectionText => w.DirectionText,
                _ => "",
            };
            if (text.Length > 0) items.Add(new TicketText(f.X, f.Y, f.Align, f.Size, text));
        }
        return items;
    }

    /// <summary>Built-in layout used when no .form file is configured: two copies on one A4 page.</summary>
    public static readonly string DefaultForm = BuildDefault();

    private static string BuildDefault()
    {
        var sb = new StringBuilder();
        int n = 0;
        void F(int type, int x, int y, char a, int size, string text = "", int width = 0) =>
            sb.Append($"<{++n:000}><{type:00}><{x:000}><{y:000}><{a}><{size:00}><{width:000}><000><><{text}>\r\n");
        foreach (int dy in new[] { 0, 150 })
        {
            F(StaticText, 105, 10 + dy, 'M', 16, "Mérlegjegy");
            F(CompanyHeader, 105, 19 + dy, 'M', 11);
            F(Line, 15, 27 + dy, 'L', 2, width: 180);
            F(StaticText, 15, 31 + dy, 'L', 10, "Sorszám:"); F(TicketNo, 50, 31 + dy, 'L', 10);
            F(StaticText, 115, 31 + dy, 'L', 10, "Irány:"); F(DirectionText, 150, 31 + dy, 'L', 10);
            F(StaticText, 15, 40 + dy, 'L', 10, "Kezdés:"); F(DateIn, 50, 40 + dy, 'L', 10); F(TimeIn, 75, 40 + dy, 'L', 10);
            F(StaticText, 115, 40 + dy, 'L', 10, "Zárás:"); F(DateOut, 150, 40 + dy, 'L', 10); F(TimeOut, 175, 40 + dy, 'L', 10);
            F(StaticText, 15, 47 + dy, 'L', 10, "Cég:"); F(PartnerName, 50, 47 + dy, 'L', 10);
            F(StaticText, 15, 53 + dy, 'L', 10, "Cím:"); F(PartnerAddress, 50, 53 + dy, 'L', 10);
            F(StaticText, 15, 59 + dy, 'L', 10, "Rendszám:"); F(Plate, 50, 59 + dy, 'L', 10);
            F(StaticText, 15, 65 + dy, 'L', 10, "Sofőr:"); F(Driver, 50, 65 + dy, 'L', 10);
            F(StaticText, 115, 59 + dy, 'L', 10, "Anyag:"); F(Material, 150, 59 + dy, 'L', 10);
            F(StaticText, 115, 65 + dy, 'L', 10, "Megjegyzés:"); F(MaterialNote, 150, 65 + dy, 'L', 10);
            F(StaticText, 115, 71 + dy, 'L', 10, "Megjelölés:"); F(Marking, 150, 71 + dy, 'L', 10);
            F(Line, 15, 79 + dy, 'L', 2, width: 180);
            F(StaticText, 15, 83 + dy, 'L', 10, "Bruttó"); F(StaticText, 105, 83 + dy, 'M', 10, "Tára"); F(StaticText, 195, 83 + dy, 'R', 10, "Nettó");
            F(Gross, 15, 89 + dy, 'L', 12); F(Tare, 105, 89 + dy, 'M', 12); F(Net, 195, 89 + dy, 'R', 12);
            F(Line, 15, 97 + dy, 'L', 2, width: 180);
            F(StaticText, 50, 116 + dy, 'M', 10, "........................................"); F(StaticText, 160, 116 + dy, 'M', 10, "........................................");
            F(StaticText, 50, 121 + dy, 'M', 10, "Átadó"); F(CompanyNote, 105, 121 + dy, 'M', 10); F(StaticText, 160, 121 + dy, 'M', 10, "Átvevő");
        }
        return sb.ToString();
    }
}
