using TruckScale.Core.Localization;
using System.Drawing.Printing;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
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
    private readonly FilterBar _filter = new(withTicketAndDate: true);
    private readonly CheckBox _showVoid = new() { Content = L.T("log.showVoid"), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _count = new() { Classes = { "hint" }, VerticalAlignment = VerticalAlignment.Center };
    private readonly DataGrid _grid = Grids.Weighings(withStatus: true);
    private List<Weighing> _all = [];

    public LogWindow(AppState state)
    {
        WindowFit.ClampToScreen(this);
        _state = state;
        Title = L.T("log.title");
        Width = 1500;
        Height = 760;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _all = state.Db.ClosedWeighings(new DateTime(1900, 1, 1), new DateTime(2100, 1, 1));
        _filter.Changed += Filter;
        _showVoid.IsCheckedChanged += (_, _) => Filter();
        _grid.DoubleTapped += async (_, _) => await OpenTicket();
        _grid.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await OpenTicket(); } };

        var ticket = new Button { Content = L.T("log.ticket") };
        ticket.Click += async (_, _) => await OpenTicket();
        var close = new Button { Content = L.T("common.close"), IsCancel = true };
        close.Click += (_, _) => Close();

        var top = new StackPanel
        {
            Spacing = 6, Margin = new Thickness(0, 0, 0, 8),
            Children = { _filter, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Children = { _showVoid, _count } } },
        };
        var bottom = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(close, Dock.Right);
        bottom.Children.Add(close);
        bottom.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { ticket, new TextBlock { Text = "   " + L.T("log.tip"), Classes = { "hint" }, VerticalAlignment = VerticalAlignment.Center } } });
        var root = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(bottom);
        root.Children.Add(_grid);
        Content = root;

        Opened += (_, _) => _filter.FocusFirst();
        Filter();
    }

    private void Filter()
    {
        var f = _filter.Filter;
        var rows = _all.Where(w => (_showVoid.IsChecked == true || w.Status != WeighingStatus.Void) && f.Matches(w)).ToList();
        _grid.ItemsSource = rows;
        var t = Summary.Total(rows.Where(r => r.Status == WeighingStatus.Done));
        _count.Text = L.F("log.count", rows.Count, L.Kg(t.Net));
    }

    private async Task OpenTicket()
    {
        if (_grid.SelectedItem is Weighing { Status: WeighingStatus.Done } w)
            await new TicketPreviewWindow(_state, w).ShowDialog(this);
    }
}

/// <summary>Per-column search fields (Napló, Áttekintés). Every filled field must match.</summary>
public sealed class FilterBar : WrapPanel
{
    private readonly TextBox? _ticket, _date;
    private readonly TextBox _plate, _partner, _driver, _material, _marking;
    private readonly ComboBox _dir = new() { ItemsSource = new[] { L.T("dir.all"), L.T("dir.in"), L.T("dir.out") }, SelectedIndex = 0, Width = 130 };
    private readonly List<(string Label, TextBox Box)> _boxes = [];

    public event Action? Changed;

    public FilterBar(bool withTicketAndDate)
    {
        TextBox Box(string label, double width, string? hint = null)
        {
            var b = new TextBox { Width = width, PlaceholderText = hint };
            b.TextChanged += (_, _) => Changed?.Invoke();
            _boxes.Add((label, b));
            Children.Add(Field(label, b));
            return b;
        }
        if (withTicketAndDate)
        {
            _ticket = Box(L.T("f.ticket"), 70);
            _date = Box(L.T("f.date"), 110, L.T("filter.datePlaceholder"));
        }
        Children.Add(Field(L.T("f.direction"), _dir));
        _dir.SelectionChanged += (_, _) => Changed?.Invoke();
        _plate = Box(L.T("f.plate"), 130);
        _partner = Box(L.T("f.customer"), 190);
        _driver = Box(L.T("f.driver"), 150);
        _material = Box(L.T("f.material"), 150);
        _marking = Box(L.T("filter.markingNote"), 160);
        var clear = new Button { Content = L.T("filter.clear"), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(4, 0, 0, 0) };
        clear.Click += (_, _) =>
        {
            foreach (var (_, b) in _boxes) b.Text = "";
            _dir.SelectedIndex = 0;
        };
        Children.Add(clear);
    }

