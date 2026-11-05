using System.Diagnostics;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Microsoft.Data.Sqlite;
using TruckScale.Core.Data;
using TruckScale.Core.Legacy;
using TruckScale.Core.Localization;

namespace TruckScale.App;

/// <summary>
/// "Átvétel a régi programból": settings from TS.ini and/or all data from DATA.LIS. The data is converted
/// by the 32-bit TruckScale.Import program (the old Jet database driver only exists as 32-bit) into
/// truckscale.db.import, which replaces the database at the restart (the old one is kept as a backup).
/// </summary>
public sealed class LegacyImportWindow : Window
{
    private readonly AppState _state;
    private readonly LegacyLocator _locator = new();
    private readonly CheckBox _useIni = new() { Content = L.T("imp.settings"), IsChecked = true };
    private readonly CheckBox _useLis = new() { Content = L.T("imp.data"), IsChecked = true };
    private readonly ComboBox _ini = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _lis = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _log = new()
    {
        IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, Height = 220,
        FontFamily = new FontFamily("Consolas, Menlo, DejaVu Sans Mono, monospace"), FontSize = 12,
    };
    private readonly Button _start = new() { Content = L.T("imp.start"), MinWidth = 110, IsDefault = true };
    private readonly Button _close = new() { Content = L.T("imp.close"), MinWidth = 110, IsCancel = true };
    private readonly Button _searchFolder = new() { Content = L.T("imp.searchFolder") };
    private readonly List<LegacyFile> _inis = [], _lises = [];
    private bool _busy;

