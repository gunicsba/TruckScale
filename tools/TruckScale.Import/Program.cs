// TruckScale.Import – one-time migration from the old TS.exe database (DATA.LIS, Access/Jet 4.0)
// into the new SQLite database. Windows only (needs the 32-bit Jet OLE DB driver), see docs/DATABASE.md.
//
// Usage: TruckScale.Import <DATA.LIS> <truckscale.db> [--ini TS.ini] [--overwrite]

using System.Data.OleDb;
using System.Text;
using Microsoft.Data.Sqlite;
using TruckScale.Core.Data;
using TruckScale.Core.Text;

Console.OutputEncoding = Encoding.UTF8;
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

var positional = args.Where(a => !a.StartsWith("--")).ToList();
string? iniPath = null;
for (int i = 0; i < args.Length - 1; i++) if (args[i] == "--ini") iniPath = args[i + 1];
positional.Remove(iniPath ?? "\0");
bool overwrite = args.Contains("--overwrite");

if (positional.Count != 2)
{
    Console.Error.WriteLine("Usage: TruckScale.Import <DATA.LIS> <truckscale.db> [--ini TS.ini] [--overwrite]");
    return 2;
}
string srcPath = Path.GetFullPath(positional[0]);
string dstPath = Path.GetFullPath(positional[1]);

if (!OperatingSystem.IsWindows() || Environment.Is64BitProcess)
{
    Console.Error.WriteLine("The importer must run as a 32-bit Windows process (Jet 4.0 driver).");
    return 2;
}
if (!File.Exists(srcPath)) { Console.Error.WriteLine($"Not found: {srcPath}"); return 2; }
if (File.Exists(dstPath))
{
    if (!overwrite) { Console.Error.WriteLine($"Already exists (use --overwrite): {dstPath}"); return 2; }
    File.Delete(dstPath);
}

// ---------------------------------------------------------------- read old database
// Work on a temporary copy so the original file is never touched or locked.
string tmpCopy = Path.Combine(Path.GetTempPath(), $"ts-import-{Guid.NewGuid():N}.mdb");
File.Copy(srcPath, tmpCopy);
Dictionary<string, List<Row>> old;
try
{
    using var jet = new OleDbConnection($"Provider=Microsoft.Jet.OLEDB.4.0;Data Source={tmpCopy};Mode=Read");
    jet.Open();
    old = new[] { "ORGANIZATIONS", "CARS", "DRIVERS", "ARTICLE", "W_RECEIPTS", "TM_CARS" }
        .ToDictionary(t => t, t => ReadTable(jet, t));
}
finally
{
    OleDbConnection.ReleaseObjectPool();
    try { File.Delete(tmpCopy); } catch { /* best effort */ }
}

var receipts = old["W_RECEIPTS"];
var inYard = old["TM_CARS"];
Console.WriteLine($"Source: {srcPath}");
foreach (var (t, rows) in old) Console.WriteLine($"  {t,-14} {rows.Count,5} rows");

// Usage counts (weighings + trucks in the yard) for each old id.
Dictionary<int, int> Usage(string col, string tmCol)
{
    var d = new Dictionary<int, int>();
    foreach (var r in receipts) if (r.Int(col) is int id) d[id] = d.GetValueOrDefault(id) + 1;
    foreach (var r in inYard) if (r.Int(tmCol) is int id) d[id] = d.GetValueOrDefault(id) + 1;
    return d;
}
var orgUse = Usage("ORG_ID", "ORG_ID");
var carUse = Usage("CAR_ID", "CAR_ID");
var drvUse = Usage("DRV_ID", "DRV_ID");
var artUse = Usage("ART_ID", "ART_ID");

// Latest weighing per old CAR_ID (for display spelling and last tare).
var lastByCar = receipts
    .Where(r => r.Int("CAR_ID") is not null)
    .GroupBy(r => r.Int("CAR_ID")!.Value)
    .ToDictionary(g => g.Key, g => g.OrderBy(r => ClosedAt(r)).ThenBy(r => r.Int("ID")).Last());

