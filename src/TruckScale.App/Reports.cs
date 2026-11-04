using System.Drawing.Printing;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using TruckScale.Core;
using TruckScale.Core.Data;
using TruckScale.Core.Text;
using TruckScale.Core.Ticket;

namespace TruckScale.App;

// ============================================================================ Napló

/// <summary>Napló: every weighing, with a search box and sortable columns; double-click = ticket (reprint).</summary>
public sealed class LogWindow : Window
{
    private readonly AppState _state;
    private readonly TextBox _search = new() { PlaceholderText = "Keresés (jegyszám, ügyfél, rendszám, sofőr, anyag, megjelölés)…", MinWidth = 420 };
    private readonly CheckBox _showVoid = new() { Content = "Stornók is", VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _count = new() { Classes = { "hint" }, VerticalAlignment = VerticalAlignment.Center };
    private readonly DataGrid _grid = Grids.Weighings(withStatus: true);
    private List<Weighing> _all = [];

    public LogWindow(AppState state)
    {
        _state = state;
        Title = "Napló – összes mérés";
        Width = 1250;
        Height = 760;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _all = state.Db.ClosedWeighings(new DateTime(1900, 1, 1), new DateTime(2100, 1, 1));
        _search.TextChanged += (_, _) => Filter();
        _showVoid.IsCheckedChanged += (_, _) => Filter();
        _grid.DoubleTapped += async (_, _) => await OpenTicket();
        _grid.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await OpenTicket(); } };

        var ticket = new Button { Content = "Mérlegjegy (előnézet / nyomtatás)…" };
        ticket.Click += async (_, _) => await OpenTicket();
        var close = new Button { Content = "Bezárás", IsCancel = true };
        close.Click += (_, _) => Close();

        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(0, 0, 0, 8), Children = { _search, _showVoid, _count } };
        var bottom = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(close, Dock.Right);
        bottom.Children.Add(close);
        bottom.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { ticket, new TextBlock { Text = "   Tipp: rendezés az oszlopfejlécre kattintva, dupla kattintás = mérlegjegy", Classes = { "hint" }, VerticalAlignment = VerticalAlignment.Center } } });
        var root = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(bottom);
        root.Children.Add(_grid);
        Content = root;

        Opened += (_, _) => _search.Focus();
        Filter();
    }

    private void Filter()
    {
        var key = Normalize.NameKey(_search.Text);
        var rows = _all.Where(w => (_showVoid.IsChecked == true || w.Status != WeighingStatus.Void) &&
                (key.Length == 0 || Normalize.NameKey($"{w.TicketNo} {w.PartnerName} {w.Plate} {Normalize.PlateKey(w.Plate)} {w.DriverName} {w.MaterialName} {w.MaterialNote} {w.Marking}").Contains(key)))
            .ToList();
        _grid.ItemsSource = rows;
        var t = Summary.Total(rows.Where(r => r.Status == WeighingStatus.Done));
        _count.Text = $"{rows.Count} tétel    nettó összesen: {AppState.Kg((int)Math.Min(int.MaxValue, t.Net))}";
    }

    private async Task OpenTicket()
    {
        if (_grid.SelectedItem is Weighing { Status: WeighingStatus.Done } w)
            await new TicketPreviewWindow(_state, w).ShowDialog(this);
    }
}

