using TruckScale.Core.Data;

namespace TruckScale.Core.Legacy;

/// <summary>Steps of "take over from the old program" that do not need the old database driver.</summary>
public static class LegacyImport
{
    /// <summary>Suffix of a finished import waiting to replace the database at the next start.</summary>
    public const string PendingSuffix = ".import";

    /// <summary>
    /// Writes settings from TS.ini into the database. The ticket form file is copied next to the database
    /// (so it stays even if the old program is removed). Company name and address only fill empty values.
    /// Returns the keys written.
    /// </summary>
    public static List<string> ApplyIni(TruckScaleDb db, IReadOnlyDictionary<string, string> fromIni)
    {
        var written = new List<string>();
        var current = db.AllSettings();
        foreach (var (key, value) in fromIni)
        {
            var v = value;
            if (LegacyIni.FillOnly.Contains(key) && !string.IsNullOrWhiteSpace(current.GetValueOrDefault(key))) continue;
            if (key == "ticket.form" && Path.IsPathRooted(v) && File.Exists(v))
                v = CopyForm(v, Path.GetDirectoryName(Path.GetFullPath(db.Path))!);
            db.SetSetting(key, v);
            written.Add(key);
        }
        return written;
    }

    /// <summary>
    /// Copies a ticket form into a folder and returns the file name to store in "ticket.form".
    /// An existing different file of the same name is kept: the copy then gets a date suffix.
    /// </summary>
    public static string CopyForm(string source, string folder)
    {
        var name = Path.GetFileName(source);
        var target = Path.Combine(folder, name);
        if (Path.GetFullPath(target).Equals(Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase)) return name;
        if (File.Exists(target) && !File.ReadAllBytes(target).AsSpan().SequenceEqual(File.ReadAllBytes(source)))
        {
            name = $"{Path.GetFileNameWithoutExtension(name)}-{DateTime.Now:yyyyMMdd-HHmmss}{Path.GetExtension(name)}";
            target = Path.Combine(folder, name);
        }
        File.Copy(source, target, overwrite: true);
        return name;
    }

    /// <summary>
    /// Settings of the current database that a freshly imported one should keep (language, scale parsing,
    /// anything set up in the new program) – everything the import did not write itself.
    /// </summary>
    public static void CarryOver(IReadOnlyDictionary<string, string?> current, TruckScaleDb imported)
    {
        var have = imported.AllSettings();
        foreach (var (k, v) in current)
        {
            if (v is null || k.StartsWith("import.") || k == "ticket.next_no" || have.ContainsKey(k)) continue;
            imported.SetSetting(k, v);
        }
    }

    /// <summary>
    /// If an import is waiting (<c>truckscale.db.import</c>), the current database is renamed to a backup
    /// and the import takes its place. Waits a little for a previous instance to release the file.
    /// Returns the backup path, or null if nothing was pending.
    /// </summary>
    public static string? ApplyPending(string dbPath, TimeSpan? wait = null)
    {
        var pending = dbPath + PendingSuffix;
        if (!File.Exists(pending)) return null;
        var until = DateTime.UtcNow + (wait ?? TimeSpan.FromSeconds(15));
        string? backup = null;
        while (true)
        {
            try
            {
                if (File.Exists(dbPath))
                {
                    backup ??= Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dbPath))!,
                        $"{Path.GetFileNameWithoutExtension(dbPath)}-backup-{DateTime.Now:yyyyMMdd-HHmmss}{Path.GetExtension(dbPath)}");
                    File.Move(dbPath, backup);
                    foreach (var side in new[] { "-journal", "-wal", "-shm" })   // keep the backup complete
                        if (File.Exists(dbPath + side)) File.Move(dbPath + side, backup + side, overwrite: true);
                }
                File.Move(pending, dbPath);
                return backup ?? "";
            }
            catch (IOException) when (DateTime.UtcNow < until)
            {
                Thread.Sleep(250);   // the previous instance is still closing
            }
        }
    }
}