// ---------------------------------------------------------------- merge master data
// Keep a row if it is not deleted or a weighing refers to it; then group by a normalised key.
static List<IGrouping<string, Row>> Merge(IEnumerable<Row> rows, Dictionary<int, int> use,
    Func<Row, string> key, Func<Row, bool>? skip = null) =>
    rows.Where(r => (!r.Bool("DEL_MARK") || use.ContainsKey(r.Id)) && !(skip?.Invoke(r) ?? false))
        .GroupBy(key)
        .ToList();

// Representative of a group: not deleted first, then most used, then newest.
static Row Pick(IEnumerable<Row> g, Dictionary<int, int> use, Func<Row, int>? extra = null) =>
    g.OrderByDescending(r => !r.Bool("DEL_MARK"))
     .ThenByDescending(r => extra?.Invoke(r) ?? 0)
     .ThenByDescending(r => use.GetValueOrDefault(r.Id))
     .ThenByDescending(r => r.Id)
     .First();

var partnerGroups = Merge(old["ORGANIZATIONS"], orgUse,
    r => Normalize.NameKey(r.Str("ORG_NAME")),
    skip: r => r.Int("ORG_TYPE") == 10 && !orgUse.ContainsKey(r.Id)); // built-in "UNKNOWN"
var driverGroups = Merge(old["DRIVERS"], drvUse, r => Normalize.NameKey(DriverName(r)));
var vehicleGroups = Merge(old["CARS"], carUse,
    r => Normalize.PlateKey(r.Str("CAR_NUM")) is { Length: > 0 } k ? k : $"#{r.Id}");
var materialGroups = Merge(old["ARTICLE"], artUse,
    r => Normalize.NameKey(r.Str("ART_NAME")) + "|" + Normalize.NameKey(r.Str("ART_CODE")));

// ---------------------------------------------------------------- write new database
using var db = Schema.CreateNew(dstPath);
using var tx = db.BeginTransaction();

var partnerMap = new Dictionary<int, long>();
var driverMap = new Dictionary<int, long>();
var vehicleMap = new Dictionary<int, long>();
var materialMap = new Dictionary<int, long>();
var report = new StringBuilder();