public static class Grids
{
    /// <summary>The weighing table used by Mai mérések and Napló (all columns sortable).</summary>
    public static DataGrid Weighings(bool withStatus)
    {
        var g = new DataGrid
        {
            IsReadOnly = true, SelectionMode = DataGridSelectionMode.Single, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            CanUserResizeColumns = true, CanUserSortColumns = true,
        };
        void Col(string header, string path, string? format = null, bool star = false) =>
            g.Columns.Add(new DataGridTextColumn
            {
                Header = header, Binding = new Binding(path) { StringFormat = format }, SortMemberPath = path,
                Width = star ? new DataGridLength(220) : DataGridLength.Auto,
            });
        Col("Jegy", "TicketNo");
        Col("Lezárva", "ClosedAt", "{0:yyyy.MM.dd. HH:mm}");
        if (withStatus) Col("Állapot", "StatusText");
        Col("Irány", "DirectionText");
        Col("Rendszám", "Plate");
        Col("Ügyfél", "PartnerName", star: true);
        Col("Sofőr", "DriverName");
        Col("Anyag", "MaterialName");
        Col("Megjelölés", "Marking");
        Col("Bruttó", "Gross", "{0:N0}");
        Col("Tára", "Tare", "{0:N0}");
        Col("Nettó", "Net", "{0:N0}");
        return g;
    }
}

// ============================================================================ Áttekintés

/// <summary>Áttekintés: totals for a period (optionally per direction / customer / material), printable.</summary>
public sealed class SummaryWindow : Window
{
    private readonly AppState _state;
    private readonly List<Weighing> _all;
    private readonly CalendarDatePicker _from = new() { SelectedDateFormat = CalendarDatePickerFormat.Custom, CustomDateFormatString = "yyyy.MM.dd." };
    private readonly CalendarDatePicker _to = new() { SelectedDateFormat = CalendarDatePickerFormat.Custom, CustomDateFormatString = "yyyy.MM.dd." };
    private readonly ComboBox _dir = new() { ItemsSource = new[] { "Mind", "Beszállítás", "Kiszállítás" }, SelectedIndex = 0, MinWidth = 140 };
    private readonly ComboBox _partner = new() { MinWidth = 260 };
    private readonly ComboBox _material = new() { MinWidth = 200 };
    private readonly ComboBox _grouping = new()
    {
        ItemsSource = new[] { "Anyagonként", "Anyagonként és ügyfelenként", "Ügyfelenként és anyagonként" }, SelectedIndex = 1, MinWidth = 240,
    };
    private SummaryGrouping Grouping => (SummaryGrouping)Math.Max(0, _grouping.SelectedIndex);
    private readonly CheckBox _itemized = new() { Content = "Tételes lista is (minden mérlegjegy)", IsChecked = true };
    private readonly DataGrid _grid = new() { IsReadOnly = true, CanUserSortColumns = true, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal };
    private readonly TextBlock _total = new() { FontWeight = FontWeight.Bold, FontSize = 15 };
    private List<Weighing> _rows = [];

    private sealed record Choice(long? Id, string Text) { public override string ToString() => Text; }

