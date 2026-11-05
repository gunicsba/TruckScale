using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TruckScale.Core.Data;
using TruckScale.Core.Localization;

namespace TruckScale.Core;

/// <summary>
/// CSV for spreadsheets. Semicolon separated, UTF-8 with BOM: Excel opens it directly with the right
/// accents and columns in Hungarian settings. Weights are plain integers, so they can be summed.
/// </summary>
public static class Csv
{
    public const char Separator = ';';
    public static readonly Encoding Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    public static string Field(object? value)
    {
        var s = value switch
        {
            null => "",
            DateTime d => d.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "",
        };
        return s.IndexOfAny([Separator, '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    public static string Build(IEnumerable<string> headers, IEnumerable<IEnumerable<object?>> rows)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(Separator, headers.Select(Field))).Append("\r\n");
        foreach (var r in rows) sb.Append(string.Join(Separator, r.Select(Field))).Append("\r\n");
        return sb.ToString();
    }

    /// <summary>The summary table as shown (with subtotal rows marked).</summary>
    public static string Summary(IEnumerable<SummaryRow> rows, string level1Header, string? level2Header)
    {
        var headers = new List<string> { L.T("f.direction"), level1Header };
        if (level2Header is not null) headers.Add(level2Header);
        headers.AddRange([L.T("f.count"), L.T("f.grossKg"), L.T("f.tareKg"), L.T("f.netKg")]);
        return Build(headers, rows.Select(r =>
        {
            var cells = new List<object?> { r.DirectionText, r.Level1 };
            if (level2Header is not null) cells.Add(r.Level2);
            cells.AddRange([r.Count, r.Gross, r.Tare, r.Net]);
            return (IEnumerable<object?>)cells;
        }));
    }

    /// <summary>One line per weighing.</summary>
    public static string Weighings(IEnumerable<Weighing> rows) => Build(
        [L.T("f.ticket"), L.T("f.status"), L.T("f.direction"), L.T("f.arrived"), L.T("f.closed"), L.T("f.plate"), L.T("f.customer"),
         L.T("f.driver"), L.T("f.material"), L.T("f.marking"), L.T("f.note"), L.T("f.grossKg"), L.T("f.tareKg"), L.T("f.netKg"), L.T("csv.storedTare")],
        rows.Select(w => (IEnumerable<object?>)new object?[]
        {
            w.TicketNo, w.StatusText, w.DirectionText, w.FirstAt, w.ClosedAt, w.Plate, w.PartnerName,
            w.DriverName, w.MaterialName, w.Marking, w.Note, w.Gross, w.Tare, w.Net, w.StoredTare ? L.T("common.yes") : "",
        }));
}

/// <summary>
/// Settings saved to / loaded from a file (to move them to another computer or keep a copy).
/// The ticket form file is included, so the ticket looks the same after loading.
/// </summary>
public static class SettingsFile
{
    /// <summary>Not settings but state of the data: never saved or loaded.</summary>
    public static bool IsState(string key) => key == "ticket.next_no" || key.StartsWith("import.");

    public static string Save(TruckScaleDb db)
    {
        var settings = new JsonObject();
        foreach (var (k, v) in db.AllSettings().OrderBy(kv => kv.Key, StringComparer.Ordinal))
            if (v is not null && !IsState(k)) settings[k] = v;
        var root = new JsonObject
        {
            ["app"] = "TruckScale",
            ["kind"] = "settings",
            ["version"] = 1,
            ["saved"] = DateTime.Now.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
            ["settings"] = settings,
        };
        if (FormPath(db) is { } form)
            root["form"] = new JsonObject { ["name"] = Path.GetFileName(form), ["base64"] = Convert.ToBase64String(File.ReadAllBytes(form)) };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    /// <summary>
    /// Loads saved settings into the database (keys not in the file stay as they are). The included ticket
    /// form is written next to the database. Returns the number of settings loaded.
    /// </summary>
    public static int Load(TruckScaleDb db, string json)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (JsonException) { root = null; }
        if (root?["app"]?.GetValue<string>() != "TruckScale" || root["settings"] is not JsonObject settings)
            throw new InvalidDataException(L.T("set.file.invalid"));

        int n = 0;
        foreach (var (k, v) in settings)
        {
            if (IsState(k) || v is null) continue;
            db.SetSetting(k, v.GetValue<string>());
            n++;
        }
        if (root["form"] is JsonObject f && f["name"]?.GetValue<string>() is { Length: > 0 } name && f["base64"]?.GetValue<string>() is { } b64)
            db.SetSetting("ticket.form", Legacy.LegacyImport.StoreForm(
                Convert.FromBase64String(b64), name, Path.GetDirectoryName(Path.GetFullPath(db.Path))!));
        return n;
    }

    /// <summary>The ticket form file in use, if it is a file (not the built-in layout).</summary>
    private static string? FormPath(TruckScaleDb db)
    {
        var name = db.GetSetting("ticket.form");
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (Path.IsPathRooted(name)) return File.Exists(name) ? name : null;
        var dbDir = Path.GetDirectoryName(Path.GetFullPath(db.Path))!;
        return new[] { Path.Combine(dbDir, name), Path.Combine(AppContext.BaseDirectory, name), Path.Combine(AppContext.BaseDirectory, "forms", name) }
            .FirstOrDefault(File.Exists);
    }
}
