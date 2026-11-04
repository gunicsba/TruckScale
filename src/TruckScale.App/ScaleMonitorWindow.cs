using System.IO.Ports;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using TruckScale.Core.Localization;
using TruckScale.Core.Scale;

namespace TruckScale.App;

/// <summary>
/// Mérleg teszt / nyers adat: shows exactly what the indicator sends, lets you set the port and the positions of
/// the "stable" marker and the weight (BWS = old program's format), with a ruler, live result, a test line
/// (works without a scale) and automatic baud rate detection.
/// </summary>
public sealed class ScaleMonitorWindow : Window
{
    private static readonly FontFamily Mono = new("Consolas, DejaVu Sans Mono, Menlo, monospace");
    private readonly AppState _state;
    private SerialScale? _monitor;
    private bool _stoppedMain;
    private CancellationTokenSource? _detect;
    private string? _lastLine;
    private bool _loading;

    // connection
    private readonly ComboBox _port = new() { MinWidth = 110 };
    private readonly ComboBox _baud = new() { ItemsSource = BaudDetector.Bauds.Order().Select(b => b.ToString()).ToList(), MinWidth = 100 };
    private readonly ComboBox _parity = new() { ItemsSource = new[] { "N", "E", "O" }, MinWidth = 60 };
    private readonly ComboBox _dataBits = new() { ItemsSource = new[] { "8", "7" }, MinWidth = 60 };
    private readonly ComboBox _stopBits = new() { ItemsSource = new[] { "1", "2" }, MinWidth = 60 };
    private readonly ComboBox _terminator = new() { ItemsSource = new[] { "CR LF", "CR", "LF" }, MinWidth = 90 };
    private readonly Button _connect = new();
    private readonly Button _detectButton = new();

    // parsing
    private readonly ComboBox _preset = new() { MinWidth = 160 };
    private readonly NumericUpDown _stStart = Num(), _stLength = Num(), _wStart = Num(), _wLength = Num(), _decimals = Num(max: 6);
    private readonly TextBox _stText = new() { Width = 70 };

    // output
    private readonly TextBox _log = new() { IsReadOnly = true, AcceptsReturn = true, FontFamily = Mono, FontSize = 13, TextWrapping = TextWrapping.NoWrap };
    private readonly TextBlock _analysis = new() { FontFamily = Mono, FontSize = 15 };
    private readonly TextBlock _result = new() { FontSize = 16, FontWeight = FontWeight.Bold };
    private readonly TextBox _testLine = new() { FontFamily = Mono };
    private readonly TextBlock _status = new() { Classes = { "hint" } };
    private readonly List<string> _logLines = [];

    private static NumericUpDown Num(int max = 200) => new() { Minimum = 0, Maximum = max, Increment = 1, FormatString = "0", Width = 110 };

    public ScaleMonitorWindow(AppState state)
    {
        _state = state;
        WindowFit.ClampToScreen(this);
        Title = L.T("scm.title");
        Width = 1000;
        Height = 820;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        // ---- initial values from the settings
        var s = state.Db.AllSettings();
        var serial = SerialSettings.From(s);
        var proto = ScaleProtocol.From(s);
        var ports = SerialPort.GetPortNames().Order().ToList();
        if (serial.Port.Length > 0 && !ports.Contains(serial.Port)) ports.Insert(0, serial.Port);
        _port.ItemsSource = ports;
        _port.SelectedItem = serial.Port.Length > 0 ? serial.Port : ports.FirstOrDefault();
        _baud.SelectedItem = serial.Baud.ToString();
        _parity.SelectedItem = serial.Parity switch { Parity.Even => "E", Parity.Odd => "O", _ => "N" };
        _dataBits.SelectedItem = serial.DataBits.ToString();
        _stopBits.SelectedItem = serial.StopBits == StopBits.Two ? "2" : "1";
        _preset.ItemsSource = new[] { L.T("scm.preset.bws"), L.T("scm.preset.auto"), L.T("scm.preset.custom") };
        LoadProtocol(proto);

        // ---- events
        foreach (var n in new[] { _stStart, _stLength, _wStart, _wLength, _decimals })
            n.ValueChanged += (_, _) => { if (!_loading) { _preset.SelectedIndex = PresetOf(CurrentProtocol()); Analyse(); } };
        _stText.TextChanged += (_, _) => { if (!_loading) { _preset.SelectedIndex = PresetOf(CurrentProtocol()); Analyse(); } };
        _preset.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            if (_preset.SelectedIndex == 0) LoadProtocol(new ScaleProtocol(TerminatorName: CurrentProtocol().TerminatorName));
            Analyse();
        };
        _terminator.SelectionChanged += (_, _) => { if (!_loading && _monitor is not null) Connect(); Analyse(); };
        _testLine.TextChanged += (_, _) => Analyse();
        _connect.Click += (_, _) => { if (_monitor is null) Connect(); else Disconnect(); };
        _detectButton.Click += async (_, _) => await DetectBaud();