void Note(string kind, IGrouping<string, Row> g, Func<Row, string> show, string chosen)
{
    if (g.Count() < 2) return;
    report.AppendLine($"  {kind}: \"{chosen}\" <= {string.Join(", ", g.Select(r => $"#{r.Id} \"{show(r)}\""))}");
}

foreach (var g in partnerGroups)
{
    var p = Pick(g, orgUse, r => new[] { "ORG_ADR_TOWN", "ORG_ADR_STREET", "ORG_ADR_ZIP" }.Count(c => r.Str(c).Length > 0));
    long id = Insert(db, tx, "partners", new()
    {
        ["name"] = p.Str("ORG_NAME"),
        ["zip"] = p.StrOrNull("ORG_ADR_ZIP"),
        ["town"] = p.StrOrNull("ORG_ADR_TOWN"),
        ["street"] = Normalize.CleanOrNull(p.Str("ORG_ADR_STREET") + " " + p.Str("ORG_ADR_STREET_NUM")),
        ["active"] = g.Any(r => !r.Bool("DEL_MARK")) ? 1 : 0,
        ["legacy_ids"] = string.Join(",", g.Select(r => r.Id)),
    });
    foreach (var r in g) partnerMap[r.Id] = id;
    Note("Partner", g, r => r.Str("ORG_NAME"), p.Str("ORG_NAME"));
}

foreach (var g in driverGroups)
{
    var d = Pick(g, drvUse);
    long id = Insert(db, tx, "drivers", new()
    {
        ["family_name"] = d.Str("NAME"),
        ["given_name"] = d.StrOrNull("SURNAME"),
        ["active"] = g.Any(r => !r.Bool("DEL_MARK")) ? 1 : 0,
        ["legacy_ids"] = string.Join(",", g.Select(r => r.Id)),
    });
    foreach (var r in g) driverMap[r.Id] = id;
    Note("Driver", g, DriverName, DriverName(d));
}

foreach (var g in vehicleGroups)
{
    // Display spelling: the one used in the most weighings (ties: used most recently); otherwise the representative row.
    var used = g.Where(r => lastByCar.ContainsKey(r.Id)).ToList();
    var latestUse = used.Select(r => lastByCar[r.Id]).OrderBy(w => ClosedAt(w)).ThenBy(w => w.Int("ID")).LastOrDefault();
    var shown = used.Count == 0 ? Pick(g, carUse) : used
        .GroupBy(r => r.Str("CAR_NUM"))
        .OrderByDescending(s => s.Sum(r => carUse.GetValueOrDefault(r.Id)))
        .ThenByDescending(s => s.Max(r => ClosedAt(lastByCar[r.Id])))
        .First().First();

    int? lastTare = latestUse is not null ? Weight(latestUse, "CAR_W") : null;
    DateTime? lastTareAt = latestUse is not null ? ClosedAt(latestUse) : null;
    lastTare ??= g.OrderByDescending(r => r.Id).Select(r => ParseWeight(r.Str("LAST_CAR_W"))).FirstOrDefault(w => w is > 0);

    // Suggested driver: the most frequent driver of this truck.
    var ids = g.Select(r => r.Id).ToHashSet();
    var topDriver = receipts.Where(w => w.Int("CAR_ID") is int c && ids.Contains(c) && w.Int("DRV_ID") is int dr && driverMap.ContainsKey(dr))
        .GroupBy(w => driverMap[w.Int("DRV_ID")!.Value])
        .OrderByDescending(x => x.Count()).Select(x => (long?)x.Key).FirstOrDefault();

    long id = Insert(db, tx, "vehicles", new()
    {
        ["plate"] = shown.Str("CAR_NUM"),
        ["plate_key"] = g.Key,
        ["last_tare"] = lastTare,
        ["last_tare_at"] = lastTareAt is DateTime t ? Schema.Ts(t) : null,
        ["default_driver_id"] = topDriver,
        ["active"] = g.Any(r => !r.Bool("DEL_MARK")) ? 1 : 0,
        ["legacy_ids"] = string.Join(",", g.Select(r => r.Id)),
    });
    foreach (var r in g) vehicleMap[r.Id] = id;
    Note("Vehicle", g, r => r.Str("CAR_NUM"), shown.Str("CAR_NUM"));
}

foreach (var g in materialGroups)
{
    var m = Pick(g, artUse);
    var std = m.Dbl("ART_STD_HUMI");
    long id = Insert(db, tx, "materials", new()
    {
        ["name"] = m.Str("ART_NAME"),
        ["note"] = m.StrOrNull("ART_CODE"),
        ["comment"] = std is > 0 ? $"{std:0.#}%" : null,
        ["active"] = g.Any(r => !r.Bool("DEL_MARK")) ? 1 : 0,
        ["legacy_ids"] = string.Join(",", g.Select(r => r.Id)),
    });
    foreach (var r in g) materialMap[r.Id] = id;
    Note("Material", g, r => $"{r.Str("ART_NAME")} / {r.Str("ART_CODE")}", m.Str("ART_NAME"));
}

// Lookups of the ORIGINAL rows, used for the per-weighing snapshot (historical spelling).
var orgById = old["ORGANIZATIONS"].ToDictionary(r => r.Id);
var carById = old["CARS"].ToDictionary(r => r.Id);
var drvById = old["DRIVERS"].ToDictionary(r => r.Id);
var artById = old["ARTICLE"].ToDictionary(r => r.Id);

Dictionary<string, object?> Common(Row w)
{
    var org = w.Int("ORG_ID") is int o ? orgById.GetValueOrDefault(o) : null;
    var car = w.Int("CAR_ID") is int c ? carById.GetValueOrDefault(c) : null;
    var drv = w.Int("DRV_ID") is int d ? drvById.GetValueOrDefault(d) : null;
    var art = w.Int("ART_ID") is int a ? artById.GetValueOrDefault(a) : null;
    return new()
    {
        ["partner_id"] = MapId(partnerMap, org),
        ["vehicle_id"] = MapId(vehicleMap, car),
        ["driver_id"] = MapId(driverMap, drv),
        ["material_id"] = MapId(materialMap, art),
        ["partner_name"] = org?.StrOrNull("ORG_NAME"),
        ["partner_address"] = org is null ? null : Normalize.CleanOrNull(string.Join(", ",
            new[] { Normalize.Clean(org.Str("ORG_ADR_STREET") + " " + org.Str("ORG_ADR_STREET_NUM")),
                    Normalize.Clean(org.Str("ORG_ADR_ZIP") + " " + org.Str("ORG_ADR_TOWN")) }.Where(s => s.Length > 0))),
        ["plate"] = car?.StrOrNull("CAR_NUM") ?? w.StrOrNull("CAR_NUM"),
        ["driver_name"] = drv is null ? null : Normalize.CleanOrNull(DriverName(drv)),
        ["material_name"] = art?.StrOrNull("ART_NAME"),
        ["material_note"] = art?.StrOrNull("ART_CODE"),
    };
}

static string Direction(Row r) => r.Int("OPER_TYPE") switch
{
    2 => "IN",   // Beszállítás
    1 => "OUT",  // Kiszállítás
    var t => throw new InvalidDataException($"Unknown OPER_TYPE {t} in row {r.Id}"),
};

int maxTicket = 0;
var usedTickets = new HashSet<int>();
foreach (var w in receipts.OrderBy(r => r.Id))
{
    var dir = Direction(w);
    int gross = Weight(w, "BRUTTO_W") ?? 0, tare = Weight(w, "CAR_W") ?? 0, net = Weight(w, "ART_W") ?? 0;
    var firstAt = Combine(w.Date("W_DATE_IN"), w.Date("W_TIME_IN"));
    var closedAt = ClosedAt(w);
    bool stored = firstAt is null;

    int ticket = int.TryParse(w.Str("TicketNumber"), out var tn) ? tn : w.Id;
    if (!usedTickets.Add(ticket)) throw new InvalidDataException($"Duplicate ticket number {ticket} (row {w.Id})");
    maxTicket = Math.Max(maxTicket, ticket);

    var v = Common(w);
    v["ticket_no"] = ticket;
    v["direction"] = dir;
    v["status"] = w.Bool("DEL_MARK") ? "VOID" : "DONE";
    if (stored)
    {
        // "Használ": one weighing (the loaded truck), tare taken from the vehicle's stored value.
        v["first_weight"] = gross;
        v["first_at"] = Ts(closedAt);
    }
    else
    {
        v["first_weight"] = dir == "IN" ? gross : tare;
        v["first_at"] = Ts(firstAt);
        v["second_weight"] = dir == "IN" ? tare : gross;
        v["second_at"] = Ts(closedAt);
    }
    v["closed_at"] = Ts(closedAt);
    v["gross"] = gross;
    v["tare"] = tare;
    v["net"] = net;
    v["stored_tare"] = stored ? 1 : 0;
    v["legacy_id"] = w.Id;
    Insert(db, tx, "weighings", v);
}

foreach (var t in inYard.OrderBy(r => r.Id))
{
    var v = Common(t);
    v["direction"] = Direction(t);
    v["status"] = "OPEN";
    v["first_weight"] = Weight(t, "WEIGHT");
    v["first_at"] = Ts(Combine(t.Date("DATE_IN"), t.Date("TIME_IN")));
    v["note"] = "Imported from TM_CARS";
    Insert(db, tx, "weighings", v);
}

// ---------------------------------------------------------------- settings
var settings = new Dictionary<string, string?>
{
    ["ticket.next_no"] = (maxTicket + 1).ToString(),
    ["import.source"] = srcPath,
    ["import.at"] = Schema.Ts(DateTime.Now),
};
var own = old["ORGANIZATIONS"].Where(r => r.Int("ORG_TYPE") == 2).OrderBy(r => r.Bool("DEL_MARK")).FirstOrDefault();
if (own is not null)
{
    settings["company.name"] = own.StrOrNull("ORG_NAME");
    settings["company.zip"] = own.StrOrNull("ORG_ADR_ZIP");
    settings["company.town"] = own.StrOrNull("ORG_ADR_TOWN");
    settings["company.street"] = Normalize.CleanOrNull(own.Str("ORG_ADR_STREET") + " " + own.Str("ORG_ADR_STREET_NUM"));
}
if (iniPath is not null)
{
    var ini = ReadIni(iniPath);
    string? I(string sec, string key) => Normalize.CleanOrNull(ini.GetValueOrDefault($"{sec}.{key}"));
    settings["company.name"] ??= I("USER", "NAME");
    settings["company.reg_no"] = I("USER", "ICO");
    settings["company.tax_no"] = I("USER", "DIC");
    settings["company.note"] = I("USER", "NOTE");
    settings["serial.port"] = I("COM", "PORT") is string port ? (OperatingSystem.IsWindows() ? "COM" + port : port) : null;
    settings["serial.baud"] = I("COM", "BAUD");
    settings["serial.parity"] = I("COM", "PARITY");
    settings["serial.data_bits"] = I("COM", "D_BITS");
    settings["serial.stop_bits"] = I("COM", "S_BITS");
    settings["scale.max"] = I("S120", "MAX");
    settings["scale.min"] = I("S120", "MIN");
    settings["scale.division"] = I("S120", "DIV");
    settings["printer.name"] = I("PRINTER", "NAME");
    settings["printer.font_name"] = I("PRINTER", "FONT_NAME");
    settings["printer.font_size"] = I("PRINTER", "FONT_SIZE");
    settings["ticket.form"] = I("TICKET", "NAME");
}
foreach (var (k, val) in settings.Where(kv => kv.Value is not null))
    Insert(db, tx, "settings", new() { ["key"] = k, ["value"] = val });

tx.Commit();

// ---------------------------------------------------------------- report and verification
int Count(string sql) { using var c = db.CreateCommand(); c.CommandText = sql; return Convert.ToInt32(c.ExecuteScalar()); }
Console.WriteLine();
Console.WriteLine($"Written: {dstPath}");
Console.WriteLine($"  partners   {Count("SELECT COUNT(*) FROM partners"),5}   (old {old["ORGANIZATIONS"].Count})");
Console.WriteLine($"  vehicles   {Count("SELECT COUNT(*) FROM vehicles"),5}   (old {old["CARS"].Count})");
Console.WriteLine($"  drivers    {Count("SELECT COUNT(*) FROM drivers"),5}   (old {old["DRIVERS"].Count})");
Console.WriteLine($"  materials  {Count("SELECT COUNT(*) FROM materials"),5}   (old {old["ARTICLE"].Count})");
Console.WriteLine($"  weighings  {Count("SELECT COUNT(*) FROM weighings"),5}   (old {receipts.Count} + {inYard.Count} in yard)");
Console.WriteLine($"    of which stored tare (Használ): {Count("SELECT COUNT(*) FROM weighings WHERE stored_tare=1")}");
Console.WriteLine($"  next ticket no: {maxTicket + 1}");

Console.WriteLine();
Console.WriteLine("Merged records:");
Console.Write(report.Length > 0 ? report.ToString() : "  (none)\n");

// Totals per year and direction: old vs new.
var oldTotals = receipts.GroupBy(r => (Year: ClosedAt(r)?.Year ?? 0, Dir: Direction(r)))
    .ToDictionary(g => g.Key, g => (N: g.Count(), Gross: g.Sum(r => (long)(Weight(r, "BRUTTO_W") ?? 0)),
        Tare: g.Sum(r => (long)(Weight(r, "CAR_W") ?? 0)), Net: g.Sum(r => (long)(Weight(r, "ART_W") ?? 0))));
var newTotals = new Dictionary<(int Year, string Dir), (int N, long Gross, long Tare, long Net)>();
using (var c = db.CreateCommand())
{
    c.CommandText = """
        SELECT CAST(COALESCE(substr(closed_at,1,4),'0') AS INTEGER), direction, COUNT(*), SUM(gross), SUM(tare), SUM(net)
        FROM weighings WHERE status <> 'OPEN' GROUP BY 1, 2
        """;
    using var rd = c.ExecuteReader();
    while (rd.Read())
        newTotals[(rd.GetInt32(0), rd.GetString(1))] = (rd.GetInt32(2), rd.GetInt64(3), rd.GetInt64(4), rd.GetInt64(5));
}
Console.WriteLine();
Console.WriteLine("Check (old = new):   year dir    count        gross         tare          net");
bool ok = true;
foreach (var k in oldTotals.Keys.Union(newTotals.Keys).OrderBy(k => k.Year).ThenBy(k => k.Dir))
{
    var o = oldTotals.GetValueOrDefault(k);
    var n = newTotals.GetValueOrDefault(k);
    bool same = o == n;
    ok &= same;
    Console.WriteLine($"  {(same ? "OK  " : "DIFF")}               {k.Year} {k.Dir,-4} {n.N,7} {n.Gross,12:N0} {n.Tare,12:N0} {n.Net,12:N0}" +
                      (same ? "" : $"   old: {o.N} {o.Gross:N0} {o.Tare:N0} {o.Net:N0}"));
}
Console.WriteLine(ok ? "All totals match." : "MISMATCH – see DIFF rows above.");
return ok ? 0 : 1;

// ================================================================= helpers

static List<Row> ReadTable(OleDbConnection c, string table)
{
    using var cmd = c.CreateCommand();
    cmd.CommandText = $"SELECT * FROM [{table}]";
    using var rd = cmd.ExecuteReader();
    var list = new List<Row>();
    while (rd.Read())
    {
        var d = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < rd.FieldCount; i++) d[rd.GetName(i)] = rd.IsDBNull(i) ? null : rd.GetValue(i);
        list.Add(new Row(d));
    }
    return list;
}