    public LegacyImportWindow(AppState state)
    {
        _state = state;
        Title = L.T("imp.title");
        Width = 820;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        if (!OperatingSystem.IsWindows()) { _useLis.IsChecked = false; _useLis.IsEnabled = false; }

        var browseIni = new Button { Content = L.T("set.browse") };
        browseIni.Click += async (_, _) => { if (await Pick(L.T("imp.pickIni"), "TS.ini", "*.ini") is { } p) { Add(_inis, _ini, p); OnIniChanged(); } };
        var browseLis = new Button { Content = L.T("set.browse") };
        browseLis.Click += async (_, _) => { if (await Pick(L.T("imp.pickLis"), "DATA.LIS", "*.LIS", "*.mdb") is { } p) Add(_lises, _lis, p); };
        _searchFolder.Click += async (_, _) =>
        {
            var dirs = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = L.T("imp.pickFolder") });
            if (dirs.FirstOrDefault()?.TryGetLocalPath() is { } dir) await Search([dir]);
        };
        _ini.SelectionChanged += (_, _) => OnIniChanged();
        _useIni.IsCheckedChanged += (_, _) => _ini.IsEnabled = browseIni.IsEnabled = _useIni.IsChecked == true;
        _useLis.IsCheckedChanged += (_, _) => _lis.IsEnabled = browseLis.IsEnabled = _useLis.IsChecked == true;
        _start.Click += async (_, _) => await Run();
        _close.Click += (_, _) => Close();
        Closing += (_, e) => { if (_busy) e.Cancel = true; };

        var warn = new TextBlock
        {
            Text = OperatingSystem.IsWindows() ? L.F("imp.replaceWarn", state.Db.WeighingCount()) : L.T("imp.windowsOnly"),
            Classes = { "hint" }, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(28, 0, 0, 0),
        };

        Content = new StackPanel
        {
            Margin = new Thickness(16), Spacing = 10,
            Children =
            {
                new TextBlock { Text = L.T("imp.intro"), TextWrapping = TextWrapping.Wrap },
                _useIni,
                FileRow(L.T("imp.ini"), _ini, browseIni),
                _useLis,
                FileRow(L.T("imp.lis"), _lis, browseLis),
                warn,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { _searchFolder, _status } },
                _log,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { _close, _start } },
            },
        };
        Opened += async (_, _) => await Search([]);
    }

    private static Control FileRow(string label, ComboBox combo, Button browse)
    {
        var g = new Grid { ColumnDefinitions = new("90,*,Auto"), Margin = new Thickness(28, 0, 0, 0) };
        var l = new TextBlock { Text = label, Classes = { "label" } };
        browse.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetColumn(combo, 1);
        Grid.SetColumn(browse, 2);
        g.Children.Add(l);
        g.Children.Add(combo);
        g.Children.Add(browse);
        return g;
    }

    private static string Show(LegacyFile f) => $"{f.Path}    ({L.DateTime(f.Modified)}, {f.Size / 1024:N0} KB)";

    private static void Add(List<LegacyFile> list, ComboBox combo, string path)
    {
        var i = list.FindIndex(f => string.Equals(f.Path, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
        if (i < 0) { list.Insert(0, LegacyFile.Of(path)); i = 0; }
        combo.ItemsSource = list.Select(Show).ToList();
        combo.SelectedIndex = i;
    }

    private static LegacyFile? Selected(List<LegacyFile> list, ComboBox combo) =>
        combo.SelectedIndex >= 0 && combo.SelectedIndex < list.Count ? list[combo.SelectedIndex] : null;

    /// <summary>A newly chosen ini: select the database it belongs to.</summary>
    private void OnIniChanged()
    {
        if (Selected(_inis, _ini) is not { } ini || !OperatingSystem.IsWindows()) return;
        try { if (_locator.DatabaseOf(ini.Path) is { } db) Add(_lises, _lis, db); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private async Task<string?> Pick(string title, string name, params string[] patterns)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            FileTypeFilter = [new FilePickerFileType(name) { Patterns = patterns }, FilePickerFileTypes.All],
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    private async Task Search(string[] extraDirs)
    {
        _status.Text = L.T("imp.searching");
        _searchFolder.IsEnabled = false;
        var (inis, lises) = await Task.Run(() => _locator.Find(extraDirs));
        _searchFolder.IsEnabled = true;
        foreach (var f in inis) if (!_inis.Any(x => x.Path.Equals(f.Path, StringComparison.OrdinalIgnoreCase))) _inis.Add(f);
        foreach (var f in lises) if (!_lises.Any(x => x.Path.Equals(f.Path, StringComparison.OrdinalIgnoreCase))) _lises.Add(f);
        _lis.ItemsSource = _lises.Select(Show).ToList();
        if (_lises.Count > 0 && _lis.SelectedIndex < 0) _lis.SelectedIndex = 0;
        _ini.ItemsSource = _inis.Select(Show).ToList();
        if (_inis.Count > 0) _ini.SelectedIndex = extraDirs.Length > 0 && inis.Count > 0 ? _inis.FindIndex(x => x.Path == inis[0].Path) : 0;
        _status.Text = _inis.Count + _lises.Count == 0 ? L.T("imp.noneFound") : L.F("imp.found", _inis.Count, _lises.Count);
    }

    private void Log(string line)
    {
        _log.Text += line + "\n";
        _log.CaretIndex = _log.Text.Length;
    }

    private async Task Run()
    {
        var ini = _useIni.IsChecked == true ? Selected(_inis, _ini) : null;
        var lis = _useLis.IsChecked == true ? Selected(_lises, _lis) : null;
        if (ini is null && lis is null) { await Dialog.Info(this, Title!, L.T("imp.nothing")); return; }

        _busy = true;
        _start.IsEnabled = _close.IsEnabled = false;
        _log.Text = "";
        try
        {
            Dictionary<string, string>? fromIni = null;
            if (ini is not null)
            {
                fromIni = LegacyIni.ToSettings(LegacyIni.Read(ini.Path), ini.Path, OperatingSystem.IsWindows());
                Log($"TS.ini: {ini.Path}");
            }

            if (lis is null)
            {
                Apply(_state.Db, fromIni!);
                _state.RestartScale();
                TicketService.WarmUp(_state.Db.GetSetting("printer.name"));
                await Dialog.Info(this, Title!, L.T("imp.doneSettings"));
                return;
            }

            var target = _state.Db.Path + LegacyImport.PendingSuffix;
            if (!await Convert(lis.Path, target)) return;

            using (var imported = TruckScaleDb.OpenOrCreate(target))
            {
                if (fromIni is not null) Apply(imported, fromIni);
                LegacyImport.CarryOver(_state.Db.AllSettings(), imported);
            }
            SqliteConnection.ClearAllPools();   // release truckscale.db.import before the restart moves it
            await Dialog.Info(this, Title!, L.T("imp.doneData"));
            _busy = false;
            _state.Restart();
        }
        catch (Exception ex)
        {
            Log(L.F("scm.error", ex.Message));
        }
        finally
        {
            _busy = false;
            _start.IsEnabled = _close.IsEnabled = true;
        }
    }

    private void Apply(TruckScaleDb db, Dictionary<string, string> fromIni)
    {
        var written = LegacyImport.ApplyIni(db, fromIni);
        Log(L.F("imp.settingsDone", written.Count));
        var form = db.GetSetting("ticket.form");
        if (fromIni.ContainsKey("ticket.form"))
            Log(Path.IsPathRooted(form) ? L.F("imp.formMissing", form) :
                File.Exists(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(db.Path))!, form ?? "")) ? L.F("imp.formCopied", form) : L.F("imp.formMissing", form));
    }

    /// <summary>Runs the 32-bit converter: DATA.LIS → a new SQLite file. False if it failed or was rejected.</summary>
    private async Task<bool> Convert(string lis, string target)
    {
        var exe = FindImporter();
        if (exe is null) { Log(L.F("imp.noImporter", Path.Combine(AppContext.BaseDirectory, "import", "TruckScale.Import.exe"))); return false; }
        Log(L.F("imp.running", lis));
        if (File.Exists(target)) File.Delete(target);

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            ArgumentList = { lis, target, "--overwrite" },
        };
        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => { if (e.Data is { } d) Dispatcher.UIThread.Post(() => Log(d)); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is { } d) Dispatcher.UIThread.Post(() => Log(d)); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        await p.WaitForExitAsync();
        await Task.Delay(100);   // let the last lines arrive

        bool ok = p.ExitCode == 0 || (p.ExitCode == 1 && File.Exists(target) && await Dialog.Confirm(this, Title!, L.T("imp.mismatch")));
        if (!ok)
        {
            Log(L.F("imp.failed", p.ExitCode));
            SqliteConnection.ClearAllPools();
            try { File.Delete(target); } catch (IOException) { }
        }
        return ok;
    }

    /// <summary>import\TruckScale.Import.exe next to the program (release), or the build output (development).</summary>
    private static string? FindImporter()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "import", "TruckScale.Import.exe");
        if (File.Exists(bundled)) return bundled;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var tool = Path.Combine(dir.FullName, "tools", "TruckScale.Import", "bin");
            if (!Directory.Exists(tool)) continue;
            return Directory.EnumerateFiles(tool, "TruckScale.Import.exe", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTime).FirstOrDefault();
        }
        return null;
    }
}
