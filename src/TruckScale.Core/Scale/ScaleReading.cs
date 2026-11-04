using System.Globalization;
using System.Text.RegularExpressions;

namespace TruckScale.Core.Scale;

public enum ScaleStatus { Stable, Unstable, Overload }

/// <summary>
/// One line from the scale indicator, e.g. <c>ST,GL,       0kg</c>
/// (status, weight type, value, unit; terminated by CR LF).
/// </summary>
public sealed partial record ScaleReading(ScaleStatus Status, string Mode, int Weight, string Unit)
{
    public bool IsStable => Status == ScaleStatus.Stable;

    [GeneratedRegex(@"^\s*(?<st>ST|US|OL)\s*,\s*(?<mode>[A-Z]{2})\s*,\s*(?<val>[+-]?\s*[0-9]+(?:\.[0-9]+)?)?\s*(?<unit>[a-zA-Z]*)\s*$")]
    private static partial Regex LineRegex();

    /// <summary>Parses one line (without CR LF). Returns null if it is not a valid reading.</summary>
    public static ScaleReading? TryParse(string? line)
    {
        if (line is null) return null;
        var m = LineRegex().Match(line);
        if (!m.Success) return null;

        var status = m.Groups["st"].Value switch
        {
            "ST" => ScaleStatus.Stable,
            "US" => ScaleStatus.Unstable,
            _ => ScaleStatus.Overload,
        };

        var val = m.Groups["val"].Value.Replace(" ", "");
        int weight = 0;
        if (val.Length > 0)
        {
            if (!decimal.TryParse(val, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture, out var d))
                return null;
            weight = (int)Math.Round(d);
        }
        else if (status != ScaleStatus.Overload)
        {
            return null;
        }

        var unit = m.Groups["unit"].Value.ToLowerInvariant();
        return new ScaleReading(status, m.Groups["mode"].Value, weight, unit.Length == 0 ? "kg" : unit);
    }
}
