using System.Text;
using TruckScale.Core.Data;
using TruckScale.Core.Legacy;

namespace TruckScale.Core.Tests;

public sealed class LegacyTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ts-legacy-").FullName;

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string Write(string relative, string text)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, Encoding.GetEncoding(1250));
        return path;
    }

    private string Ini(string formPath = @"C:\nowhere\minta.form", string lastDb = "") => $"""
        [COM]
        PORT=8
        BAUD=9600
        PARITY=N
        D_BITS=8
        S_BITS=1
        [S120]
        MAX=60000
        DIV=20
        PROTOCOL_NAME=BWS
        [LIS120]
        LAST_DB_FILE_NAME={lastDb}
        OPERATOR=Mérlegkezelő Ödön
        [PRINTER]
        NAME=Minta nyomtató
        FONT_NAME=Arial
        [USER]
        NAME=Minta Gazdaság Kft.
        ADRESS_STREET=Fő utca
        ADRESS_CITY=Kisfalu
        ADRESS_NO=12
        ADRESS_ZIP=1234
        NOTE=Kisfalu külterület 0999/9 Hrsz
        DIC=12345678-1-12
        [TICKET]
        PATH={formPath}
        NAME=minta.form
        """;

    [Fact]
    public void Ini_becomes_settings()
    {
        var path = Write("old/TS.ini", Ini());
        var s = LegacyIni.ToSettings(LegacyIni.Read(path), path);
        Assert.Equal("COM8", s["serial.port"]);
        Assert.Equal("9600", s["serial.baud"]);
        Assert.Equal("Minta Gazdaság Kft.", s["company.name"]);        // Windows-1250 accents
        Assert.Equal("Fő utca 12", s["company.street"]);
        Assert.Equal("Kisfalu", s["company.town"]);
        Assert.Equal("1234", s["company.zip"]);
        Assert.Equal("Kisfalu külterület 0999/9 Hrsz", s["company.note"]);
        Assert.Equal("Mérlegkezelő Ödön", s["operator.name"]);
        Assert.Equal("Minta nyomtató", s["printer.name"]);
        Assert.Equal("fixed", s["scale.parse"]);                       // BWS = the old fixed positions
        Assert.Equal("6", s["scale.weight.start"]);
        Assert.Equal("minta.form", s["ticket.form"]);                  // form file not found: name only
        Assert.False(s.ContainsKey("company.reg_no"));                 // empty values are skipped
    }

    [Fact]
    public void Form_is_found_next_to_the_ini_and_copied_next_to_the_database()
    {
        var form = Write("old/Files/Forms/minta.form", "1;21;10;10;L;10;0;0;x;Mérlegjegy\n");
        var ini = Write("old/TS.ini", Ini());
        var s = LegacyIni.ToSettings(LegacyIni.Read(ini), ini);
        Assert.Equal(Path.GetFullPath(form), Path.GetFullPath(s["ticket.form"]));

        var dbPath = Path.Combine(_dir, "new", "truckscale.db");
        using var db = TruckScaleDb.OpenOrCreate(dbPath);
        db.SetSetting("company.name", "Már beállított név");
        LegacyImport.ApplyIni(db, s);
        Assert.Equal("minta.form", db.GetSetting("ticket.form"));
        Assert.True(File.Exists(Path.Combine(_dir, "new", "minta.form")));
        Assert.Equal("Már beállított név", db.GetSetting("company.name"));   // name/address only fill empty values
        Assert.Equal("COM8", db.GetSetting("serial.port"));
    }

    [Fact]
    public void Different_form_with_the_same_name_is_not_overwritten()
    {
        var src = Write("old/minta.form", "new layout");
        var existing = Write("db/minta.form", "my own layout");
        var name = LegacyImport.CopyForm(src, Path.GetDirectoryName(existing)!);
        Assert.NotEqual("minta.form", name);
        Assert.Equal("my own layout", File.ReadAllText(existing));
        Assert.Equal("minta.form", LegacyImport.CopyForm(Write("old2/minta.form", "my own layout"), Path.GetDirectoryName(existing)!));
    }

    [Fact]
    public void VirtualStore_copy_is_the_one_the_old_program_used()
    {
        var pf = Path.Combine(_dir, "Program Files (x86)");
        var local = Path.Combine(_dir, "Local");
        // VirtualStore mirrors the path below the drive root
        var relative = Path.GetFullPath(pf)[(Path.GetPathRoot(Path.GetFullPath(pf))!.Length)..];
        var vs = Path.Combine(local, "VirtualStore", relative);

        var iniPf = Write(Path.Combine(pf, "Truck Scale", "TS.ini"), Ini(lastDb: Path.Combine(pf, "Truck Scale", "FILES", "DATA.LIS")));
        Write(Path.Combine(pf, "Truck Scale", "Files", "DATA.LIS"), "old");
        var iniVs = Write(Path.Combine(vs, "Truck Scale", "TS.ini"), Ini(lastDb: Path.Combine(pf, "Truck Scale", "FILES", "DATA.LIS")));
        var lisVs = Write(Path.Combine(vs, "Truck Scale", "Files", "DATA.LIS"), "used");

        var loc = new LegacyLocator(local, [pf], [pf, Path.Combine(local, "VirtualStore")]);
        Assert.Equal(iniVs, loc.Effective(iniPf), ignoreCase: true);
        Assert.Equal(lisVs, loc.DatabaseOf(iniPf), ignoreCase: true);
        Assert.Equal(lisVs, loc.DatabaseOf(iniVs), ignoreCase: true);

        var (inis, dbs) = loc.Find();
        Assert.Equal(2, inis.Count);
        Assert.Equal(iniVs, inis[0].Path, ignoreCase: true);   // the one really in use first
        Assert.Equal(2, dbs.Count);
    }

    [Fact]
    public void Pending_import_replaces_the_database_and_keeps_a_backup()
    {
        var dbPath = Path.Combine(_dir, "truckscale.db");
        using (var cur = TruckScaleDb.OpenOrCreate(dbPath)) { cur.SetSetting("ui.language", "en"); cur.SetSetting("marker", "old"); }
        using (var imp = TruckScaleDb.OpenOrCreate(dbPath + LegacyImport.PendingSuffix))
        {
            imp.SetSetting("ticket.next_no", "414");
            imp.SetSetting("marker", "new");
            using var cur = TruckScaleDb.OpenOrCreate(dbPath);
            LegacyImport.CarryOver(cur.AllSettings(), imp);
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        Assert.Null(LegacyImport.ApplyPending(Path.Combine(_dir, "other.db")));
        var backup = LegacyImport.ApplyPending(dbPath);
        Assert.True(File.Exists(backup));
        Assert.False(File.Exists(dbPath + LegacyImport.PendingSuffix));
        using var now = TruckScaleDb.OpenOrCreate(dbPath);
        Assert.Equal("new", now.GetSetting("marker"));       // the import wins
        Assert.Equal("en", now.GetSetting("ui.language"));   // settings of the new program are kept
        Assert.Equal("414", now.GetSetting("ticket.next_no"));
    }
}
