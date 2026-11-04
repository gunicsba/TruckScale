using System.IO.Ports;

namespace TruckScale.Core.Scale;

/// <summary>A weight source. <see cref="ReadingChanged"/> fires on a background thread; null means "no connection".</summary>
public interface IScale : IDisposable
{
    event Action<ScaleReading?>? ReadingChanged;
    string Description { get; }
    void Start();
}

public sealed record SerialSettings(string Port, int Baud = 9600, Parity Parity = Parity.None, int DataBits = 8, StopBits StopBits = StopBits.One)
{
    public static SerialSettings From(IReadOnlyDictionary<string, string?> s) => new(
        s.GetValueOrDefault("serial.port") ?? "",
        int.TryParse(s.GetValueOrDefault("serial.baud"), out var b) ? b : 9600,
        (s.GetValueOrDefault("serial.parity") ?? "N").ToUpperInvariant() switch
        {
            "E" => Parity.Even, "O" => Parity.Odd, "M" => Parity.Mark, "S" => Parity.Space, _ => Parity.None,
        },
        int.TryParse(s.GetValueOrDefault("serial.data_bits"), out var d) ? d : 8,
        s.GetValueOrDefault("serial.stop_bits") switch { "2" => StopBits.Two, "1.5" => StopBits.OnePointFive, _ => StopBits.One });
}

/// <summary>Reads <c>ST,GL,   12340kg</c> lines from the indicator; reconnects automatically.</summary>
public sealed class SerialScale(SerialSettings settings) : IScale
{
    private static readonly TimeSpan NoDataTimeout = TimeSpan.FromSeconds(2);
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public event Action<ScaleReading?>? ReadingChanged;
    public string Description => $"{settings.Port} {settings.Baud}";

    public void Start() => _loop ??= Task.Run(() => Run(_cts.Token));

    private async Task Run(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var port = new SerialPort(settings.Port, settings.Baud, settings.Parity, settings.DataBits, settings.StopBits)
                {
                    NewLine = "\n",
                    ReadTimeout = (int)NoDataTimeout.TotalMilliseconds,
                };
                port.Open();
                while (!ct.IsCancellationRequested)
                {
                    string line;
                    try { line = port.ReadLine().TrimEnd('\r'); }
                    catch (TimeoutException) { ReadingChanged?.Invoke(null); continue; }
                    if (ScaleReading.TryParse(line) is { } reading) ReadingChanged?.Invoke(reading);
                }
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                ReadingChanged?.Invoke(null);
                try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { }
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop?.Wait(3000); } catch { /* shutting down */ }
        _cts.Dispose();
    }
}

/// <summary>Test scale: the weight is set from the UI and "settles" (unstable for a moment) after each change.</summary>
public sealed class SimulatedScale : IScale
{
    private readonly Timer _timer;
    private readonly object _lock = new();
    private int _shown, _target;
    private DateTime _settleAt;

    public SimulatedScale() => _timer = new Timer(_ => Tick());

    public event Action<ScaleReading?>? ReadingChanged;
    public string Description => "Szimulátor";

    public int Target
    {
        get { lock (_lock) return _target; }
        set { lock (_lock) { _target = Math.Max(0, value); _settleAt = DateTime.UtcNow.AddSeconds(1.5); } }
    }

    public void Start() => _timer.Change(0, 250);

    private void Tick()
    {
        ScaleReading reading;
        lock (_lock)
        {
            bool settling = DateTime.UtcNow < _settleAt;
            // Move towards the target in steps, with a little noise while settling.
            _shown += (_target - _shown) / 2;
            if (Math.Abs(_target - _shown) < 40) _shown = _target;
            int display = settling ? _shown + Random.Shared.Next(-3, 4) * 20 : _target;
            display = Math.Max(0, display / 20 * 20);
            reading = new ScaleReading(settling ? ScaleStatus.Unstable : ScaleStatus.Stable, "GL", display, "kg");
        }
        ReadingChanged?.Invoke(reading);
    }

    public void Dispose() => _timer.Dispose();
}
