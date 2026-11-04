using TruckScale.Core.Localization;
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
    /// <summary>Culture of the current language (numbers, dates, sorting).</summary>
    public static CultureInfo Hu => L.Culture;

    /// <summary>Command-line arguments (to restart the program with the same arguments).</summary>
    public string[] Args { get; init; } = [];

    public required TruckScaleDb Db { get; init; }
    public required IScale Scale { get; set; }
    public SimulatedScale? Simulator => Scale as SimulatedScale;
    public string? ScaleNote { get; init; }

    /// <summary>Latest reading (null = no connection). Only read/written on the UI thread.</summary>
    public ScaleReading? Reading { get; private set; }

    /// <summary>Raised on the UI thread for every reading.</summary>
    public event Action? ReadingUpdated;

    public static AppState Create(string[] args)
    {
        string? Arg(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        var dbPath = Arg("--db") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TruckScale", "truckscale.db");
        var db = TruckScaleDb.OpenOrCreate(dbPath);

        // Language (setting "ui.language", or --lang en); numbers and dates follow it.
        L.Set(Arg("--lang") ?? db.GetSetting("ui.language"));
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = L.Culture;
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = L.Culture;

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
            scale = new SerialScale(serial, ScaleProtocol.From(settings));
        }
        else
        {
            scale = new SimulatedScale();
            note = serial.Port.Length > 0 ? L.F("scale.portMissing", serial.Port) : L.T("scale.noPort");
        }

        var state = new AppState { Db = db, Scale = scale, ScaleNote = note, Args = args };
        state.Attach(scale);
        TicketService.WarmUp(db.GetSetting("printer.name"));
        return state;
    }

    private void Attach(IScale scale)
    {
        Scale = scale;
        scale.ReadingChanged += r => Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(Scale, scale)) return;   // an old, already replaced scale
            Reading = r;
            ReadingUpdated?.Invoke();
        });
        scale.Start();
    }

    /// <summary>
    /// Releases the serial port (the raw data window opens it itself). The simulator keeps running.
    /// Call <see cref="RestartScale"/> afterwards.
    /// </summary>
    public void StopScale()
    {
        if (Scale is not SerialScale) return;
        Scale.Dispose();
        Scale = new SimulatedScale();   // placeholder, not started: no readings
        Reading = null;
        ReadingUpdated?.Invoke();
    }

    /// <summary>Re-reads the scale settings (port, speed, protocol) and reconnects – no program restart needed.</summary>
    public void RestartScale()
    {
        if (Simulator is not null && Scale is SimulatedScale { } sim && sim.IsRunning) return;   // simulator mode stays
        var settings = Db.AllSettings();
        var serial = SerialSettings.From(settings);
        Scale.Dispose();
        if (serial.Port.Length > 0 && SerialPort.GetPortNames().Contains(serial.Port, StringComparer.OrdinalIgnoreCase))
            Attach(new SerialScale(serial, ScaleProtocol.From(settings)));
        else
        {
            Scale = new SimulatedScale();
            Reading = null;
            ReadingUpdated?.Invoke();
        }
    }

    public void Dispose()
    {
        Scale.Dispose();
        Db.Dispose();
    }

    public static string Kg(int? w) => L.Kg(w);
    public static string When(DateTime? t) => L.DateTime(t);

    /// <summary>Starts the program again with the same arguments and closes this instance.</summary>
    public void Restart()
    {
        if (Environment.ProcessPath is { } exe)
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false, ArgumentList = { } }
                .WithArgs(Args));
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d) d.Shutdown();
    }
}

internal static class ProcessStartInfoExtensions
{
    public static System.Diagnostics.ProcessStartInfo WithArgs(this System.Diagnostics.ProcessStartInfo psi, IEnumerable<string> args)
    {
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }
}

/// <summary>XAML: Text="{l:T main.in}" – a UI text in the current language.</summary>
public sealed class TExtension(string key) : Avalonia.Markup.Xaml.MarkupExtension
{
    public string Key { get; set; } = key;
    public override object ProvideValue(IServiceProvider serviceProvider) => L.T(Key);
}
