using TruckScale.Core.Localization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using TruckScale.Core;
using TruckScale.Core.Data;

namespace TruckScale.App;

public partial class MainWindow : Window
{
    private readonly AppState _state;
    private readonly WeightDisplay _weight = new(72);

    public MainWindow() : this(null!) { } // XAML designer only

    public MainWindow(AppState state)
    {
        _state = state;
        InitializeComponent();
        if (state is null) return;

        WeightHost.Child = _weight;
        state.ReadingUpdated += () => _weight.Update(state.Reading);

        if (state.Simulator is { } sim)
        {
            SimulatorPanel.IsVisible = true;
            SimSlider.PropertyChanged += (_, e) =>
            {
                if (e.Property == Slider.ValueProperty) sim.Target = (int)(SimSlider.Value / 20) * 20;
            };
            foreach (var kg in new[] { 0, 9_000, 11_300, 18_000, 26_440, 40_000 })
            {
                var b = new Button { Content = AppState.Kg(kg), Margin = new Avalonia.Thickness(0, 0, 4, 4) };
                b.Click += (_, _) => SimSlider.Value = kg;
                SimPresets.Children.Add(b);
            }
        }

        BuildLanguageMenu();
        StatusText.Text = L.F("main.status", state.Db.Path, state.Scale.Description) +
                          (state.ScaleNote is null ? "" : $"  ({state.ScaleNote})");
        Refresh();
        if (state.Db.GetSetting("update.check") != "0") _ = CheckForUpdate();
        if (state.ImportBackup is { } backup)
            Opened += async (_, _) => await Dialog.Info(this, L.T("imp.title"),
                L.F("imp.imported", state.Db.WeighingCount()) + "\n\n" + L.F("imp.backup", backup));
    }

    private void Refresh()
    {
        var open = _state.Db.OpenWeighings();
        YardGrid.ItemsSource = open;
        YardTitle.Text = L.F("main.yard", open.Count);
        var today = DateTime.Today;
        var done = _state.Db.ClosedWeighings(today, today.AddDays(1));
        TodayGrid.ItemsSource = done;
        TodayTitle.Text = L.F("main.today", done.Count(w => w.Status == WeighingStatus.Done));
    }

    private async Task OpenWeighing(WeighingWindow win)
    {
        await win.ShowDialog(this);
        Refresh();
    }

    private async void OnIn(object? sender, RoutedEventArgs e) => await OpenWeighing(new WeighingWindow(_state, Direction.In));
    private async void OnOut(object? sender, RoutedEventArgs e) => await OpenWeighing(new WeighingWindow(_state, Direction.Out));

    private async void OnLeave(object? sender, RoutedEventArgs e)
    {
        if (YardGrid.SelectedItem is not Weighing w)
        {
            if (YardGrid.ItemsSource is List<Weighing> { Count: 1 } only) w = only[0];
            else { await Dialog.Info(this, L.T("main.leave.title"), L.T("main.leave.select")); return; }
        }
        await OpenWeighing(new WeighingWindow(_state, w));
    }

    private void OnYardDoubleTapped(object? sender, TappedEventArgs e) => OnLeave(sender, e);

    private void OnYardKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; OnLeave(sender, e); }
    }

    private async void OnTodayDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (TodayGrid.SelectedItem is Weighing { Status: WeighingStatus.Done } w)
            await new TicketPreviewWindow(_state, w).ShowDialog(this);
    }

    private async void OnLog(object? sender, RoutedEventArgs e) => await new LogWindow(_state).ShowDialog(this);
    private async void OnSummary(object? sender, RoutedEventArgs e) => await new SummaryWindow(_state).ShowDialog(this);

    private async void OnSettings(object? sender, RoutedEventArgs e) => await new SettingsWindow(_state).ShowDialog(this);

    private async void OnData(object? sender, RoutedEventArgs e)
    {
        await new PartnersWindow(_state).ShowDialog(this);
        Refresh();
    }

    private void OnExit(object? sender, RoutedEventArgs e) => Close();

    private async void OnAbout(object? sender, RoutedEventArgs e) =>
        await Dialog.Info(this, L.T("main.menu.about").Replace("_", ""),
            L.T("main.about") + "\n" + L.F("main.version", UpdateCheck.Current().ToString(3)) + "\nhttps://github.com/gunicsba/TruckScale");

    private string _updateUrl = UpdateCheck.ReleasesUrl;

    /// <summary>Startup: is there a newer release on GitHub? Shows a yellow bar if so; silent when offline.</summary>
    private async Task CheckForUpdate()
    {
        var current = UpdateCheck.Current();
        if (await UpdateCheck.NewerAsync(current) is not { } r) return;
        _updateUrl = r.Url;
        UpdateText.Text = L.F("upd.available", r.Version.ToString(3), current.ToString(3));
        UpdateBanner.IsVisible = true;
    }

    private void OnUpdateOpen(object? sender, RoutedEventArgs e)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_updateUrl) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { _ = Dialog.Info(this, L.T("upd.open"), _updateUrl); }
    }

    private void OnUpdateDismiss(object? sender, RoutedEventArgs e) => UpdateBanner.IsVisible = false;

    /// <summary>Nyelv / Language menu: one item per language; choosing one saves it and offers a restart.</summary>
    private void BuildLanguageMenu()
    {
        foreach (var lang in L.Languages)
        {
            var item = new MenuItem
            {
                Header = lang.Name,
                Icon = lang.Code == L.Current.Code ? new TextBlock { Text = "✓" } : null,
            };
            item.Click += async (_, _) =>
            {
                if (lang.Code == L.Current.Code) return;
                _state.Db.SetSetting("ui.language", lang.Code);
                L.Set(lang.Code);   // the question below already in the new language
                if (await Dialog.Confirm(this, L.T("common.restartTitle"), L.T("common.restartNow"))) _state.Restart();
            };
            LanguageMenu.Items.Add(item);
        }
    }
}
