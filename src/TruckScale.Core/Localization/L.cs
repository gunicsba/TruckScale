using System.Globalization;

namespace TruckScale.Core.Localization;

/// <summary>
/// UI texts in several languages. <see cref="T"/> returns the text of the current language, falling back to
/// Hungarian (the reference language). To add a language: add a dictionary in Strings.*.cs and list it in <see cref="Languages"/>.
/// The language is chosen at startup (setting "ui.language"); changing it needs a restart.
/// </summary>
public static class L
{
    public sealed record Language(string Code, string Name, string Culture, IReadOnlyDictionary<string, string> Texts);

    public static readonly IReadOnlyList<Language> Languages =
    [
        new("hu", "Magyar", "hu-HU", Strings.Hu),
        new("en", "English", "en-GB", Strings.En),
    ];

    public static Language Current { get; private set; } = Languages[0];

    /// <summary>Culture for numbers and dates (e.g. "26 440" in Hungarian, "26,440" in English).</summary>
    public static CultureInfo Culture => CultureInfo.GetCultureInfo(Current.Culture);

    public static void Set(string? code) =>
        Current = Languages.FirstOrDefault(l => l.Code == code) ?? Languages[0];

    /// <summary>Text for a key; Hungarian if the current language does not have it; the key itself if unknown.</summary>
    public static string T(string key) =>
        Current.Texts.TryGetValue(key, out var s) ? s : Strings.Hu.TryGetValue(key, out var hu) ? hu : key;

    /// <summary>Formatted text, e.g. F("weigh.recorded", plate, weight).</summary>
    public static string F(string key, params object?[] args) => string.Format(Culture, T(key), args);

    public static string Date(DateTime? t) => t?.ToString(T("fmt.date"), Culture) ?? "";
    public static string DateTime(DateTime? t) => t?.ToString(T("fmt.datetime"), Culture) ?? "";
    public static string Time(DateTime? t) => t?.ToString("HH:mm", Culture) ?? "";
    public static string Kg(long? w) => w is long v ? v.ToString("N0", Culture) + " kg" : "";
    public static string Number(long v) => v.ToString("N0", Culture);
}