    public SummaryWindow(AppState state)
    {
        _state = state;
        Title = "Áttekintés – összesítő";
        Width = 1180;
        Height = 700;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _all = state.Db.ClosedWeighings(new DateTime(1900, 1, 1), new DateTime(2100, 1, 1));
        var hu = StringComparer.Create(AppState.Hu, true);
        _partner.ItemsSource = new[] { new Choice(null, "Minden ügyfél") }.Concat(state.Db.Partners().OrderBy(p => p.Name, hu).Select(p => new Choice(p.Id, p.Name))).ToList();
        _material.ItemsSource = new[] { new Choice(null, "Minden anyag") }.Concat(state.Db.Materials().OrderBy(m => m.Display, hu).Select(m => new Choice(m.Id, m.Display))).ToList();
        _partner.SelectedIndex = _material.SelectedIndex = 0;
        var today = DateTime.Today;
        _from.SelectedDate = new DateTime(today.Year, 1, 1);
        _to.SelectedDate = today;

        foreach (var (h, p, f) in new[] { ("Irány", "DirectionText", null), ("Anyag", "Level1", null), ("Ügyfél", "Level2", null), ("Mérések", "Count", null),
                     ("Bruttó (kg)", "Gross", "{0:N0}"), ("Tára (kg)", "Tare", "{0:N0}"), ("Nettó (kg)", "Net", "{0:N0}") })
            _grid.Columns.Add(new DataGridTextColumn { Header = h, Binding = new Binding(p) { StringFormat = f }, SortMemberPath = p,
                Width = p is "Level1" or "Level2" ? new DataGridLength(1, DataGridLengthUnitType.Star) : DataGridLength.Auto });
        // Subtotal lines in bold.
        _grid.LoadingRow += (_, e) => e.Row.FontWeight = e.Row.DataContext is SummaryRow { IsSubtotal: true } ? FontWeight.Bold : FontWeight.Normal;
        _grouping.SelectionChanged += (_, _) => Recalc();

        _from.SelectedDateChanged += (_, _) => Recalc();
        _to.SelectedDateChanged += (_, _) => Recalc();
        _dir.SelectionChanged += (_, _) => Recalc();
        _partner.SelectionChanged += (_, _) => Recalc();
        _material.SelectionChanged += (_, _) => Recalc();

        Button Quick(string text, Func<(DateTime, DateTime)> range)
        {
            var b = new Button { Content = text };
            b.Click += (_, _) => { var (f, t) = range(); _from.SelectedDate = f; _to.SelectedDate = t; };
            return b;
        }
        var quick = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 4,
            Children =
            {
                Quick("Ma", () => (today, today)),
                Quick("Ez a hónap", () => (new DateTime(today.Year, today.Month, 1), today)),
                Quick("Előző hónap", () => { var f = new DateTime(today.Year, today.Month, 1).AddMonths(-1); return (f, f.AddMonths(1).AddDays(-1)); }),
                Quick("Idén", () => (new DateTime(today.Year, 1, 1), today)),
                Quick("Tavaly", () => (new DateTime(today.Year - 1, 1, 1), new DateTime(today.Year - 1, 12, 31))),
                Quick("Összes", () => (_all.Count == 0 ? today : _all.Min(w => w.ClosedAt ?? today).Date, today)),
            },
        };

