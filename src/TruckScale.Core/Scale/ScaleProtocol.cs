using System.Globalization;
using System.Text;

namespace TruckScale.Core.Scale;

public enum ParseMode { Auto, Fixed }

/// <summary>
/// How a line from the scale indicator is read. <see cref="ParseMode.Fixed"/> works like the old program:
/// the "stable" marker and the weight are at fixed character positions (0-based), e.g. for
/// <c>ST,GL,       0kg</c>: stable = index 0, length 2, "ST"; weight = index 6, length 8, 0 decimals.
/// <see cref="ParseMode.Auto"/> recognises the "ST/US/OL,GL,…kg" format by itself.
/// </summary>
public sealed record ScaleProtocol(
    ParseMode Mode = ParseMode.Fixed,
    int StableStart = 0, int StableLength = 2, string StableText = "ST",
    int WeightStart = 6, int WeightLength = 8, int Decimals = 0,
    string TerminatorName = "CRLF")
{
    public static readonly string[] Terminators = ["CRLF", "CR", "LF"];

    public string Terminator => TerminatorName switch { "CR" => "\r", "LF" => "\n", _ => "\r\n" };

    /// <summary>Reads one line (without the terminator). Null if the line does not contain a weight.</summary>
    public ScaleReading? Parse(string? line)
    {
        if (line is null) return null;
        if (Mode == ParseMode.Auto) return ScaleReading.TryParse(line);

        var weightText = Cut(line, WeightStart, WeightLength);
        if (weightText is null) return null;
        weightText = weightText.Replace(" ", "");
        if (!decimal.TryParse(weightText, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
            return null;
        if (Decimals > 0 && !weightText.Contains('.')) value /= (decimal)Math.Pow(10, Decimals);

        var stable = Cut(line, StableStart, StableLength) is { } st && string.Equals(st.Trim(), StableText.Trim(), StringComparison.OrdinalIgnoreCase);
        return new ScaleReading(stable ? ScaleStatus.Stable : ScaleStatus.Unstable, "", (int)Math.Round(value), "kg");
    }

    private static string? Cut(string s, int start, int length) =>
        start < 0 || length <= 0 || start >= s.Length ? null : s.Substring(start, Math.Min(length, s.Length - start));

    // ---------------------------------------------------------------- settings

    public static ScaleProtocol From(IReadOnlyDictionary<string, string?> s)
    {
        int I(string key, int def) => int.TryParse(s.GetValueOrDefault(key), out var v) ? v : def;
        var d = new ScaleProtocol();
        return new ScaleProtocol(
            s.GetValueOrDefault("scale.parse") == "auto" ? ParseMode.Auto : ParseMode.Fixed,
            I("scale.stable.start", d.StableStart), I("scale.stable.length", d.StableLength),
            s.GetValueOrDefault("scale.stable.text") ?? d.StableText,
            I("scale.weight.start", d.WeightStart), I("scale.weight.length", d.WeightLength), I("scale.weight.decimals", d.Decimals),
            Terminators.Contains(s.GetValueOrDefault("scale.terminator")) ? s.GetValueOrDefault("scale.terminator")! : d.TerminatorName);
    }

    public IEnumerable<(string Key, string Value)> ToSettings() =>
    [
        ("scale.parse", Mode == ParseMode.Auto ? "auto" : "fixed"),
        ("scale.stable.start", StableStart.ToString(CultureInfo.InvariantCulture)),
        ("scale.stable.length", StableLength.ToString(CultureInfo.InvariantCulture)),
        ("scale.stable.text", StableText),
        ("scale.weight.start", WeightStart.ToString(CultureInfo.InvariantCulture)),
        ("scale.weight.length", WeightLength.ToString(CultureInfo.InvariantCulture)),
        ("scale.weight.decimals", Decimals.ToString(CultureInfo.InvariantCulture)),
        ("scale.terminator", TerminatorName),
    ];

    // ---------------------------------------------------------------- display helpers (raw data window)

    /// <summary>Makes control characters visible: CR → &lt;CR&gt;, LF → &lt;LF&gt;, others → &lt;1B&gt;.</summary>
    public static string Visible(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
            sb.Append(ch switch
            {
                '\r' => "<CR>",
                '\n' => "<LF>",
                '\t' => "<TAB>",
                < ' ' or (> '~' and < ' ') => $"<{(int)ch:X2}>",
                _ => ch.ToString(),
            });
        return sb.ToString();
    }

    /// <summary>Character position ruler for a line of the given length: "0123456789012…".</summary>
    public static string Ruler(int length) =>
        string.Concat(Enumerable.Range(0, Math.Max(0, length)).Select(i => (char)('0' + i % 10)));

    /// <summary>Marks the stable (S) and weight (W) positions under a line, e.g. "SS    WWWWWWWW".</summary>
    public string Marks(int length)
    {
        var c = new char[Math.Max(length, Math.Max(StableStart + StableLength, WeightStart + WeightLength))];
        Array.Fill(c, ' ');
        for (int i = StableStart; i < StableStart + StableLength && i >= 0 && i < c.Length; i++) c[i] = 'S';
        for (int i = WeightStart; i < WeightStart + WeightLength && i >= 0 && i < c.Length; i++) c[i] = 'W';
        return new string(c).TrimEnd();
    }
}