        // ---- layout
        Control Labeled(string label, Control c) => new StackPanel
        {
            Margin = new Thickness(0, 0, 10, 4),
            Children = { new TextBlock { Text = label, Classes = { "hint" } }, c },
        };
        var connection = new WrapPanel
        {
            Children =
            {
                Labeled(L.T("set.port"), _port), Labeled(L.T("set.baud"), _baud), Labeled(L.T("scm.parity"), _parity),
                Labeled(L.T("scm.dataBits"), _dataBits), Labeled(L.T("scm.stopBits"), _stopBits), Labeled(L.T("scm.terminator"), _terminator),
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 4), Children = { _connect, _detectButton } },
            },
        };
        var parsing = new WrapPanel
        {
            Children =
            {
                Labeled(L.T("scm.format"), _preset),
                Labeled(L.T("scm.stableStart"), _stStart), Labeled(L.T("scm.stableLength"), _stLength), Labeled(L.T("scm.stableText"), _stText),
                Labeled(L.T("scm.weightStart"), _wStart), Labeled(L.T("scm.weightLength"), _wLength), Labeled(L.T("scm.decimals"), _decimals),
            },
        };
        var clear = new Button { Content = L.T("scm.clear") };
        clear.Click += (_, _) => { _logLines.Clear(); _log.Text = ""; };
        var save = new Button { Content = L.T("common.save"), MinWidth = 100, IsDefault = true };
        var cancel = new Button { Content = L.T("common.cancel"), MinWidth = 100, IsCancel = true };
        save.Click += (_, _) => { Save(); Close(); };
        cancel.Click += (_, _) => Close();

        var analysisBox = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#f4f4f4")), BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 6), Child = new StackPanel { Spacing = 4, Children = { _analysis, _result } },
        };
        var top = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                Section(L.T("scm.connection")), connection,
                Section(L.T("scm.parsing")), parsing,
                analysisBox,
                new Grid { ColumnDefinitions = new("Auto,*"), Children = { new TextBlock { Text = L.T("scm.testLine"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) }, At(_testLine, 1) } },
                new DockPanel { Children = { Docked(clear, Dock.Right), Section(L.T("scm.raw")) } },
            },
        };
        var bottom = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { cancel, save } };
        DockPanel.SetDock(buttons, Dock.Right);
        bottom.Children.Add(buttons);
        bottom.Children.Add(_status);

        var root = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(bottom);
        root.Children.Add(_log);
        Content = root;

        Closed += (_, _) =>
        {
            _detect?.Cancel();
            _monitor?.Dispose();
            if (_stoppedMain) _state.RestartScale();
        };
        UpdateButtons();
        _status.Text = L.T("scm.hint");
        Analyse();
    }

    private static Control Section(string text) => new TextBlock { Text = text, FontWeight = FontWeight.Bold, FontSize = 15, Margin = new Thickness(0, 6, 0, 0) };
    private static Control At(Control c, int column) { Grid.SetColumn(c, column); return c; }
    private static Control Docked(Control c, Dock dock) { DockPanel.SetDock(c, dock); return c; }

    // ---------------------------------------------------------------- protocol <-> fields

    private void LoadProtocol(ScaleProtocol p)
    {
        _loading = true;
        _stStart.Value = p.StableStart; _stLength.Value = p.StableLength; _stText.Text = p.StableText;
        _wStart.Value = p.WeightStart; _wLength.Value = p.WeightLength; _decimals.Value = p.Decimals;
        _terminator.SelectedIndex = Array.IndexOf(ScaleProtocol.Terminators, p.TerminatorName) is var i and >= 0 ? i : 0;
        _preset.SelectedIndex = p.Mode == ParseMode.Auto ? 1 : PresetOf(p);
        _loading = false;
    }

    private ScaleProtocol CurrentProtocol() => new(
        _preset.SelectedIndex == 1 ? ParseMode.Auto : ParseMode.Fixed,
        (int)(_stStart.Value ?? 0), (int)(_stLength.Value ?? 0), _stText.Text ?? "",
        (int)(_wStart.Value ?? 0), (int)(_wLength.Value ?? 0), (int)(_decimals.Value ?? 0),
        ScaleProtocol.Terminators[Math.Max(0, _terminator.SelectedIndex)]);

    /// <summary>0 = BWS (old program's settings), 2 = custom.</summary>
    private static int PresetOf(ScaleProtocol p) =>
        p with { TerminatorName = "CRLF" } == new ScaleProtocol() with { Mode = p.Mode } && p.Mode == ParseMode.Fixed ? 0 : p.Mode == ParseMode.Auto ? 1 : 2;

    private SerialSettings CurrentSerial() => new(
        _port.SelectedItem as string ?? "",
        int.TryParse(_baud.SelectedItem as string, out var b) ? b : 9600,
        (_parity.SelectedItem as string) switch { "E" => Parity.Even, "O" => Parity.Odd, _ => Parity.None },
        int.TryParse(_dataBits.SelectedItem as string, out var d) ? d : 8,
        (_stopBits.SelectedItem as string) == "2" ? StopBits.Two : StopBits.One);

    // ---------------------------------------------------------------- analysis of the last (or test) line

    private void Analyse()
    {
        var line = string.IsNullOrEmpty(_testLine.Text) ? _lastLine : _testLine.Text;
        var p = CurrentProtocol();
        if (line is null)
        {
            _analysis.Text = L.T("scm.noData");
            _result.Text = "";
            return;
        }
        _analysis.Text =
            $"{L.T("scm.position"),-10}{ScaleProtocol.Ruler(Math.Max(line.Length, 10))}\n" +
            $"{L.T("scm.line"),-10}{ScaleProtocol.Visible(line)}\n" +
            (p.Mode == ParseMode.Fixed ? $"{L.T("scm.marks"),-10}{p.Marks(line.Length)}" : $"{"",-10}{L.T("scm.autoInfo")}");
        if (p.Parse(line) is { } r)
        {
            _result.Text = L.F("scm.result", L.T(r.IsStable ? "common.yes" : "common.no"), L.Kg(r.Weight));
            _result.Foreground = r.IsStable ? Brushes.DarkGreen : Brushes.DarkOrange;
        }
        else
        {
            _result.Text = L.T("scm.unreadable");
            _result.Foreground = Brushes.Firebrick;
        }
    }

    // ---------------------------------------------------------------- connection

    private void Connect()
    {
        Disconnect();
        var serial = CurrentSerial();
        if (serial.Port.Length == 0) { _status.Text = L.T("scm.noPort"); return; }
        if (!_stoppedMain) { _state.StopScale(); _stoppedMain = true; }
        var m = new SerialScale(serial, CurrentProtocol());
        m.RawReceived += raw => Dispatcher.UIThread.Post(() => AddRaw(raw));
        m.LineReceived += line => Dispatcher.UIThread.Post(() => { _lastLine = line; if (string.IsNullOrEmpty(_testLine.Text)) Analyse(); });
        m.Error += err => Dispatcher.UIThread.Post(() => _status.Text = L.F("scm.error", err));
        _monitor = m;
        m.Start();
        _status.Text = L.F("scm.connected", serial.Port, serial.Baud);
        UpdateButtons();
    }

    private void Disconnect()
    {
        _monitor?.Dispose();
        _monitor = null;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        _connect.Content = L.T(_monitor is null ? "scm.connect" : "scm.disconnect");
        _detectButton.Content = L.T(_detect is null ? "scm.detect" : "scm.detectStop");
    }

    /// <summary>Raw data in the log: one row per received line, control characters made visible.</summary>
    private void AddRaw(string raw)
    {
        var visible = ScaleProtocol.Visible(raw);
        var stamp = DateTime.Now.ToString("HH:mm:ss.fff");
        // start a new row after each LF so that every received line gets its own row
        var parts = visible.Split("<LF>");
        for (int i = 0; i < parts.Length; i++)
        {
            var part = parts[i] + (i < parts.Length - 1 ? "<LF>" : "");
            if (part.Length == 0) continue;
            if (_logLines.Count > 0 && !_logLines[^1].EndsWith("<LF>"))
                _logLines[^1] += part;     // continue an unfinished line
            else
                _logLines.Add($"{stamp}  {part}");
        }
        if (_logLines.Count > 400) _logLines.RemoveRange(0, _logLines.Count - 400);
        _log.Text = string.Join("\n", _logLines);
        _log.CaretIndex = _log.Text.Length;
    }

    private async Task DetectBaud()
    {
        if (_detect is not null) { _detect.Cancel(); return; }
        var serial = CurrentSerial();
        if (serial.Port.Length == 0) { _status.Text = L.T("scm.noPort"); return; }
        Disconnect();
        if (!_stoppedMain) { _state.StopScale(); _stoppedMain = true; }
        _detect = new CancellationTokenSource();
        UpdateButtons();
        try
        {
            var progress = new Progress<int>(b => _status.Text = L.F("scm.trying", b));
            var found = await BaudDetector.DetectAsync(serial.Port, serial.Parity, serial.DataBits, serial.StopBits, CurrentProtocol(), progress, _detect.Token);
            if (found is null)
            {
                _status.Text = L.T("scm.notFound");
                return;
            }
            _baud.SelectedItem = found.Baud.ToString();
            AddRaw(found.Sample);
            _status.Text = L.F("scm.found", found.Baud);
            Connect();
            _status.Text = L.F("scm.found", found.Baud);
        }
        catch (OperationCanceledException)
        {
            _status.Text = L.T("scm.detectStopped");
        }
        finally
        {
            _detect = null;
            UpdateButtons();
        }
    }

    private void Save()
    {
        var serial = CurrentSerial();
        var db = _state.Db;
        if (serial.Port.Length > 0) db.SetSetting("serial.port", serial.Port);
        db.SetSetting("serial.baud", serial.Baud.ToString());
        db.SetSetting("serial.parity", serial.Parity switch { Parity.Even => "E", Parity.Odd => "O", _ => "N" });
        db.SetSetting("serial.data_bits", serial.DataBits.ToString());
        db.SetSetting("serial.stop_bits", serial.StopBits == StopBits.Two ? "2" : "1");
        foreach (var (k, v) in CurrentProtocol().ToSettings()) db.SetSetting(k, v);
        _stoppedMain = true;   // reconnect the program's scale with the new settings when the window closes
    }
}
