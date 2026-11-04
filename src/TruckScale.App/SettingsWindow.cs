using TruckScale.Core.Localization;
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
        var combo = new ComboBox { ItemsSource = list, SelectedItem = current, MinWidth = 320, PlaceholderText = L.T("common.defaultPrinter") };
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is not string s || s == state.Db.GetSetting("printer.name")) return;
            state.Db.SetSetting("printer.name", s);
            TicketService.WarmUp(s);
        };
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
    private readonly CheckBox _sim = new() { Content = L.T("set.sim") };
    private readonly ComboBox _language = new() { MinWidth = 140, ItemsSource = L.Languages.Select(l => l.Name).ToList() };
    private readonly ComboBox _printer;

    public SettingsWindow(AppState state)
    {
        _state = state;
        Title = L.T("set.title");
        _language.SelectedIndex = L.Languages.ToList().FindIndex(l => l.Code == L.Current.Code);
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
        var browse = new Button { Content = L.T("set.browse") };
        browse.Click += async (_, _) =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = L.T("set.formPick"),
                FileTypeFilter = [new FilePickerFileType(L.T("set.formType")) { Patterns = ["*.form", "*.txt"] }],
            });
            if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) formBox.Text = path;
        };

        var form = new StackPanel { Spacing = 14, Margin = new Thickness(16) };
        form.Children.Add(Section(L.T("set.printing"),
            Row(L.T("set.printer"), _printer),
            Row(L.T("set.form"), WithButton(formBox, browse)),
            Hint(L.T("set.formHint"))));
        form.Children.Add(Section(L.T("set.company"),
            Row(L.T("f.name"), Field("company.name", s)),
            Row(L.T("f.street"), Field("company.street", s)),
            Row(L.T("f.town"), TownZip(Field("company.town", s), Field("company.zip", s))),
            Row(L.T("f.note"), Field("company.note", s)),
            Hint(L.T("set.noteHint")),
            Row(L.T("set.taxNo"), Field("company.tax_no", s)),
            Row(L.T("set.operator"), Field("operator.name", s))));
        form.Children.Add(Section(L.T("set.scale"),
            Row(L.T("set.port"), _port),
            Row(L.T("set.baud"), _baud),
            Row("", _sim),
            Hint(L.T("set.scaleHint"))));
        form.Children.Add(Section(L.T("set.language"), Row(L.T("set.language"), _language)));

        var save = new Button { Content = L.T("common.save"), MinWidth = 100, IsDefault = true };
        var cancel = new Button { Content = L.T("common.cancel"), MinWidth = 100, IsCancel = true };
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
        var l = new TextBlock { Text = L.T("f.zip"), Classes = { "label" }, Margin = new Thickness(12, 0, 8, 0) };
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

    private async void Save()
    {
        foreach (var (key, box) in _fields)
            _state.Db.SetSetting(key, string.IsNullOrWhiteSpace(box.Text) ? null : box.Text.Trim());
        if (_printer.SelectedItem is string printer && printer != _state.Db.GetSetting("printer.name"))
        {
            _state.Db.SetSetting("printer.name", printer);
            TicketService.WarmUp(printer);
        }
        if (_port.SelectedItem is string port) _state.Db.SetSetting("serial.port", port);
        if (_baud.SelectedItem is string baud) _state.Db.SetSetting("serial.baud", baud);
        _state.Db.SetSetting("scale.simulator", _sim.IsChecked == true ? "1" : "0");
        var lang = L.Languages[Math.Max(0, _language.SelectedIndex)];
        if (lang.Code != L.Current.Code)
        {
            _state.Db.SetSetting("ui.language", lang.Code);
            L.Set(lang.Code);
            if (await Dialog.Confirm(this, L.T("common.restartTitle"), L.T("common.restartNow"))) { _state.Restart(); return; }
        }
        Close();
    }
}