    private static Control Field(string label, Control c) => new StackPanel
    {
        Margin = new Thickness(0, 0, 8, 4),
        Children = { new TextBlock { Text = label, Classes = { "hint" } }, c },
    };

    public WeighingFilter Filter => new(
        _ticket?.Text, _date?.Text, _dir.SelectedIndex switch { 1 => Direction.In, 2 => Direction.Out, _ => null },
        _plate.Text, _partner.Text, _driver.Text, _material.Text, _marking.Text);

    public void FocusFirst() => _boxes.FirstOrDefault().Box?.Focus();

    /// <summary>Human-readable description for printouts, e.g. "Irány: Beszállítás, Ügyfél: kiss".</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (_dir.SelectedIndex > 0) parts.Add($"{L.T("f.direction")}: {_dir.SelectedItem}");
        parts.AddRange(_boxes.Where(x => !string.IsNullOrWhiteSpace(x.Box.Text)).Select(x => $"{x.Label}: „{x.Box.Text!.Trim()}”"));
        return parts.Count == 0 ? L.T("filter.none") : string.Join(", ", parts);
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
        void Col(string header, string path, string? format = null, bool star = false, string? sortBy = null) =>
            g.Columns.Add(new DataGridTextColumn
            {
                Header = header, Binding = new Binding(path) { StringFormat = format }, SortMemberPath = sortBy ?? path,
                Width = star ? new DataGridLength(220) : DataGridLength.Auto,
            });
        Col(L.T("f.ticket"), "TicketNo");
        Col(L.T("f.closed"), "ClosedAtText", sortBy: "ClosedAt");
        if (withStatus) Col(L.T("f.status"), "StatusText");
        Col(L.T("f.direction"), "DirectionText");
        Col(L.T("f.plate"), "Plate");
        Col(L.T("f.customer"), "PartnerName", star: true);
        Col(L.T("f.driver"), "DriverName");
        Col(L.T("f.material"), "MaterialName");
        Col(L.T("f.marking"), "Marking");
        Col(L.T("f.note"), "Note");
        Col(L.T("f.gross"), "Gross", "{0:N0}");
        Col(L.T("f.tare"), "Tare", "{0:N0}");
        Col(L.T("f.net"), "Net", "{0:N0}");
        return g;
    }
}

// ============================================================================ Áttekintés

/// <summary>Áttekintés: totals for a period (optionally per direction / customer / material), printable.</summary>
public sealed class SummaryWindow : Window
{
    private readonly AppState _state;
    private readonly List<Weighing> _all;
    private readonly CalendarDatePicker _from = new() { SelectedDateFormat = CalendarDatePickerFormat.Custom, CustomDateFormatString = L.T("fmt.date") };
    private readonly CalendarDatePicker _to = new() { SelectedDateFormat = CalendarDatePickerFormat.Custom, CustomDateFormatString = L.T("fmt.date") };
    private readonly FilterBar _filter = new(withTicketAndDate: false);
    private readonly ComboBox _grouping = new()
    {
        ItemsSource = new[] { L.T("sum.byMaterial"), L.T("sum.byMaterialCustomer"), L.T("sum.byCustomerMaterial") }, SelectedIndex = 1, MinWidth = 240,
    };
    private SummaryGrouping Grouping => (SummaryGrouping)Math.Max(0, _grouping.SelectedIndex);
    private readonly CheckBox _itemized = new() { Content = L.T("sum.itemized"), IsChecked = true };
    private readonly DataGrid _grid = new() { IsReadOnly = true, CanUserSortColumns = true, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal };
    private readonly TextBlock _total = new() { FontWeight = FontWeight.Bold, FontSize = 15 };
    private List<Weighing> _rows = [];

