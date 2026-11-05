using System.Text;
using TruckScale.Core.Text;

namespace TruckScale.Core.Legacy;

/// <summary>Reads the old program's TS.ini (Windows-1250) and turns it into settings of the new program.</summary>
public static class LegacyIni
{
    /// <summary>"SECTION.KEY" → value (case-insensitive).</summary>
    public static Dictionary<string, string> Read(string path)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
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

    /// <summary>Setting keys that only fill in an empty value (the own company from DATA.LIS comes first).</summary>
    public static readonly HashSet<string> FillOnly = ["company.name", "company.zip", "company.town", "company.street"];

    /// <summary>
    /// New settings from the ini (only non-empty values). The ticket form becomes the full path of the
    /// file if it can be found (see <see cref="FindForm"/>), otherwise the file name from the ini.
    /// </summary>
    public static Dictionary<string, string> ToSettings(IReadOnlyDictionary<string, string> ini, string? iniPath = null, bool windows = true)
    {
        string? I(string key) => Normalize.CleanOrNull(ini.GetValueOrDefault(key));
        var s = new Dictionary<string, string?>
        {
            ["company.name"] = I("USER.NAME"),
            ["company.zip"] = I("USER.ADRESS_ZIP"),
            ["company.town"] = I("USER.ADRESS_CITY"),
            ["company.street"] = Normalize.CleanOrNull($"{I("USER.ADRESS_STREET")} {I("USER.ADRESS_NO")}"),
            ["company.note"] = I("USER.NOTE"),
            ["company.reg_no"] = I("USER.ICO"),
            ["company.tax_no"] = I("USER.DIC"),
            ["operator.name"] = I("LIS120.OPERATOR"),
            ["serial.port"] = I("COM.PORT") is string port ? (windows && int.TryParse(port, out _) ? "COM" + port : port) : null,
            ["serial.baud"] = I("COM.BAUD"),
            ["serial.parity"] = I("COM.PARITY"),
            ["serial.data_bits"] = I("COM.D_BITS"),
            ["serial.stop_bits"] = I("COM.S_BITS"),
            ["scale.max"] = I("S120.MAX"),
            ["scale.min"] = I("S120.MIN"),
            ["scale.division"] = I("S120.DIV"),
            ["printer.name"] = I("PRINTER.NAME"),
            ["printer.font_name"] = I("PRINTER.FONT_NAME"),
            ["printer.font_size"] = I("PRINTER.FONT_SIZE"),
            ["ticket.form"] = FindForm(ini, iniPath) ?? I("TICKET.NAME"),
        };
        if (I("S120.PROTOCOL_NAME") is string protocol)
        {
            // BWS = the fixed positions of the old program (ST at 0/2, weight at 6/8, CR LF); anything else: automatic.
            var p = string.Equals(protocol, "BWS", StringComparison.OrdinalIgnoreCase)
                ? new Scale.ScaleProtocol()
                : new Scale.ScaleProtocol(Scale.ParseMode.Auto);
            foreach (var (k, v) in p.ToSettings()) s[k] = v;
        }
        return s.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value!);
    }

    /// <summary>
    /// The ticket form file of the old program: [TICKET] PATH if it exists, else the file [TICKET] NAME
    /// next to the ini, in its Files / Files\Forms folder or in the Documents folder.
    /// </summary>
    public static string? FindForm(IReadOnlyDictionary<string, string> ini, string? iniPath)
    {
        var path = Normalize.CleanOrNull(ini.GetValueOrDefault("TICKET.PATH"));
        if (path is not null && File.Exists(path)) return path;
        var name = Normalize.CleanOrNull(ini.GetValueOrDefault("TICKET.NAME")) ?? (path is null ? null : FileName(path));
        if (name is null) return null;
        var dirs = new List<string>();
        if (iniPath is not null && Path.GetDirectoryName(Path.GetFullPath(iniPath)) is { } d)
            dirs.AddRange([d, Path.Combine(d, "Files"), Path.Combine(d, "Files", "Forms")]);
        dirs.Add(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        return dirs.Where(x => x.Length > 0).Select(x => Path.Combine(x, name)).FirstOrDefault(File.Exists);
    }

    /// <summary>File name of a path written on any system (the ini contains Windows paths).</summary>
    private static string FileName(string path) => path[(path.LastIndexOfAny(['\\', '/']) + 1)..];
}

/// <summary>A file of the old program found on this computer.</summary>
public sealed record LegacyFile(string Path, DateTime Modified, long Size)
{
    public static LegacyFile Of(string path) { var f = new FileInfo(path); return new(f.FullName, f.LastWriteTime, f.Length); }
}

