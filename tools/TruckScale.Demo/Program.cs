// TruckScale.Demo – creates a database with fictional demo data (company, customers, trucks, ~2 years of
// weighings). Used for screenshots, documentation and trying the program without real data.
//
// Usage: TruckScale.Demo <demo.db> [--overwrite]

using TruckScale.Core.Data;

var path = args.FirstOrDefault(a => !a.StartsWith("--")) ?? "demo.db";
if (File.Exists(path))
{
    if (!args.Contains("--overwrite")) { Console.Error.WriteLine($"Already exists (use --overwrite): {path}"); return 2; }
    File.Delete(path);
}

using var db = TruckScaleDb.OpenOrCreate(path);
var rnd = new Random(42);

// ---------------------------------------------------------------- settings (fictional company)
foreach (var (k, v) in new Dictionary<string, string>
{
    ["company.name"] = "Minta Gazdaság Kft.",
    ["company.zip"] = "1234",
    ["company.town"] = "Mintafalva",
    ["company.street"] = "Fő utca 1.",
    ["company.note"] = "Mintafalva külterület 0123/4 Hrsz",
    ["company.tax_no"] = "12345678-2-12",
    ["serial.port"] = "COM1",
    ["serial.baud"] = "9600",
    ["scale.simulator"] = "1",
    ["scale.max"] = "60000",
    ["scale.division"] = "20",
    ["printer.font_name"] = "Arial",
    ["ticket.next_no"] = "1",
})
    db.SetSetting(k, v);

// ---------------------------------------------------------------- master data
var partners = new (string Name, PartnerDetails Details)[]
{
    ("Minta Gazdaság Kft.", new("Fő utca 1.", "Mintafalva", "1234", "saját")),
    ("Példa Péter", new("Kossuth út 12.", "Mintafalva", "1234", null)),
    ("Kitalált Kata", new("Petőfi utca 3.", "Mintafalva", "1234", null)),
    ("Alma Aladár", new("Rákóczi út 45.", "Szomszédfalu", "1235", null)),
    ("Búzavirág Bt.", new("Ipari park 7.", "Kisváros", "1240", "felvásárló")),
    ("Dél-Mező Zrt.", new("Major utca 2.", "Nagyváros", "1300", "felvásárló")),
    ("Erdei Ernő", new("Erdő sor 8.", "Szomszédfalu", "1235", null)),
    ("Gabona Gábor", new("Táncsics utca 21.", "Mintafalva", "1234", null)),
    ("Határ Hajnalka", new("Dózsa György út 5.", "Kisváros", "1240", null)),
    ("Mező Mihály", new("Ady Endre utca 9.", "Mintafalva", "1234", null)),
};
var trucks = new (string Plate, string Driver, int Tare)[]
{
    ("ABC-123/XYZ-456", "Minta Márton", 11300),
    ("DEF-456/UVW-789", "Minta Márton", 9800),
    ("GHI-789/RST-012", "Példa Pál", 10500),
    ("JKL-012", "Teszt Tamás", 7200),
    ("MNO-345/OPQ-678", "Kitalált Kristóf", 12100),
};
string[] inMaterials = ["Búza", "Búza", "Malmi búza", "Árpa", "Kukorica", "Kukorica", "Napraforgó", "Repce"];
string[] outMaterials = ["Búza", "Kukorica", "Napraforgó", "Árpa"];
string[] markings = ["", "", "", "", "Tábla 3", "Tábla 7", "Szárító", "Szerződés 2025/14"];

DateTime today = DateTime.Today;
int count = 0;

void Weighing(Direction dir, DateTime at, int partner, int truck, string material, int net, bool storedTare = false)
{
    var (plate, driver, tare) = trucks[truck];
    tare += rnd.Next(-6, 7) * 20;
    var draft = new WeighingDraft(dir, partners[partner].Name, plate, driver, material,
        markings[rnd.Next(markings.Length)], partners[partner].Details);
    int gross = tare + net;
    if (storedTare && db.FindVehicle(plate)?.LastTare is int last && gross > last)
    {
        db.SingleWeighing(draft, gross, at);
    }
    else
    {
        var first = db.StartWeighing(draft, dir == Direction.In ? gross : tare, at);
        db.CloseWeighing(first.Id, dir == Direction.In ? tare : gross, at.AddMinutes(rnd.Next(8, 35)));
    }
    count++;
}

// Two seasons: harvest (in) June–October, selling (out) November–April.
foreach (int year in new[] { today.Year - 1, today.Year })
{
    for (var day = new DateTime(year, 1, 1); day <= new DateTime(year, 12, 31) && day < today; day = day.AddDays(1))
    {
        bool harvest = day.Month is >= 6 and <= 10;
        bool selling = day.Month is >= 11 or <= 4;
        int loads = harvest ? (rnd.NextDouble() < 0.35 ? rnd.Next(2, 9) : 0) : selling ? (rnd.NextDouble() < 0.06 ? rnd.Next(1, 4) : 0) : 0;
        var t = day.AddHours(7).AddMinutes(rnd.Next(0, 60));
        for (int i = 0; i < loads; i++)
        {
            t = t.AddMinutes(rnd.Next(30, 90));
            if (harvest)
            {
                string m = day.Month switch
                {
                    6 or 7 => inMaterials[rnd.Next(0, 4)],   // cereals
                    8 => rnd.NextDouble() < 0.5 ? "Repce" : "Búza",
                    _ => inMaterials[rnd.Next(4, 7)],          // maize, sunflower
                };
                Weighing(Direction.In, t, rnd.Next(0, 10) is var p && p is 4 or 5 ? 0 : p, rnd.Next(trucks.Length), m,
                    rnd.Next(70, 150) * 100, storedTare: rnd.NextDouble() < 0.15);
            }
            else
            {
                Weighing(Direction.Out, t, rnd.NextDouble() < 0.6 ? 4 : 5, rnd.Next(trucks.Length), outMaterials[rnd.Next(outMaterials.Length)],
                    rnd.Next(180, 260) * 100);
            }
        }
    }
}

// Today: a few closed tickets, one cancelled, and two trucks still in the yard.
var now = today.AddHours(8);
Weighing(Direction.In, now, 1, 0, "Kukorica", 14200);
Weighing(Direction.In, now.AddMinutes(50), 2, 2, "Napraforgó", 9800);
Weighing(Direction.Out, now.AddMinutes(95), 5, 4, "Búza", 23600);
var voided = db.StartWeighing(new WeighingDraft(Direction.In, partners[3].Name, trucks[3].Plate, trucks[3].Driver, "Árpa", null), 15800, now.AddMinutes(120));
db.VoidWeighing(voided.Id, now.AddMinutes(122));
db.StartWeighing(new WeighingDraft(Direction.In, partners[1].Name, trucks[0].Plate, trucks[0].Driver, "Kukorica", "Tábla 3"), 25680, now.AddMinutes(150));
db.StartWeighing(new WeighingDraft(Direction.Out, partners[4].Name, trucks[4].Plate, trucks[4].Driver, "Búza", null), 12080, now.AddMinutes(170));

Console.WriteLine($"Demo database: {Path.GetFullPath(path)}  ({count} closed weighings, 2 trucks in the yard, 1 cancelled)");
return 0;