    public SummaryWindow(AppState state)
    {
        WindowFit.ClampToScreen(this);
        _state = state;
        Title = L.T("sum.title");
        Width = 1180;
        Height = 700;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _all = state.Db.ClosedWeighings(new DateTime(1900, 1, 1), new DateTime(2100, 1, 1));
        var today = DateTime.Today;
        _from.SelectedDate = new DateTime(today.Year, 1, 1);
        _to.SelectedDate = today;

        foreach (var (h, p, f) in new[] { (L.T("f.direction"), "DirectionText", null), (L.T("f.material"), "Level1", null), (L.T("f.customer"), "Level2", null), (L.T("f.count"), "Count", null),
                     (L.T("f.grossKg"), "Gross", "{0:N0}"), (L.T("f.tareKg"), "Tare", "{0:N0}"), (L.T("f.netKg"), "Net", "{0:N0}") })
            _grid.Columns.Add(new DataGridTextColumn { Header = h, Binding = new Binding(p) { StringFormat = f }, SortMemberPath = p,
                Width = p is "Level1" or "Level2" ? new DataGridLength(1, DataGridLengthUnitType.Star) : DataGridLength.Auto });
        // Subtotal lines in bold.
        _grid.LoadingRow += (_, e) => e.Row.FontWeight = e.Row.DataContext is SummaryRow { IsSubtotal: true } ? FontWeight.Bold : FontWeight.Normal;
        _grouping.SelectionChanged += (_, _) => Recalc();

        _from.SelectedDateChanged += (_, _) => Recalc();
        _to.SelectedDateChanged += (_, _) => Recalc();
        _filter.Changed += Recalc;

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
                Quick(L.T("sum.today"), () => (today, today)),
                Quick(L.T("sum.thisMonth"), () => (new DateTime(today.Year, today.Month, 1), today)),
                Quick(L.T("sum.prevMonth"), () => { var f = new DateTime(today.Year, today.Month, 1).AddMonths(-1); return (f, f.AddMonths(1).AddDays(-1)); }),
                Quick(L.T("sum.thisYear"), () => (new DateTime(today.Year, 1, 1), today)),
                Quick(L.T("sum.lastYear"), () => (new DateTime(today.Year - 1, 1, 1), new DateTime(today.Year - 1, 12, 31))),
                Quick(L.T("sum.all"), () => (_all.Count == 0 ? today : _all.Min(w => w.ClosedAt ?? today).Date, today)),
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
                new WrapPanel { Children = { Labeled(L.T("sum.period"), _from), new TextBlock { Text = " – ", VerticalAlignment = VerticalAlignment.Center }, _to, new Border { Width = 12 }, quick } },
                _filter,
                new WrapPanel { Children = { Labeled(L.T("sum.grouping"), _grouping) } },
            },
        };

        var print = new Button { Content = L.T("common.print"), MinWidth = 120, IsDefault = true };
        var close = new Button { Content = L.T("common.close"), MinWidth = 100, IsCancel = true };
        print.Click += async (_, _) => await Print();
        var preview = new Button { Content = L.T("common.preview"), MinWidth = 100 };
        preview.Click += async (_, _) => await ShowPreview();
        close.Click += (_, _) => Close();
        var csvSummary = new MenuItem { Header = L.T("sum.csvSummary") };
        var csvItems = new MenuItem { Header = L.T("sum.csvItems") };
        csvSummary.Click += async (_, _) =>
        {
            var (h1, h2) = Headers();
            await ExportCsv("osszesito", Csv.Summary(Summary.Build(_rows, Grouping), h1, h2));
        };
        csvItems.Click += async (_, _) => await ExportCsv("meresek", Csv.Weighings(_rows));
        var csv = new Button { Content = L.T("sum.csv") + " ▾", MinWidth = 110, Flyout = new MenuFlyout { Items = { csvSummary, csvItems } } };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right,
            Children = { new TextBlock { Text = L.T("common.printer"), VerticalAlignment = VerticalAlignment.Center }, Printers.Chooser(state), _itemized, csv, close, preview, print } };
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

    /// <summary>Saves a CSV file (the period goes into the suggested file name).</summary>
    private async Task ExportCsv(string baseName, string content)
    {
        var f = CurrentFilter();
        var file = await StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = L.T("sum.csv"),
            SuggestedFileName = $"{baseName}_{f.From:yyyyMMdd}-{f.To:yyyyMMdd}.csv",
            DefaultExtension = "csv",
            FileTypeChoices = [new Avalonia.Platform.Storage.FilePickerFileType(L.T("csv.type")) { Patterns = ["*.csv"] }],
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        try
        {
            await File.WriteAllTextAsync(path, content, Csv.Encoding);
            await Dialog.Info(this, L.T("sum.csv"), L.F("sum.csvSaved", path));
        }
        catch (IOException ex) { await Dialog.Info(this, L.T("sum.csv"), ex.Message); }
    }

    private SummaryFilter CurrentFilter() => new(
        _from.SelectedDate ?? DateTime.Today, _to.SelectedDate ?? DateTime.Today, Text: _filter.Filter);

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
        _total.Text = L.F("sum.totalLine", t.Count, L.Number(t.Gross), L.Number(t.Tare), L.Number(t.Net));
    }

    private (string, string?) Headers() => Grouping switch
    {
        SummaryGrouping.MaterialPartner => (L.T("f.material"), L.T("f.customer")),
        SummaryGrouping.PartnerMaterial => (L.T("f.customer"), L.T("f.material")),
        _ => (L.T("f.material"), null),
    };

    private async Task Print()
    {
        if (_rows.Count == 0) { await Dialog.Info(this, L.T("common.print"), L.T("sum.nothing")); return; }
        var err = ReportPrinter.Print(BuildDoc(), _state);
        if (err is not null) await Dialog.Info(this, L.T("common.print"), err);
    }

    private async Task ShowPreview()
    {
        if (_rows.Count == 0) { await Dialog.Info(this, L.T("common.preview"), L.T("sum.nothing")); return; }
        var doc = BuildDoc();
        await new ReportPreviewWindow(_state, doc).ShowDialog(this);
    }

    public ReportDoc BuildDoc()
    {
        var f = CurrentFilter();
        var s = _state.Db.AllSettings();
        string N(long v) => L.Number(v);
        var header = new List<string>
        {
            string.Join(", ", new[] { s.GetValueOrDefault("company.name"), s.GetValueOrDefault("company.zip") + " " + s.GetValueOrDefault("company.town"), s.GetValueOrDefault("company.street") }
                .Where(x => !string.IsNullOrWhiteSpace(x))),
            L.F("sum.periodLine", L.Date(f.From), L.Date(f.To)),
            L.F("sum.filterLine", _filter.Describe()),
        };
        var sections = new List<ReportSection>();
        var t = Summary.Total(_rows);
        var (h1, h2) = Headers();
        var summary = Summary.Build(_rows, Grouping);
        List<ReportColumn> cols = h2 is null
            ? [new(L.T("f.direction"), 26, TextAlign.Left), new(h1, 64, TextAlign.Left), new(L.T("f.countShort"), 15, TextAlign.Right),
               new(L.T("f.grossKg"), 25, TextAlign.Right), new(L.T("f.tareKg"), 25, TextAlign.Right), new(L.T("f.netKg"), 25, TextAlign.Right)]
            : [new(L.T("f.direction"), 22, TextAlign.Left), new(h1, 38, TextAlign.Left), new(h2, 45, TextAlign.Left), new(L.T("f.countShort"), 12, TextAlign.Right),
               new(L.T("f.grossKg"), 21, TextAlign.Right), new(L.T("f.tareKg"), 21, TextAlign.Right), new(L.T("f.netKg"), 21, TextAlign.Right)];
        string[] Cells(SummaryRow r) => h2 is null
            ? [r.DirectionText, r.Level1, r.Count.ToString(), N(r.Gross), N(r.Tare), N(r.Net)]
            : [r.IsSubtotal ? "" : r.DirectionText, r.Level1, r.IsSubtotal ? L.T("common.total") : r.Level2 ?? "",
               r.Count.ToString(), N(r.Gross), N(r.Tare), N(r.Net)];
        string[] total = h2 is null
            ? [L.T("common.total"), "", t.Count.ToString(), N(t.Gross), N(t.Tare), N(t.Net)]
            : [L.T("common.total"), "", "", t.Count.ToString(), N(t.Gross), N(t.Tare), N(t.Net)];
        sections.Add(new ReportSection(L.F("sum.section", _grouping.SelectedItem), cols, summary.Select(Cells).ToList(), total)
        {
            BoldRows = summary.Select((r, i) => (r, i)).Where(x => x.r.IsSubtotal).Select(x => x.i).ToHashSet(),
            SpaceAfterBold = true,
        });
        if (_itemized.IsChecked == true)
            sections.Add(new ReportSection(L.T("sum.items"),
                [new(L.T("f.ticket"), 11, TextAlign.Left), new(L.T("f.date"), 25, TextAlign.Left), new(L.T("f.direction"), 8, TextAlign.Left), new(L.T("f.plate"), 26, TextAlign.Left),
                 new(L.T("f.customer"), 40, TextAlign.Left), new(L.T("f.material"), 24, TextAlign.Left), new(L.T("f.gross"), 15, TextAlign.Right), new(L.T("f.tare"), 15, TextAlign.Right), new(L.T("f.net"), 16, TextAlign.Right)],
                _rows.Select(w => new[] { w.TicketNo?.ToString() ?? "", L.DateTime(w.ClosedAt), L.T(w.Direction == Direction.In ? "dir.inShort" : "dir.outShort"),
                    w.Plate ?? "", w.PartnerName ?? "", w.MaterialName ?? "", N(w.Gross ?? 0), N(w.Tare ?? 0), N(w.Net ?? 0) }).ToList(),
                ["", "", "", "", L.T("common.total"), "", N(t.Gross), N(t.Tare), N(t.Net)]));

        return new ReportDoc(L.T("sum.reportTitle"), header, sections);
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
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) return L.T("print.windowsOnly");
        try { PrintWindows(doc, state.Db.GetSetting("printer.name"), state.Db.GetSetting("printer.font_name") ?? "Arial"); return null; }
        catch (Exception ex) { return L.F("print.error", ex.Message); }
    }

    /// <summary>
    /// Renders full A4 pages (PNG, 150 dpi) with exactly the same drawing code as the printout,
    /// for the on-screen preview. Needs no printer.
    /// </summary>
    public static List<byte[]> Preview(ReportDoc doc, AppState state)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) return [];
        var fontName = state.Db.GetSetting("printer.font_name") ?? "Arial";
        int total = CountPages(doc, fontName);
        using var pager = new Pager(doc, fontName, total);
        var pages = new List<byte[]>();
        bool more = true;
        while (more && pages.Count < 1000)
        {
            using var bmp = new System.Drawing.Bitmap(1240, 1754);
            bmp.SetResolution(150, 150);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.Clear(System.Drawing.Color.White);
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                g.PageUnit = System.Drawing.GraphicsUnit.Millimeter;
                more = pager.DrawPage(g);
            }
            using var ms = new MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            pages.Add(ms.ToArray());
        }
        return pages;
    }

    [SupportedOSPlatform("windows6.1")]
    private static int CountPages(ReportDoc doc, string fontName)
    {
        using var pager = new Pager(doc, fontName, 0);
        using var bmp = new System.Drawing.Bitmap(10, 10);
        bmp.SetResolution(150, 150);
        using var g = System.Drawing.Graphics.FromImage(bmp);
        g.PageUnit = System.Drawing.GraphicsUnit.Millimeter;
        int n = 1;
        while (pager.DrawPage(g) && n < 1000) n++;
        return n;
    }

    [SupportedOSPlatform("windows6.1")]
    private static void PrintWindows(ReportDoc doc, string? printer, string fontName)
    {
        int total = CountPages(doc, fontName);
        using var pd = new PrintDocument { DocumentName = doc.Title, PrintController = new StandardPrintController() };
        if (!string.IsNullOrWhiteSpace(printer))
        {
            pd.PrinterSettings.PrinterName = printer;
            if (!pd.PrinterSettings.IsValid)
                throw new InvalidOperationException(L.F("print.noPrinter", printer));
        }
        TicketService.UseA4(pd);
        using var pager = new Pager(doc, fontName, total);
        pd.PrintPage += (_, e) =>
        {
            var g = e.Graphics!;
            g.PageUnit = System.Drawing.GraphicsUnit.Millimeter;
            // Coordinates are from the paper edge; the printer's origin is at its hard margin.
            g.TranslateTransform(-(float)(e.PageSettings.HardMarginX * 0.254), -(float)(e.PageSettings.HardMarginY * 0.254));
            e.HasMorePages = pager.DrawPage(g);
        };
        pd.Print();
    }

    /// <summary>Lays the report out page by page; the same code draws the printout and the preview.</summary>
    [SupportedOSPlatform("windows6.1")]
    private sealed class Pager : IDisposable
    {
        private readonly List<PLine> _lines = [];
        private readonly int _total;
        private readonly DateTime _printed = DateTime.Now;
        private readonly System.Drawing.Font _normal, _bold, _big, _mid;
        private readonly System.Drawing.Pen _pen = new(System.Drawing.Color.Black, 0.2f);
        private int _next, _page;

        public Pager(ReportDoc doc, string fontName, int totalPages)
        {
            _total = totalPages;
            _normal = new System.Drawing.Font(fontName, 8f);
            _bold = new System.Drawing.Font(fontName, 8f, System.Drawing.FontStyle.Bold);
            _big = new System.Drawing.Font(fontName, 14f, System.Drawing.FontStyle.Bold);
            _mid = new System.Drawing.Font(fontName, 10f, System.Drawing.FontStyle.Bold);
            _lines.Add(new PLine(Kind.Title, Text: doc.Title));
            _lines.AddRange(doc.HeaderLines.Select(h => new PLine(Kind.Header, Text: h)));
            foreach (var s in doc.Sections)
            {
                _lines.Add(new PLine(Kind.Gap));
                _lines.Add(new PLine(Kind.SectionTitle, s, Text: s.Title));
                _lines.Add(new PLine(Kind.ColHeader, s));
                for (int i = 0; i < s.Rows.Count; i++)
                {
                    bool isBold = s.BoldRows.Contains(i);
                    _lines.Add(new PLine(Kind.Row, s, s.Rows[i], Bold: isBold));
                    if (isBold && s.SpaceAfterBold && i < s.Rows.Count - 1) _lines.Add(new PLine(Kind.Gap));
                }
                if (s.TotalRow is not null) _lines.Add(new PLine(Kind.Total, s, s.TotalRow));
            }
        }

        /// <summary>Draws the next page (Graphics in millimetres, origin = paper corner). Returns true if more pages follow.</summary>
        public bool DrawPage(System.Drawing.Graphics g)
        {
            _page++;
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
                Cells(s, s.Columns.Select(c => c.Header).ToArray(), _bold);
                g.DrawLine(_pen, Left, y + LineH - 0.3f, Left + (float)s.Columns.Sum(c => c.Width), y + LineH - 0.3f);
                y += LineH + 0.5f;
            }

            // A new page never starts with an empty line; a table continued on a new page repeats its column header.
            while (_page > 1 && _next < _lines.Count && _lines[_next].Kind == Kind.Gap) _next++;
            if (_page > 1 && _next < _lines.Count && _lines[_next].Kind is Kind.Row or Kind.Total) ColHeader(_lines[_next].Section!);

            while (_next < _lines.Count)
            {
                var l = _lines[_next];
                float need = l.Kind switch { Kind.Title => 8, Kind.SectionTitle => LineH * 3, _ => LineH };
                if (y + need > Bottom) break;
                switch (l.Kind)
                {
                    case Kind.Title: g.DrawString(l.Text, _big, System.Drawing.Brushes.Black, Left, y); y += 8; break;
                    case Kind.Header: g.DrawString(l.Text, _normal, System.Drawing.Brushes.Black, Left, y); y += LineH; break;
                    case Kind.Gap: y += LineH; break;
                    case Kind.SectionTitle: g.DrawString(l.Text, _mid, System.Drawing.Brushes.Black, Left, y); y += 6; break;
                    case Kind.ColHeader: ColHeader(l.Section!); break;
                    case Kind.Row: Cells(l.Section!, l.Cells!, l.Bold ? _bold : _normal); y += LineH; break;
                    case Kind.Total:
                        g.DrawLine(_pen, Left, y, Left + (float)l.Section!.Columns.Sum(c => c.Width), y);
                        y += 0.5f;
                        Cells(l.Section, l.Cells!, _bold); y += LineH; break;
                }
                _next++;
            }

            using var foot = new System.Drawing.StringFormat { Alignment = System.Drawing.StringAlignment.Far };
            g.DrawLine(_pen, Left, Bottom + 2.5f, Left + 180, Bottom + 2.5f);
            g.DrawString(L.F("print.printedAt", L.DateTime(_printed)), _normal, System.Drawing.Brushes.Black, Left, Bottom + 3.5f);
            g.DrawString(_total > 0 ? L.F("print.pageOf", _page, _total) : L.F("print.page", _page), _normal, System.Drawing.Brushes.Black,
                new System.Drawing.RectangleF(Left, Bottom + 3.5f, 180, LineH), foot);
            return _next < _lines.Count;
        }

        public void Dispose()
        {
            _normal.Dispose(); _bold.Dispose(); _big.Dispose(); _mid.Dispose(); _pen.Dispose();
        }
    }
}

