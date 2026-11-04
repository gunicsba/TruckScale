using System.Globalization;
using Microsoft.Data.Sqlite;
using TruckScale.Core.Text;

namespace TruckScale.Core.Data;

/// <summary>All database access of the program. One instance per open database file.</summary>
public sealed class TruckScaleDb : IDisposable
{
    private readonly SqliteConnection _c;

    public string Path { get; }

    private TruckScaleDb(SqliteConnection c, string path) { _c = c; Path = path; }

    public static TruckScaleDb OpenOrCreate(string path)
    {
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var conn = File.Exists(path) ? Schema.Open(path) : Schema.CreateNew(path);
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA user_version";
            var v = Convert.ToInt32(cmd.ExecuteScalar());
            if (v != Schema.Version)
            {
                conn.Dispose();
                throw new InvalidDataException($"Unsupported database version {v} (expected {Schema.Version}): {path}");
            }
        }
        return new TruckScaleDb(conn, path);
    }

    public void Dispose() => _c.Dispose();

    // ------------------------------------------------------------------ settings

    public string? GetSetting(string key) =>
        Scalar("SELECT value FROM settings WHERE key = $k", ("$k", key)) as string;

    public void SetSetting(string key, string? value) =>
        Exec("INSERT INTO settings(key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
            null, ("$k", key), ("$v", value));

    public IReadOnlyDictionary<string, string?> AllSettings()
    {
        var d = new Dictionary<string, string?>();
        using var cmd = Cmd("SELECT key, value FROM settings");
        using var r = cmd.ExecuteReader();
        while (r.Read()) d[r.GetString(0)] = r.IsDBNull(1) ? null : r.GetString(1);
        return d;
    }

    // ------------------------------------------------------------------ master data

    public List<Partner> Partners() => Query("SELECT id, name, zip, town, street FROM partners WHERE active = 1 ORDER BY name",
        r => new Partner(r.GetInt64(0), r.GetString(1), Str(r, 2), Str(r, 3), Str(r, 4)));

    public List<Vehicle> Vehicles() => Query("SELECT id, plate, last_tare, last_tare_at, default_driver_id FROM vehicles WHERE active = 1 ORDER BY plate",
        r => new Vehicle(r.GetInt64(0), r.GetString(1), Int(r, 2), Dt(r, 3), Long(r, 4)));

    public List<Driver> Drivers() => Query("SELECT id, family_name, given_name FROM drivers WHERE active = 1 ORDER BY family_name, given_name",
        r => new Driver(r.GetInt64(0), r.GetString(1), Str(r, 2)));

    public List<Material> Materials() => Query("SELECT id, name, note, comment FROM materials WHERE active = 1 ORDER BY name, note",
        r => new Material(r.GetInt64(0), r.GetString(1), Str(r, 2), Str(r, 3)));

    public Vehicle? FindVehicle(string plate)
    {
        var key = Normalize.PlateKey(plate);
        return key.Length == 0 ? null : Query("SELECT id, plate, last_tare, last_tare_at, default_driver_id FROM vehicles WHERE plate_key = $k",
            r => new Vehicle(r.GetInt64(0), r.GetString(1), Int(r, 2), Dt(r, 3), Long(r, 4)), ("$k", key)).FirstOrDefault();
    }

    public Driver? GetDriver(long id) => Query("SELECT id, family_name, given_name FROM drivers WHERE id = $id",
        r => new Driver(r.GetInt64(0), r.GetString(1), Str(r, 2)), ("$id", id)).FirstOrDefault();

    // Find by normalised text among ALL rows (a matching inactive row is re-activated), otherwise create.

    private Partner ResolvePartner(string name, SqliteTransaction tx)
    {
        var key = Normalize.NameKey(name);
        var match = Query("SELECT id, name, zip, town, street, active FROM partners", r => (P: new Partner(r.GetInt64(0), r.GetString(1), Str(r, 2), Str(r, 3), Str(r, 4)), Active: r.GetInt64(5) == 1), tx)
            .Where(x => Normalize.NameKey(x.P.Name) == key).OrderByDescending(x => x.Active).Select(x => x.P).FirstOrDefault();
        if (match is not null) { Exec("UPDATE partners SET active = 1 WHERE id = $id", tx, ("$id", match.Id)); return match; }
        var clean = Normalize.Clean(name);
        var id = InsertId("INSERT INTO partners(name) VALUES ($n)", tx, ("$n", clean));
        return new Partner(id, clean, null, null, null);
    }

    private Vehicle ResolveVehicle(string plate, SqliteTransaction tx)
    {
        var key = Normalize.PlateKey(plate);
        if (key.Length == 0) throw new ArgumentException("Empty registration plate.");
        var match = Query("SELECT id, plate, last_tare, last_tare_at, default_driver_id FROM vehicles WHERE plate_key = $k",
            r => new Vehicle(r.GetInt64(0), r.GetString(1), Int(r, 2), Dt(r, 3), Long(r, 4)), tx, ("$k", key)).FirstOrDefault();
        if (match is not null) { Exec("UPDATE vehicles SET active = 1 WHERE id = $id", tx, ("$id", match.Id)); return match; }
        var clean = Normalize.Clean(plate).ToUpperInvariant();
        var id = InsertId("INSERT INTO vehicles(plate, plate_key) VALUES ($p, $k)", tx, ("$p", clean), ("$k", key));
        return new Vehicle(id, clean, null, null, null);
    }

    private Driver? ResolveDriver(string? fullName, SqliteTransaction tx)
    {
        var clean = Normalize.Clean(fullName);
        if (clean.Length == 0) return null;
        var key = Normalize.NameKey(clean);
        var match = Query("SELECT id, family_name, given_name, active FROM drivers", r => (D: new Driver(r.GetInt64(0), r.GetString(1), Str(r, 2)), Active: r.GetInt64(3) == 1), tx)
            .Where(x => Normalize.NameKey(x.D.FullName) == key).OrderByDescending(x => x.Active).Select(x => x.D).FirstOrDefault();
        if (match is not null) { Exec("UPDATE drivers SET active = 1 WHERE id = $id", tx, ("$id", match.Id)); return match; }
        // Hungarian order: family name first, the rest is the given name.
        var parts = clean.Split(' ', 2);
        var id = InsertId("INSERT INTO drivers(family_name, given_name) VALUES ($f, $g)", tx, ("$f", parts[0]), ("$g", parts.Length > 1 ? parts[1] : null));
        return new Driver(id, parts[0], parts.Length > 1 ? parts[1] : null);
    }

    private Material? ResolveMaterial(string? text, SqliteTransaction tx)
    {
        var clean = Normalize.Clean(text);
        if (clean.Length == 0) return null;
        var key = Normalize.NameKey(clean);
        var all = Query("SELECT id, name, note, comment, active FROM materials", r => (M: new Material(r.GetInt64(0), r.GetString(1), Str(r, 2), Str(r, 3)), Active: r.GetInt64(4) == 1), tx);
        // Match the display text "Name (note)" first, then the bare name.
        var match = all.Where(x => Normalize.NameKey(x.M.Display) == key).OrderByDescending(x => x.Active).Select(x => x.M).FirstOrDefault()
                 ?? all.Where(x => Normalize.NameKey(x.M.Name) == key).OrderByDescending(x => x.Active).ThenBy(x => x.M.Note is null ? 0 : 1).Select(x => x.M).FirstOrDefault();
        if (match is not null) { Exec("UPDATE materials SET active = 1 WHERE id = $id", tx, ("$id", match.Id)); return match; }
        var id = InsertId("INSERT INTO materials(name) VALUES ($n)", tx, ("$n", clean));
        return new Material(id, clean, null, null);
    }

    // ------------------------------------------------------------------ weighings

    private const string WeighingCols = """
        id, ticket_no, direction, status, partner_id, vehicle_id, driver_id, material_id,
        partner_name, partner_address, plate, driver_name, material_name, material_note, marking,
        first_weight, first_at, second_weight, second_at, closed_at, gross, tare, net, stored_tare
        """;

    private static Weighing ReadWeighing(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        TicketNo = Int(r, 1),
        Direction = r.GetString(2) == "IN" ? Direction.In : Direction.Out,
        Status = r.GetString(3) switch { "OPEN" => WeighingStatus.Open, "VOID" => WeighingStatus.Void, _ => WeighingStatus.Done },
        PartnerId = Long(r, 4), VehicleId = Long(r, 5), DriverId = Long(r, 6), MaterialId = Long(r, 7),
        PartnerName = Str(r, 8), PartnerAddress = Str(r, 9), Plate = Str(r, 10), DriverName = Str(r, 11),
        MaterialName = Str(r, 12), MaterialNote = Str(r, 13), Marking = Str(r, 14),
        FirstWeight = Int(r, 15), FirstAt = Dt(r, 16), SecondWeight = Int(r, 17), SecondAt = Dt(r, 18), ClosedAt = Dt(r, 19),
        Gross = Int(r, 20), Tare = Int(r, 21), Net = Int(r, 22), StoredTare = r.GetInt64(23) == 1,
    };

    public Weighing GetWeighing(long id) =>
        Query($"SELECT {WeighingCols} FROM weighings WHERE id = $id", ReadWeighing, ("$id", id)).Single();

    /// <summary>Trucks in the yard: weighings with only the first weight.</summary>
    public List<Weighing> OpenWeighings() =>
        Query($"SELECT {WeighingCols} FROM weighings WHERE status = 'OPEN' ORDER BY first_at", ReadWeighing);

    /// <summary>Closed and cancelled weighings with closed_at in [from, to).</summary>
    public List<Weighing> ClosedWeighings(DateTime from, DateTime to) =>
        Query($"SELECT {WeighingCols} FROM weighings WHERE status <> 'OPEN' AND closed_at >= $f AND closed_at < $t ORDER BY closed_at DESC",
            ReadWeighing, ("$f", Schema.Ts(from)), ("$t", Schema.Ts(to)));

    /// <summary>The most recently started weighing, used to pre-fill the arrival screen.</summary>
    public Weighing? LastWeighing(Direction? direction = null) =>
        Query($"SELECT {WeighingCols} FROM weighings {(direction is null ? "" : "WHERE direction = $d")} ORDER BY id DESC LIMIT 1",
            ReadWeighing, ("$d", direction == Direction.In ? "IN" : "OUT")).FirstOrDefault();

    /// <summary>First Mérés: the truck enters the yard (status OPEN).</summary>
    public Weighing StartWeighing(WeighingDraft d, int weight, DateTime at)
    {
        using var tx = _c.BeginTransaction();
        var v = Resolved(d, tx);
        if (Scalar("SELECT id FROM weighings WHERE status = 'OPEN' AND vehicle_id = $v", tx, ("$v", v.Vehicle.Id)) is not null)
            throw new InvalidOperationException($"A(z) {v.Vehicle.Plate} rendszámú jármű már az udvaron van.");
        var id = InsertWeighing(d, v, tx, new()
        {
            ["status"] = "OPEN",
            ["first_weight"] = weight,
            ["first_at"] = Schema.Ts(at),
        });
        tx.Commit();
        return GetWeighing(id);
    }

    /// <summary>One Mérés with the vehicle's stored tare ("Használ"): closed immediately.</summary>
    public Weighing SingleWeighing(WeighingDraft d, int weight, DateTime at)
    {
        using var tx = _c.BeginTransaction();
        var v = Resolved(d, tx);
        var res = WeighingMath.FromStoredTare(weight, v.Vehicle.LastTare ?? 0);
        var id = InsertWeighing(d, v, tx, new()
        {
            ["status"] = "DONE",
            ["ticket_no"] = NextTicketNo(tx),
            ["first_weight"] = weight,
            ["first_at"] = Schema.Ts(at),
            ["closed_at"] = Schema.Ts(at),
            ["gross"] = res.Gross, ["tare"] = res.Tare, ["net"] = res.Net,
            ["stored_tare"] = 1,
        });
        tx.Commit();
        return GetWeighing(id);
    }

    /// <summary>Second Mérés of a truck in the yard: closes the ticket and stores the vehicle's tare.</summary>
    public Weighing CloseWeighing(long id, int weight, DateTime at)
    {
        using var tx = _c.BeginTransaction();
        var w = Query($"SELECT {WeighingCols} FROM weighings WHERE id = $id", ReadWeighing, tx, ("$id", id)).Single();
        if (w.Status != WeighingStatus.Open) throw new InvalidOperationException("This weighing is not open.");
        var res = WeighingMath.FromPair(w.FirstWeight ?? 0, weight);
        Exec("""
            UPDATE weighings SET status = 'DONE', ticket_no = $t, second_weight = $w, second_at = $at, closed_at = $at,
                gross = $g, tare = $ta, net = $n WHERE id = $id
            """, tx, ("$t", NextTicketNo(tx)), ("$w", weight), ("$at", Schema.Ts(at)),
            ("$g", res.Gross), ("$ta", res.Tare), ("$n", res.Net), ("$id", id));
        if (w.VehicleId is long vid)
            Exec("UPDATE vehicles SET last_tare = $t, last_tare_at = $at, default_driver_id = COALESCE($d, default_driver_id) WHERE id = $id",
                tx, ("$t", res.Tare), ("$at", Schema.Ts(at)), ("$d", w.DriverId), ("$id", vid));
        tx.Commit();
        return GetWeighing(id);
    }

    /// <summary>Stornó of a truck in the yard: kept as VOID, never deleted.</summary>
    public void VoidWeighing(long id, DateTime at)
    {
        var n = Exec("UPDATE weighings SET status = 'VOID', closed_at = $at WHERE id = $id AND status = 'OPEN'",
            null, ("$at", Schema.Ts(at)), ("$id", id));
        if (n != 1) throw new InvalidOperationException("This weighing is not open.");
    }

    private sealed record Refs(Partner Partner, Vehicle Vehicle, Driver? Driver, Material? Material);

    private Refs Resolved(WeighingDraft d, SqliteTransaction tx)
    {
        if (string.IsNullOrWhiteSpace(d.PartnerName)) throw new ArgumentException("Az ügyfél nincs megadva.");
        if (string.IsNullOrWhiteSpace(d.Plate)) throw new ArgumentException("A rendszám nincs megadva.");
        return new Refs(ResolvePartner(d.PartnerName, tx), ResolveVehicle(d.Plate, tx), ResolveDriver(d.DriverName, tx), ResolveMaterial(d.MaterialName, tx));
    }

    private long InsertWeighing(WeighingDraft d, Refs v, SqliteTransaction tx, Dictionary<string, object?> extra)
    {
        var values = new Dictionary<string, object?>
        {
            ["direction"] = d.Direction == Direction.In ? "IN" : "OUT",
            ["partner_id"] = v.Partner.Id,
            ["vehicle_id"] = v.Vehicle.Id,
            ["driver_id"] = v.Driver?.Id,
            ["material_id"] = v.Material?.Id,
            ["partner_name"] = v.Partner.Name,
            ["partner_address"] = Normalize.CleanOrNull(v.Partner.Address),
            ["plate"] = v.Vehicle.Plate,
            ["driver_name"] = v.Driver?.FullName,
            ["material_name"] = v.Material?.Name,
            ["material_note"] = v.Material?.Note,
            ["marking"] = Normalize.CleanOrNull(d.Marking),
        };
        foreach (var (k, val) in extra) values[k] = val;
        return InsertId($"INSERT INTO weighings({string.Join(",", values.Keys)}) VALUES ({string.Join(",", values.Keys.Select(k => "$" + k))})",
            tx, values.Select(kv => ("$" + kv.Key, kv.Value)).ToArray());
    }

    private int NextTicketNo(SqliteTransaction tx)
    {
        var fromSetting = int.TryParse(Scalar("SELECT value FROM settings WHERE key = 'ticket.next_no'", tx) as string, out var s) ? s : 1;
        var fromData = Convert.ToInt32(Scalar("SELECT COALESCE(MAX(ticket_no), 0) + 1 FROM weighings", tx));
        var no = Math.Max(fromSetting, fromData);
        Exec("INSERT INTO settings(key, value) VALUES ('ticket.next_no', $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
            tx, ("$v", (no + 1).ToString(CultureInfo.InvariantCulture)));
        return no;
    }

    // ------------------------------------------------------------------ helpers

    private SqliteCommand Cmd(string sql, SqliteTransaction? tx = null, params (string, object?)[] p)
    {
        var cmd = _c.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        foreach (var (k, v) in p) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        return cmd;
    }

    private int Exec(string sql, SqliteTransaction? tx, params (string, object?)[] p) { using var c = Cmd(sql, tx, p); return c.ExecuteNonQuery(); }
    private object? Scalar(string sql, params (string, object?)[] p) => Scalar(sql, null, p);
    private object? Scalar(string sql, SqliteTransaction? tx, params (string, object?)[] p) { using var c = Cmd(sql, tx, p); var v = c.ExecuteScalar(); return v is DBNull ? null : v; }
    private long InsertId(string sql, SqliteTransaction tx, params (string, object?)[] p) { using var c = Cmd(sql + "; SELECT last_insert_rowid();", tx, p); return (long)c.ExecuteScalar()!; }

    private List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string, object?)[] p) => Query(sql, map, null, p);
    private List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, SqliteTransaction? tx, params (string, object?)[] p)
    {
        using var cmd = Cmd(sql, tx, p);
        using var r = cmd.ExecuteReader();
        var list = new List<T>();
        while (r.Read()) list.Add(map(r));
        return list;
    }

    private static string? Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static int? Int(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt32(i);
    private static long? Long(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);
    private static DateTime? Dt(SqliteDataReader r, int i) =>
        r.IsDBNull(i) ? null : DateTime.ParseExact(r.GetString(i), "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
}
