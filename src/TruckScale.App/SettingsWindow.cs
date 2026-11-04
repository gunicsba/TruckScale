using System.IO.Ports;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace TruckScale.App;

/// <summary>Installed printers (Windows); empty elsewhere.</summary>
public static class Printers
{
    public static List<string> Installed()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) return [];
        try { return System.Drawing.Printing.PrinterSettings.InstalledPrinters.Cast<string>().ToList(); }
        catch { return []; }
    }

    /// <summary>A printer chooser bound to the "printer.name" setting (saved immediately).</summary>
    public static ComboBox Chooser(AppState state)
    {
        var list = Installed();
        var current = state.Db.GetSetting("printer.name");
        if (!string.IsNullOrWhiteSpace(current) && !list.Contains(current)) list.Insert(0, current); // keep it visible even if missing
        var combo = new ComboBox { ItemsSource = list, SelectedItem = current, MinWidth = 320, PlaceholderText = "alapértelmezett nyomtató" };
        combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is string s) state.Db.SetSetting("printer.name", s); };
        return combo;
    }
}

/// <summary>Beállítás: printer, ticket form, company details, serial port.</summary>
public sealed class SettingsWindow : Window
{
    private readonly AppState _state;
    private readonly Dictionary<string, TextBox> _fields = new();
    private readonly ComboBox _port = new() { MinWidth = 140 };
    private readonly ComboBox _baud = new() { ItemsSource = new[] { "1200", "2400", "4800", "9600", "19200", "38400", "57600", "115200" }, MinWidth = 140 };
    private readonly CheckBox _sim = new() { Content = "Szimulátor használata (mérleg nélkül, teszteléshez)" };
    private readonly ComboBox _printer;

    public SettingsWindow(AppState state)
    {
        _state = state;
        Title = "Beállítás";
        Width = 680;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var s = state.Db.AllSettings();
        _printer = Printers.Chooser(state);

        var ports = SerialPort.GetPortNames().OrderBy(p => p).ToList();
        var curPort = s.GetValueOrDefault("serial.port");
        if (!string.IsNullOrWhiteSpace(curPort) && !ports.Contains(curPort)) ports.Insert(0, curPort);
        _port.ItemsSource = ports;
        _port.SelectedItem = curPort;
        _baud.SelectedItem = s.GetValueOrDefault("serial.baud") ?? "9600";
        _sim.IsChecked = s.GetValueOrDefault("scale.simulator") == "1";

        var formBox = Field("ticket.form", s);
        var browse = new Button { Content = "Tallózás…" };
        browse.Click += async (_, _) =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Mérlegjegy forma kiválasztása",
                FileTypeFilter = [new FilePickerFileType("Forma (*.form)") { Patterns = ["*.form", "*.txt"] }],
            });
            if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) formBox.Text = path;
        };

        var form = new StackPanel { Spacing = 14, Margin = new Thickness(16) };
        form.Children.Add(Section("Nyomtatás",
            Row("Nyomtató", _printer),
            Row("Forma fájl", WithButton(formBox, browse)),
            Hint("Üresen hagyva a beépített mintát használja. Relatív név esetén az adatbázis mellett keresi.")));
        form.Children.Add(Section("Cég (a mérlegjegyen)",
            Row("Név", Field("company.name", s)),
            Row("Utca", Field("company.street", s)),
            Row("Település", TownZip(Field("company.town", s), Field("company.zip", s))),
            Row("Megjegyzés", Field("company.note", s)),
            Hint("A megjegyzés (pl. „… 0130/3 Hrsz”) az Átadó és Átvevő között jelenik meg."),
            Row("Adószám", Field("company.tax_no", s)),
            Row("Kezelő neve", Field("operator.name", s))));
        form.Children.Add(Section("Mérleg",
            Row("Soros port", _port),
            Row("Sebesség", _baud),
            Row("", _sim),
            Hint("A mérleg beállításai a program újraindítása után lépnek életbe.")));

        var save = new Button { Content = "Mentés", MinWidth = 100, IsDefault = true };
        var cancel = new Button { Content = "Mégse", MinWidth = 100, IsCancel = true };
        save.Click += (_, _) => Save();
        cancel.Click += (_, _) => Close();
        form.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, save } });
        Content = form;
    }

    private TextBox Field(string key, IReadOnlyDictionary<string, string?> s) => _fields[key] = new TextBox { Text = s.GetValueOrDefault(key) };

    private static Control Row(string label, Control c)
    {
        var g = new Grid { ColumnDefinitions = new("120,*") };
        var l = new TextBlock { Text = label, Classes = { "label" } };
        Grid.SetColumn(c, 1);
        g.Children.Add(l);
        g.Children.Add(c);
        return g;
    }

    private static Control Hint(string text) => new TextBlock { Text = text, Classes = { "hint" }, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(120, 0, 0, 0) };

    private static Control WithButton(Control c, Button b)
    {
        var g = new Grid { ColumnDefinitions = new("*,Auto") };
        b.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetColumn(b, 1);
        g.Children.Add(c);
        g.Children.Add(b);
        return g;
    }

    private static Control TownZip(TextBox town, TextBox zip)
    {
        var g = new Grid { ColumnDefinitions = new("*,Auto,90") };
        var l = new TextBlock { Text = "Ir.szám", Classes = { "label" }, Margin = new Thickness(12, 0, 8, 0) };
        Grid.SetColumn(l, 1);
        Grid.SetColumn(zip, 2);
        g.Children.Add(town);
        g.Children.Add(l);
        g.Children.Add(zip);
        return g;
    }

    private static Control Section(string title, params Control[] rows)
    {
        var p = new StackPanel { Spacing = 6 };
        p.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.Bold, FontSize = 15 });
        foreach (var r in rows) p.Children.Add(r);
        return p;
    }

    private void Save()
    {
        foreach (var (key, box) in _fields)
            _state.Db.SetSetting(key, string.IsNullOrWhiteSpace(box.Text) ? null : box.Text.Trim());
        if (_printer.SelectedItem is string printer) _state.Db.SetSetting("printer.name", printer);
        if (_port.SelectedItem is string port) _state.Db.SetSetting("serial.port", port);
        if (_baud.SelectedItem is string baud) _state.Db.SetSetting("serial.baud", baud);
        _state.Db.SetSetting("scale.simulator", _sim.IsChecked == true ? "1" : "0");
        Close();
    }
}