        Control Labeled(string label, Control c) => new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6,
            Children = { new TextBlock { Text = label, Classes = { "label" } }, c },
        };
        var filters = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new WrapPanel { Children = { Labeled("Időszak", _from), new TextBlock { Text = " – ", VerticalAlignment = VerticalAlignment.Center }, _to, new Border { Width = 12 }, quick } },
                new WrapPanel { Children = { Labeled("Irány", _dir), new Border { Width = 16 }, Labeled("Ügyfél", _partner), new Border { Width = 16 }, Labeled("Anyag", _material) } },
                new WrapPanel { Children = { Labeled("Csoportosítás", _grouping) } },
            },
        };

        var print = new Button { Content = "Nyomtatás", MinWidth = 120, IsDefault = true };
        var close = new Button { Content = "Bezárás", MinWidth = 100, IsCancel = true };
        print.Click += async (_, _) => await Print();
        var preview = new Button { Content = "Előnézet", MinWidth = 100 };
        preview.Click += async (_, _) => await ShowPreview();
        close.Click += (_, _) => Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right,
            Children = { new TextBlock { Text = "Nyomtató:", VerticalAlignment = VerticalAlignment.Center }, Printers.Chooser(state), _itemized, close, preview, print } };
        var bottom = new StackPanel { Spacing = 8, Margin = new Thickness(0, 8, 0, 0), Children = { _total, buttons } };

        var root = new DockPanel { Margin = new Thickness(12) };
        filters.Margin = new Thickness(0, 0, 0, 10);
        DockPanel.SetDock(filters, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(filters);
        root.Children.Add(bottom);
        root.Children.Add(_grid);
        Content = root;
        Recalc();
    }

    private SummaryFilter CurrentFilter() => new(
        _from.SelectedDate ?? DateTime.Today, _to.SelectedDate ?? DateTime.Today,
        _dir.SelectedIndex switch { 1 => Direction.In, 2 => Direction.Out, _ => null },
        (_partner.SelectedItem as Choice)?.Id, (_material.SelectedItem as Choice)?.Id);

    private void Recalc()
    {
        _rows = Summary.Select(_all, CurrentFilter());
        var (h1, h2) = Headers();
        _grid.Columns[1].Header = h1;
        _grid.Columns[2].Header = h2 ?? "";
        _grid.Columns[2].IsVisible = h2 is not null;
        _grid.CanUserSortColumns = Grouping == SummaryGrouping.Material; // keep subtotals under their group
        _grid.ItemsSource = Summary.Build(_rows, Grouping);
        var t = Summary.Total(_rows);
        _total.Text = $"Összesen: {t.Count} mérés    Bruttó {t.Gross:N0} kg    Tára {t.Tare:N0} kg    Nettó {t.Net:N0} kg";
    }

    private (string, string?) Headers() => Grouping switch
    {
        SummaryGrouping.MaterialPartner => ("Anyag", "Ügyfél"),
        SummaryGrouping.PartnerMaterial => ("Ügyfél", "Anyag"),
        _ => ("Anyag", null),
    };

    private async Task Print()
    {
        if (_rows.Count == 0) { await Dialog.Info(this, "Nyomtatás", "Nincs a feltételeknek megfelelő mérés."); return; }
        var err = ReportPrinter.Print(BuildDoc(), _state);
        if (err is not null) await Dialog.Info(this, "Nyomtatás", err);
    }

    private async Task ShowPreview()
    {
        if (_rows.Count == 0) { await Dialog.Info(this, "Előnézet", "Nincs a feltételeknek megfelelő mérés."); return; }
        var doc = BuildDoc();
        await new ReportPreviewWindow(_state, doc).ShowDialog(this);
    }

    public ReportDoc BuildDoc()
    {
        var f = CurrentFilter();
        var s = _state.Db.AllSettings();
        string N(long v) => v.ToString("N0", AppState.Hu);
        var header = new List<string>
        {
            string.Join(", ", new[] { s.GetValueOrDefault("company.name"), s.GetValueOrDefault("company.zip") + " " + s.GetValueOrDefault("company.town"), s.GetValueOrDefault("company.street") }
                .Where(x => !string.IsNullOrWhiteSpace(x))),
            $"Időszak: {f.From:yyyy.MM.dd.} – {f.To:yyyy.MM.dd.}",
            $"Irány: {_dir.SelectedItem}    Ügyfél: {_partner.SelectedItem}    Anyag: {_material.SelectedItem}",
        };
        var sections = new List<ReportSection>();
        var t = Summary.Total(_rows);
        var (h1, h2) = Headers();
        var summary = Summary.Build(_rows, Grouping);
        List<ReportColumn> cols = h2 is null
            ? [new("Irány", 26, TextAlign.Left), new(h1, 64, TextAlign.Left), new("Mérés", 15, TextAlign.Right),
               new("Bruttó (kg)", 25, TextAlign.Right), new("Tára (kg)", 25, TextAlign.Right), new("Nettó (kg)", 25, TextAlign.Right)]
            : [new("Irány", 22, TextAlign.Left), new(h1, 38, TextAlign.Left), new(h2, 45, TextAlign.Left), new("Mérés", 12, TextAlign.Right),
               new("Bruttó (kg)", 21, TextAlign.Right), new("Tára (kg)", 21, TextAlign.Right), new("Nettó (kg)", 21, TextAlign.Right)];
        string[] Cells(SummaryRow r) => h2 is null
            ? [r.DirectionText, r.Level1, r.Count.ToString(), N(r.Gross), N(r.Tare), N(r.Net)]
            : [r.IsSubtotal ? "" : r.DirectionText, r.IsSubtotal ? r.Level1 + " összesen" : r.Level1, r.IsSubtotal ? "" : r.Level2 ?? "",
               r.Count.ToString(), N(r.Gross), N(r.Tare), N(r.Net)];
        string[] total = h2 is null
            ? ["Összesen", "", t.Count.ToString(), N(t.Gross), N(t.Tare), N(t.Net)]
            : ["Összesen", "", "", t.Count.ToString(), N(t.Gross), N(t.Tare), N(t.Net)];
        sections.Add(new ReportSection($"Összesítés – {_grouping.SelectedItem}", cols, summary.Select(Cells).ToList(), total)
        {
            BoldRows = summary.Select((r, i) => (r, i)).Where(x => x.r.IsSubtotal).Select(x => x.i).ToHashSet(),
            SpaceAfterBold = true,
        });
        if (_itemized.IsChecked == true)
            sections.Add(new ReportSection("Tételes lista",
                [new("Jegy", 11, TextAlign.Left), new("Dátum", 25, TextAlign.Left), new("Irány", 8, TextAlign.Left), new("Rendszám", 26, TextAlign.Left),
                 new("Ügyfél", 40, TextAlign.Left), new("Anyag", 24, TextAlign.Left), new("Bruttó", 15, TextAlign.Right), new("Tára", 15, TextAlign.Right), new("Nettó", 16, TextAlign.Right)],
                _rows.Select(w => new[] { w.TicketNo?.ToString() ?? "", $"{w.ClosedAt:yyyy.MM.dd. HH:mm}", w.Direction == Direction.In ? "Be" : "Ki",
                    w.Plate ?? "", w.PartnerName ?? "", w.MaterialName ?? "", N(w.Gross ?? 0), N(w.Tare ?? 0), N(w.Net ?? 0) }).ToList(),
                ["", "", "", "", "Összesen", "", N(t.Gross), N(t.Tare), N(t.Net)]));

        return new ReportDoc("Összesítő – mérlegelések", header, sections);
    }
}

