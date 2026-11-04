using Microsoft.Data.Sqlite;

namespace TruckScale.Core.Data;

/// <summary>SQLite schema of the new TruckScale database. See docs/DATABASE.md.</summary>
public static class Schema
{
    public const int Version = 1;

    public const string Sql = """
        CREATE TABLE partners (
            id          INTEGER PRIMARY KEY,
            name        TEXT NOT NULL,
            zip         TEXT,
            town        TEXT,
            street      TEXT,
            note        TEXT,
            active      INTEGER NOT NULL DEFAULT 1,
            legacy_ids  TEXT
        );

        CREATE TABLE drivers (
            id          INTEGER PRIMARY KEY,
            family_name TEXT NOT NULL,
            given_name  TEXT,
            active      INTEGER NOT NULL DEFAULT 1,
            legacy_ids  TEXT
        );

        CREATE TABLE vehicles (
            id                INTEGER PRIMARY KEY,
            plate             TEXT NOT NULL,
            plate_key         TEXT NOT NULL UNIQUE,
            last_tare         INTEGER,
            last_tare_at      TEXT,
            default_driver_id INTEGER REFERENCES drivers(id),
            active            INTEGER NOT NULL DEFAULT 1,
            legacy_ids        TEXT
        );

        CREATE TABLE materials (
            id          INTEGER PRIMARY KEY,
            name        TEXT NOT NULL,
            note        TEXT,
            comment     TEXT,
            active      INTEGER NOT NULL DEFAULT 1,
            legacy_ids  TEXT
        );

        CREATE TABLE weighings (
            id              INTEGER PRIMARY KEY,
            ticket_no       INTEGER UNIQUE,
            direction       TEXT NOT NULL CHECK (direction IN ('IN','OUT')),
            status          TEXT NOT NULL CHECK (status IN ('OPEN','DONE','VOID')),
            partner_id      INTEGER REFERENCES partners(id),
            vehicle_id      INTEGER REFERENCES vehicles(id),
            driver_id       INTEGER REFERENCES drivers(id),
            material_id     INTEGER REFERENCES materials(id),
            partner_name    TEXT,
            partner_address TEXT,
            plate           TEXT,
            driver_name     TEXT,
            material_name   TEXT,
            material_note   TEXT,
            marking         TEXT,
            first_weight    INTEGER,
            first_at        TEXT,
            second_weight   INTEGER,
            second_at       TEXT,
            closed_at       TEXT,
            gross           INTEGER,
            tare            INTEGER,
            net             INTEGER,
            stored_tare     INTEGER NOT NULL DEFAULT 0,
            operator        TEXT,
            note            TEXT,
            legacy_id       INTEGER
        );
        CREATE INDEX ix_weighings_status ON weighings(status);
        CREATE INDEX ix_weighings_closed_at ON weighings(closed_at);

        CREATE TABLE settings (
            key   TEXT PRIMARY KEY,
            value TEXT
        );
        """;

    /// <summary>Creates a new, empty database file. Fails if the file already exists.</summary>
    public static SqliteConnection CreateNew(string path)
    {
        if (File.Exists(path))
            throw new IOException($"Database already exists: {path}");

        var conn = Open(path);
        using var tx = conn.BeginTransaction();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = Sql + $"\nPRAGMA user_version = {Version};";
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return conn;
    }

    public static SqliteConnection Open(string path)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            ForeignKeys = true,
        }.ToString());
        conn.Open();
        return conn;
    }

    /// <summary>Timestamp format stored in the database (local time, ISO 8601).</summary>
    public static string Ts(DateTime t) => t.ToString("yyyy-MM-dd'T'HH:mm:ss");
}