/// <summary>Shows the printed pages of a report; Nyomtatás prints them.</summary>
public sealed class ReportPreviewWindow : Window
{
    public ReportPreviewWindow(AppState state, ReportDoc doc)
    {
        WindowFit.ClampToScreen(this);
        Title = L.F("print.previewTitle", doc.Title);
        Width = 980;
        Height = 860;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var pages = new StackPanel { Spacing = 16, Margin = new Thickness(20), HorizontalAlignment = HorizontalAlignment.Center };
        var imgs = new List<Image>();
        double pageWidth = 520; // a whole A4 page fits in the window
        var images = ReportPrinter.Preview(doc, state);
        foreach (var png in images)
        {
            using var ms = new MemoryStream(png);
            pages.Children.Add(new Border
            {
                BoxShadow = BoxShadows.Parse("0 2 8 0 #60000000"),
                Child = imgs.AddAndReturn(new Image { Source = new Avalonia.Media.Imaging.Bitmap(ms), Width = pageWidth }),
            });
        }
        if (images.Count == 0) pages.Children.Add(new TextBlock { Text = L.T("print.previewWindowsOnly"), Foreground = Brushes.White });

        var print = new Button { Content = L.T("common.print"), MinWidth = 120, IsDefault = true };
        var close = new Button { Content = L.T("common.close"), MinWidth = 100, IsCancel = true };
        print.Click += async (_, _) =>
        {
            if (ReportPrinter.Print(doc, state) is { } err) await Dialog.Info(this, L.T("common.print"), err);
            else Close();
        };
        close.Click += (_, _) => Close();
        // Zoom like the ticket preview: 100% = A4 at screen size (≈ 794 px wide); "Egész oldal" fits one page.
        const double FullWidth = 794, Ratio = 1754.0 / 1240.0;
        var scroller = new ScrollViewer { Background = Brushes.Gray, Content = pages, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        var zoomText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MinWidth = 44 };
        void SetWidth(double w)
        {
            pageWidth = Math.Clamp(w, 240, 2400);
            foreach (var i in imgs) i.Width = pageWidth;
            zoomText.Text = $"{pageWidth / FullWidth * 100:0}%";
        }
        void Fit()
        {
            double h = scroller.Bounds.Height - 40, w = scroller.Bounds.Width - 40;
            if (h > 0 && w > 0) SetWidth(Math.Min(w, h / Ratio));
        }
        var zoomOut = new Button { Content = "−", MinWidth = 36 };
        var zoomIn = new Button { Content = "+", MinWidth = 36 };
        var fit = new Button { Content = L.T("common.fitPage") };
        zoomOut.Click += (_, _) => SetWidth(pageWidth / 1.25);
        zoomIn.Click += (_, _) => SetWidth(pageWidth * 1.25);
        fit.Click += (_, _) => Fit();
        scroller.AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
            SetWidth(pageWidth * (e.Delta.Y > 0 ? 1.1 : 1 / 1.1));
            e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Opened += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(Fit, Avalonia.Threading.DispatcherPriority.Loaded);
        SetWidth(pageWidth);
        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(8), HorizontalAlignment = HorizontalAlignment.Right,
            Children =
            {
                zoomOut, zoomText, zoomIn, fit,
                new TextBlock { Text = $"   {L.F("common.pages", images.Count)}    {L.T("common.printer")}", VerticalAlignment = VerticalAlignment.Center },
                Printers.Chooser(state), close, print,
            },
        };
        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Bottom);
        root.Children.Add(bar);
        root.Children.Add(scroller);
        Content = root;
    }
}

internal static class ListExtensions
{
    public static T AddAndReturn<T>(this List<T> list, T item) { list.Add(item); return item; }
}