// ============================================================================ printing of lists

public sealed record ReportColumn(string Header, double Width, TextAlign Align);
public sealed record ReportSection(string Title, List<ReportColumn> Columns, List<string[]> Rows, string[]? TotalRow)
{
    /// <summary>Row indexes printed in bold (subtotals).</summary>
    public HashSet<int> BoldRows { get; init; } = [];
    /// <summary>Leave a small gap after each bold row (separates the groups).</summary>
    public bool SpaceAfterBold { get; init; }
}
public sealed record ReportDoc(string Title, List<string> HeaderLines, List<ReportSection> Sections);

/// <summary>Prints a multi-page table report on A4 portrait (Windows).</summary>
public static class ReportPrinter
{
    private const float Left = 15, Top = 15, Bottom = 280, LineH = 4.6f;

    private enum Kind { Title, Header, Gap, SectionTitle, ColHeader, Row, Total }
    private sealed record PLine(Kind Kind, ReportSection? Section = null, string[]? Cells = null, string? Text = null, bool Bold = false);

    public static string? Print(ReportDoc doc, AppState state)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) return "A közvetlen nyomtatás egyelőre csak Windows alatt működik.";
        try { PrintWindows(doc, state.Db.GetSetting("printer.name"), state.Db.GetSetting("printer.font_name") ?? "Arial", new StandardPrintController()); return null; }
        catch (Exception ex) { return "Nyomtatási hiba: " + ex.Message; }
    }

    /// <summary>Renders the pages exactly as they will be printed (PNG images), for the on-screen preview.</summary>
    public static List<byte[]> Preview(ReportDoc doc, AppState state)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) return [];
        var pc = new PreviewPrintController();
        PrintWindows(doc, state.Db.GetSetting("printer.name"), state.Db.GetSetting("printer.font_name") ?? "Arial", pc);
        var pages = new List<byte[]>();
        foreach (var info in pc.GetPreviewPageInfo())
        {
            // 150 dpi rendering of an A4 page
            using var bmp = new System.Drawing.Bitmap(1240, 1754);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.Clear(System.Drawing.Color.White);
                g.DrawImage(info.Image, 0, 0, bmp.Width, bmp.Height);
            }
            using var ms = new MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            pages.Add(ms.ToArray());
            info.Image.Dispose();
        }
        return pages;
    }

    [SupportedOSPlatform("windows6.1")]
    private static void PrintWindows(ReportDoc doc, string? printer, string fontName, PrintController controller)
    {
        var lines = new List<PLine> { new(Kind.Title, Text: doc.Title) };
        lines.AddRange(doc.HeaderLines.Select(h => new PLine(Kind.Header, Text: h)));
        foreach (var s in doc.Sections)
        {
            lines.Add(new PLine(Kind.Gap));
            lines.Add(new PLine(Kind.SectionTitle, s, Text: s.Title));
            lines.Add(new PLine(Kind.ColHeader, s));
            for (int i = 0; i < s.Rows.Count; i++)
            {
                bool isBold = s.BoldRows.Contains(i);
                lines.Add(new PLine(Kind.Row, s, s.Rows[i], Bold: isBold));
                if (isBold && s.SpaceAfterBold && i < s.Rows.Count - 1) lines.Add(new PLine(Kind.Gap));
            }
            if (s.TotalRow is not null) lines.Add(new PLine(Kind.Total, s, s.TotalRow));
        }

        using var doc2 = new PrintDocument { DocumentName = doc.Title, PrintController = controller };
        if (!string.IsNullOrWhiteSpace(printer))
        {
            if (!PrinterSettings.InstalledPrinters.Cast<string>().Contains(printer, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException($"A(z) „{printer}” nyomtató nem található.");
            doc2.PrinterSettings.PrinterName = printer;
        }
        int next = 0, page = 0;
        var printed = DateTime.Now;
        using var normal = new System.Drawing.Font(fontName, 8f);
        using var bold = new System.Drawing.Font(fontName, 8f, System.Drawing.FontStyle.Bold);
        using var big = new System.Drawing.Font(fontName, 14f, System.Drawing.FontStyle.Bold);
        using var mid = new System.Drawing.Font(fontName, 10f, System.Drawing.FontStyle.Bold);
        using var pen = new System.Drawing.Pen(System.Drawing.Color.Black, 0.2f);

        doc2.PrintPage += (_, e) =>
        {
            var g = e.Graphics!;
            g.PageUnit = System.Drawing.GraphicsUnit.Millimeter;
            if (controller is not PreviewPrintController)
                g.TranslateTransform(-(float)(e.PageSettings.HardMarginX * 0.254), -(float)(e.PageSettings.HardMarginY * 0.254));
            page++;
            float y = Top;

            void Cells(ReportSection s, string[] cells, System.Drawing.Font font)
            {
                float x = Left;
                for (int i = 0; i < s.Columns.Count; i++)
                {
                    var c = s.Columns[i];
                    using var sf = new System.Drawing.StringFormat(System.Drawing.StringFormatFlags.NoWrap)
                    {
                        Trimming = System.Drawing.StringTrimming.EllipsisCharacter,
                        Alignment = c.Align switch { TextAlign.Right => System.Drawing.StringAlignment.Far, TextAlign.Center => System.Drawing.StringAlignment.Center, _ => System.Drawing.StringAlignment.Near },
                    };
                    g.DrawString(i < cells.Length ? cells[i] : "", font, System.Drawing.Brushes.Black, new System.Drawing.RectangleF(x, y, (float)c.Width - 1, LineH), sf);
                    x += (float)c.Width;
                }
            }
            void ColHeader(ReportSection s)
            {
                Cells(s, s.Columns.Select(c => c.Header).ToArray(), bold);
                g.DrawLine(pen, Left, y + LineH - 0.3f, Left + (float)s.Columns.Sum(c => c.Width), y + LineH - 0.3f);
                y += LineH + 0.5f;
            }

            // A new page never starts with an empty line.
            while (page > 1 && next < lines.Count && lines[next].Kind == Kind.Gap) next++;
            // Continuing a table on a new page: repeat its column header.
            if (page > 1 && next < lines.Count && lines[next].Kind is Kind.Row or Kind.Total) ColHeader(lines[next].Section!);

            while (next < lines.Count)
            {
                var l = lines[next];
                float need = l.Kind switch { Kind.Title => 8, Kind.SectionTitle => LineH * 3, _ => LineH };
                if (y + need > Bottom) break;
                switch (l.Kind)
                {
                    case Kind.Title: g.DrawString(l.Text, big, System.Drawing.Brushes.Black, Left, y); y += 8; break;
                    case Kind.Header: g.DrawString(l.Text, normal, System.Drawing.Brushes.Black, Left, y); y += LineH; break;
                    case Kind.Gap: y += LineH; break;
                    case Kind.SectionTitle: g.DrawString(l.Text, mid, System.Drawing.Brushes.Black, Left, y); y += 6; break;
                    case Kind.ColHeader: ColHeader(l.Section!); break;
                    case Kind.Row: Cells(l.Section!, l.Cells!, l.Bold ? bold : normal); y += LineH; break;
                    case Kind.Total:
                        g.DrawLine(pen, Left, y, Left + (float)l.Section!.Columns.Sum(c => c.Width), y);
                        y += 0.5f;
                        Cells(l.Section, l.Cells!, bold); y += LineH; break;
                }
                next++;
            }

            using var foot = new System.Drawing.StringFormat { Alignment = System.Drawing.StringAlignment.Far };
            g.DrawString($"Nyomtatva: {printed:yyyy.MM.dd. HH:mm}", normal, System.Drawing.Brushes.Black, Left, Bottom + 4);
            g.DrawString($"{page}. oldal", normal, System.Drawing.Brushes.Black, new System.Drawing.RectangleF(Left, Bottom + 4, 180, LineH), foot);
            e.HasMorePages = next < lines.Count;
        };
        doc2.Print();
    }
}

/// <summary>Shows the printed pages of a report; Nyomtatás prints them.</summary>
public sealed class ReportPreviewWindow : Window
{
    public ReportPreviewWindow(AppState state, ReportDoc doc)
    {
        Title = $"{doc.Title} – előnézet";
        Width = 900;
        Height = 760;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var pages = new StackPanel { Spacing = 16, Margin = new Thickness(20), HorizontalAlignment = HorizontalAlignment.Center };
        var images = ReportPrinter.Preview(doc, state);
        foreach (var png in images)
        {
            using var ms = new MemoryStream(png);
            pages.Children.Add(new Border
            {
                BoxShadow = BoxShadows.Parse("0 2 8 0 #60000000"),
                Child = new Image { Source = new Avalonia.Media.Imaging.Bitmap(ms), Width = 800 },
            });
        }
        if (images.Count == 0) pages.Children.Add(new TextBlock { Text = "Előnézet egyelőre csak Windows alatt.", Foreground = Brushes.White });

        var print = new Button { Content = "Nyomtatás", MinWidth = 120, IsDefault = true };
        var close = new Button { Content = "Bezárás", MinWidth = 100, IsCancel = true };
        print.Click += async (_, _) =>
        {
            if (ReportPrinter.Print(doc, state) is { } err) await Dialog.Info(this, "Nyomtatás", err);
            else Close();
        };
        close.Click += (_, _) => Close();
        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(8), HorizontalAlignment = HorizontalAlignment.Right,
            Children = { new TextBlock { Text = $"{images.Count} oldal    Nyomtató:", VerticalAlignment = VerticalAlignment.Center }, Printers.Chooser(state), close, print },
        };
        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Bottom);
        root.Children.Add(bar);
        root.Children.Add(new ScrollViewer { Background = Brushes.Gray, Content = pages });
        Content = root;
    }
}
