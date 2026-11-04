using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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

        StatusText.Text = $"Adatbázis: {state.Db.Path}    Mérleg: {state.Scale.Description}" +
                          (state.ScaleNote is null ? "" : $"  ({state.ScaleNote})");
        Refresh();
    }

    private void Refresh()
    {
        var open = _state.Db.OpenWeighings();
        YardGrid.ItemsSource = open;
        YardTab.Header = $"Udvaron ({open.Count})";
        var today = DateTime.Today;
        var done = _state.Db.ClosedWeighings(today, today.AddDays(1));
        TodayGrid.ItemsSource = done;
        TodayTab.Header = $"Mai mérések ({done.Count(w => w.Status == WeighingStatus.Done)})";
        LeaveButton.IsEnabled = open.Count > 0;
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
            else { await Dialog.Info(this, "Távozás", "Válassza ki a távozó járművet a listában."); return; }
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

    private async void OnSettings(object? sender, RoutedEventArgs e) => await new SettingsWindow(_state).ShowDialog(this);

    private async void OnData(object? sender, RoutedEventArgs e)
    {
        await new PartnersWindow(_state).ShowDialog(this);
        Refresh();
    }

    private void OnExit(object? sender, RoutedEventArgs e) => Close();

    private async void OnAbout(object? sender, RoutedEventArgs e) =>
        await Dialog.Info(this, "Névjegy", "Truck Scale – hídmérleg program\nhttps://github.com/gunicsba/TruckScale");
}
