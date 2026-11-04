using System.Globalization;
using System.IO.Ports;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using TruckScale.Core.Data;
using TruckScale.Core.Scale;

namespace TruckScale.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var state = AppState.Create(desktop.Args ?? []);
            desktop.MainWindow = new MainWindow(state);
            desktop.ShutdownRequested += (_, _) => state.Dispose();
        }
        base.OnFrameworkInitializationCompleted();
    }
}

/// <summary>Everything the windows share: the database, the scale and the latest reading.</summary>
public sealed class AppState : IDisposable
{
    public static readonly CultureInfo Hu = CultureInfo.GetCultureInfo("hu-HU");

    public required TruckScaleDb Db { get; init; }
    public required IScale Scale { get; init; }
    public SimulatedScale? Simulator => Scale as SimulatedScale;
    public string? ScaleNote { get; init; }

    /// <summary>Latest reading (null = no connection). Only read/written on the UI thread.</summary>
    public ScaleReading? Reading { get; private set; }

    /// <summary>Raised on the UI thread for every reading.</summary>
    public event Action? ReadingUpdated;

    public static AppState Create(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = Hu;
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = Hu;

        string? Arg(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        var dbPath = Arg("--db") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TruckScale", "truckscale.db");
        var db = TruckScaleDb.OpenOrCreate(dbPath);

        var settings = db.AllSettings();
        var serial = SerialSettings.From(settings);
        IScale scale;
        string? note = null;
        if (args.Contains("--sim") || settings.GetValueOrDefault("scale.simulator") == "1")
        {
            scale = new SimulatedScale();
        }
        else if (serial.Port.Length > 0 && SerialPort.GetPortNames().Contains(serial.Port, StringComparer.OrdinalIgnoreCase))
        {
            scale = new SerialScale(serial);
        }
        else
        {
            scale = new SimulatedScale();
            note = serial.Port.Length > 0 ? $"{serial.Port} nem elérhető – szimulátor" : "Nincs soros port beállítva – szimulátor";
        }

        var state = new AppState { Db = db, Scale = scale, ScaleNote = note };
        scale.ReadingChanged += r => Dispatcher.UIThread.Post(() =>
        {
            state.Reading = r;
            state.ReadingUpdated?.Invoke();
        });
        scale.Start();
        TicketService.WarmUp(db.GetSetting("printer.name"));
        return state;
    }

    public void Dispose()
    {
        Scale.Dispose();
        Db.Dispose();
    }

    public static string Kg(int? w) => w is int v ? v.ToString("N0", Hu) + " kg" : "";
    public static string When(DateTime? t) => t?.ToString("yyyy.MM.dd. HH:mm", Hu) ?? "";
}