/// <summary>
/// Finds the old program's TS.ini and DATA.LIS. The old program usually ran from "Program Files" without
/// administrator rights, so Windows redirected its writes to %LOCALAPPDATA%\VirtualStore\…: the copy there
/// (if it exists) is the one the old program really used.
/// </summary>
public sealed class LegacyLocator(string? localAppData = null, IEnumerable<string>? programFiles = null, IEnumerable<string>? searchRoots = null)
{
    private readonly string _virtualStore = Path.Combine(
        localAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VirtualStore");
    private readonly string[] _programFiles = (programFiles ?? DefaultProgramFiles()).Where(p => p.Length > 0).ToArray();
    private readonly string[] _roots = (searchRoots ?? DefaultRoots()).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static IEnumerable<string> DefaultProgramFiles() =>
    [
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
    ];

    private static IEnumerable<string> DefaultRoots()
    {
        foreach (var p in DefaultProgramFiles()) yield return p;
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VirtualStore");
        yield return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        yield return Path.GetPathRoot(Environment.SystemDirectory) ?? "";
    }

    /// <summary>The file the old program really used: its VirtualStore copy, if there is one.</summary>
    public string Effective(string path)
    {
        var full = Path.GetFullPath(path);
        foreach (var pf in _programFiles)
        {
            var root = Path.GetFullPath(pf).TrimEnd('\\', '/');
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            var relative = full[(Path.GetPathRoot(full)?.Length ?? 0)..];          // "Program Files (x86)\Truck Scale\TS.ini"
            var redirected = Path.Combine(_virtualStore, relative);
            if (File.Exists(redirected)) return redirected;
        }
        return full;
    }

    /// <summary>The database an ini points to ([LIS120] LAST_DB_FILE_NAME, else Files\DATA.LIS next to it).</summary>
    public string? DatabaseOf(string iniPath)
    {
        var ini = LegacyIni.Read(iniPath);
        var candidates = new List<string>();
        if (Normalize.CleanOrNull(ini.GetValueOrDefault("LIS120.LAST_DB_FILE_NAME")) is { } last) candidates.Add(last);
        // An ini from VirtualStore describes the program in Program Files: look next to both.
        var dir = Path.GetDirectoryName(Path.GetFullPath(iniPath))!;
        candidates.Add(Path.Combine(dir, "Files", "DATA.LIS"));
        if (Unvirtualize(dir) is { } original) candidates.Add(Path.Combine(original, "Files", "DATA.LIS"));
        foreach (var c in candidates)
        {
            try
            {
                var e = Effective(c);
                if (File.Exists(e)) return e;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { /* bad path in the ini */ }
        }
        return null;
    }

    /// <summary>VirtualStore folder → the original Program Files folder (null if not in VirtualStore).</summary>
    private string? Unvirtualize(string dir)
    {
        var vs = Path.GetFullPath(_virtualStore).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        if (!dir.StartsWith(vs, StringComparison.OrdinalIgnoreCase)) return null;
        var relative = dir[vs.Length..];
        return _programFiles.Select(pf => Path.Combine(Path.GetPathRoot(Path.GetFullPath(pf)) ?? "", relative))
            .FirstOrDefault(Directory.Exists);
    }

    /// <summary>
    /// All TS.ini and DATA.LIS files in the usual places (a few folder levels deep), plus the folders given.
    /// The likely ones first: inis that the old program really uses, then newest; databases newest first.
    /// </summary>
    public (List<LegacyFile> Inis, List<LegacyFile> Databases) Find(IEnumerable<string>? extraDirs = null)
    {
        var inis = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in (extraDirs ?? []).Concat(_roots))
        {
            int depth = root.Length <= 3 ? 1 : 3; // drive root: shallow; "…\Truck Scale\Files\DATA.LIS" is 2 levels deep
            foreach (var f in Search(root, depth))
            {
                var name = Path.GetFileName(f);
                if (name.Equals("TS.ini", StringComparison.OrdinalIgnoreCase)) inis.Add(f);
                else dbs.Add(f);
            }
        }
        foreach (var ini in inis.ToList())
        {
            inis.Add(Effective(ini));   // its VirtualStore copy
            if (DatabaseOfSafe(ini) is { } db) dbs.Add(db);
        }

        bool used(string ini) => string.Equals(Effective(ini), ini, StringComparison.OrdinalIgnoreCase);
        var iniList = inis.Select(LegacyFile.Of)
            .OrderByDescending(f => used(f.Path)).ThenByDescending(f => f.Modified).ToList();
        var dbList = dbs.Select(LegacyFile.Of).OrderByDescending(f => f.Modified).ToList();
        return (iniList, dbList);
    }

    private string? DatabaseOfSafe(string ini) { try { return DatabaseOf(ini); } catch (IOException) { return null; } catch (UnauthorizedAccessException) { return null; } }

    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "$Recycle.Bin", "System Volume Information", "node_modules", ".git", "WindowsApps", "Microsoft", "Common Files",
    };

    private static IEnumerable<string> Search(string dir, int depth)
    {
        if (!Directory.Exists(dir)) yield break;
        string[] files = [], subdirs = [];
        try
        {
            files = Directory.GetFiles(dir);
            if (depth > 0) subdirs = Directory.GetDirectories(dir);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { }
        foreach (var f in files)
        {
            var n = Path.GetFileName(f);
            if (n.Equals("TS.ini", StringComparison.OrdinalIgnoreCase) || n.Equals("DATA.LIS", StringComparison.OrdinalIgnoreCase)) yield return f;
        }
        foreach (var s in subdirs)
        {
            if (SkipDirs.Contains(Path.GetFileName(s))) continue;
            try { if (new DirectoryInfo(s).Attributes.HasFlag(FileAttributes.ReparsePoint)) continue; } catch { continue; }
            foreach (var f in Search(s, depth - 1)) yield return f;
        }
    }
}
