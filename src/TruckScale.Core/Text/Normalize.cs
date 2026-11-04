using System.Globalization;
using System.Text;

namespace TruckScale.Core.Text;

/// <summary>Text helpers used to compare and de-duplicate names and plates.</summary>
public static class Normalize
{
    /// <summary>Trims and collapses inner whitespace. Returns "" for null.</summary>
    public static string Clean(string? s) =>
        string.Join(' ', (s ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Returns null for empty/whitespace input, otherwise <see cref="Clean"/>.</summary>
    public static string? CleanOrNull(string? s)
    {
        var c = Clean(s);
        return c.Length == 0 ? null : c;
    }

    /// <summary>
    /// Comparison key for registration plates: upper-case letters and digits only,
    /// with the letter O treated as zero. "DEF-456/UVW-694" and "DEF456 UVW694" give the same key.
    /// </summary>
    public static string PlateKey(string? plate)
    {
        var sb = new StringBuilder();
        foreach (var ch in (plate ?? "").ToUpperInvariant())
        {
            if (ch is >= 'A' and <= 'Z' or >= '0' and <= '9')
                sb.Append(ch == 'O' ? '0' : ch);
        }
        return sb.ToString();
    }

    /// <summary>Comparison key for names: lower case, accents removed, whitespace collapsed.</summary>
    public static string NameKey(string? name)
    {
        var decomposed = Clean(name).ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                sb.Append(ch);
        }
        return sb.ToString();
    }
}
