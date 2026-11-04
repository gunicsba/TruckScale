using TruckScale.Core.Localization;
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

/// <summary>
/// Reads lines from the scale indicator over a serial port and parses them with a <see cref="ScaleProtocol"/>;
/// reconnects automatically. <see cref="RawReceived"/> passes on everything exactly as received (for the raw data window).
/// </summary>
public sealed class SerialScale(SerialSettings settings, ScaleProtocol? protocol = null) : IScale
{
    private static readonly TimeSpan NoDataTimeout = TimeSpan.FromSeconds(2);
    private readonly ScaleProtocol _protocol = protocol ?? new ScaleProtocol();
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public event Action<ScaleReading?>? ReadingChanged;
    /// <summary>Raw text as received (may contain partial lines and control characters).</summary>
    public event Action<string>? RawReceived;
    /// <summary>A complete line (without the terminator).</summary>
    public event Action<string>? LineReceived;
    /// <summary>Connection problem (e.g. the port is in use by another program).</summary>
    public event Action<string>? Error;

    public string Description => $"{settings.Port} {settings.Baud}";

    public void Start() => _loop ??= Task.Run(() => Run(_cts.Token));

    private async Task Run(CancellationToken ct)
    {
        var term = _protocol.Terminator;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var port = new SerialPort(settings.Port, settings.Baud, settings.Parity, settings.DataBits, settings.StopBits)
                {
                    ReadTimeout = 300,
                    Encoding = System.Text.Encoding.Latin1,
                };
                port.Open();
                var buffer = new byte[1024];
                var pending = new System.Text.StringBuilder();
                var lastData = DateTime.UtcNow;
                while (!ct.IsCancellationRequested)
                {
                    int n;
                    try { n = port.Read(buffer, 0, buffer.Length); }
                    catch (TimeoutException)
                    {
                        if (DateTime.UtcNow - lastData > NoDataTimeout) { ReadingChanged?.Invoke(null); lastData = DateTime.UtcNow; }
                        continue;
                    }
                    if (n <= 0) continue;
                    lastData = DateTime.UtcNow;
                    var text = System.Text.Encoding.Latin1.GetString(buffer, 0, n);
                    RawReceived?.Invoke(text);
                    pending.Append(text);
                    var all = pending.ToString();
                    int idx;
                    while ((idx = all.IndexOf(term, StringComparison.Ordinal)) >= 0)
                    {
                        var line = all[..idx];
                        all = all[(idx + term.Length)..];
                        LineReceived?.Invoke(line);
                        if (_protocol.Parse(line) is { } reading) ReadingChanged?.Invoke(reading);
                    }
                    pending.Clear().Append(all.Length > 4096 ? "" : all);   // no terminator for a long time: wrong setting
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Error?.Invoke(ex.Message);
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

/// <summary>
/// Automatic baud rate detection: listens at each speed for a moment and picks the one where the data
/// looks like scale lines (lines that the protocol can read score highest, readable text scores a little).
/// </summary>
public static class BaudDetector
{
    public static readonly int[] Bauds = [9600, 4800, 2400, 19200, 1200, 38400, 57600, 115200];

    public sealed record Result(int Baud, int Score, string Sample);

    public static async Task<Result?> DetectAsync(string portName, Parity parity, int dataBits, StopBits stopBits, ScaleProtocol protocol,
        IProgress<int>? progress = null, CancellationToken ct = default, int listenMs = 1500)
    {
        Result? best = null;
        foreach (var baud in Bauds)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(baud);
            var text = await Task.Run(() => Listen(portName, baud, parity, dataBits, stopBits, listenMs, ct), ct);
            var score = Score(text, protocol);
            if (score > (best?.Score ?? 0)) best = new Result(baud, score, text);
            if (score >= 30) break;   // several good lines: no need to try further
        }
        return best;
    }

    /// <summary>Score of the received text: 10 per line the protocol reads, 1 if it is at least readable text.</summary>
    public static int Score(string text, ScaleProtocol protocol)
    {
        if (text.Length == 0) return 0;
        var lines = text.Split(protocol.Terminator).SkipLast(1).ToList();
        int good = lines.Count(l => protocol.Parse(l) is not null || ScaleReading.TryParse(l) is not null);
        if (good > 0) return good * 10;
        double printable = text.Count(c => c is >= ' ' and <= '~' or '\r' or '\n') / (double)text.Length;
        return printable > 0.9 && text.Length > 10 ? 1 : 0;
    }

    private static string Listen(string portName, int baud, Parity parity, int dataBits, StopBits stopBits, int ms, CancellationToken ct)
    {
        try
        {
            using var port = new SerialPort(portName, baud, parity, dataBits, stopBits) { ReadTimeout = 100, Encoding = System.Text.Encoding.Latin1 };
            port.Open();
            port.DiscardInBuffer();
            var sb = new System.Text.StringBuilder();
            var until = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < until && !ct.IsCancellationRequested)
            {
                try { sb.Append(port.ReadExisting()); } catch (TimeoutException) { }
                Thread.Sleep(50);
            }
            return sb.ToString();
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return "";
        }
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
    public string Description => L.T("scale.simulator");

    public int Target
    {
        get { lock (_lock) return _target; }
        set { lock (_lock) { _target = Math.Max(0, value); _settleAt = DateTime.UtcNow.AddSeconds(1.5); } }
    }

    public bool IsRunning { get; private set; }

    public void Start() { IsRunning = true; _timer.Change(0, 250); }

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