static long Insert(SqliteConnection db, SqliteTransaction tx, string table, Dictionary<string, object?> values)
{
    using var cmd = db.CreateCommand();
    cmd.Transaction = tx;
    cmd.CommandText = $"INSERT INTO {table} ({string.Join(",", values.Keys)}) VALUES ({string.Join(",", values.Keys.Select(k => "$" + k))}); SELECT last_insert_rowid();";
    foreach (var (k, v) in values) cmd.Parameters.AddWithValue("$" + k, v ?? DBNull.Value);
    return (long)cmd.ExecuteScalar()!;
}

static Dictionary<string, string> ReadIni(string path)
{
    var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    string section = "";
    foreach (var raw in File.ReadAllLines(path, Encoding.GetEncoding(1250)))
    {
        var line = raw.Trim();
        if (line.StartsWith('[') && line.EndsWith(']')) section = line[1..^1];
        else if (line.IndexOf('=') is int eq and > 0) d[$"{section}.{line[..eq].Trim()}"] = line[(eq + 1)..];
    }
    return d;
}

static long? MapId(Dictionary<int, long> map, Row? r) =>
    r is not null && map.TryGetValue(r.Id, out var id) ? id : null;

static string DriverName(Row r) => Normalize.Clean(r.Str("NAME") + " " + r.Str("SURNAME"));

static int? ParseWeight(string s) => int.TryParse(s.Replace(" ", ""), out var w) ? w : null;
static int? Weight(Row r, string col) => ParseWeight(r.Str(col));

// Access stores time-only values on the "zero date" 1899-12-30, so a date part before 1900 means "empty".
static DateTime? Combine(DateTime? date, DateTime? time) =>
    date is DateTime d && d.Year >= 1900 ? d.Date + (time?.TimeOfDay ?? TimeSpan.Zero) : null;

static DateTime? ClosedAt(Row r) => Combine(r.Date("W_DATE_OUT"), r.Date("W_TIME_OUT"));

static string? Ts(DateTime? t) => t is DateTime v ? Schema.Ts(v) : null;

/// <summary>One row of the old database, with helpers that undo its quirks (padding, text weights, 1899 dates).</summary>
sealed record Row(Dictionary<string, object?> Values)
{
    public int Id => Int("ID") ?? 0;
    public string Str(string c) => Normalize.Clean(Values.GetValueOrDefault(c)?.ToString());
    public string? StrOrNull(string c) => Normalize.CleanOrNull(Values.GetValueOrDefault(c)?.ToString());
    public int? Int(string c) => Values.GetValueOrDefault(c) is { } v ? Convert.ToInt32(v) : null;
    public double? Dbl(string c) => Values.GetValueOrDefault(c) is { } v ? Convert.ToDouble(v) : null;
    public bool Bool(string c) => Values.GetValueOrDefault(c) is bool b && b;
    /// <summary>Raw date/time value (see Combine for the 1899-12-30 "zero date").</summary>
    public DateTime? Date(string c) => Values.GetValueOrDefault(c) is DateTime d ? d : null;
}
