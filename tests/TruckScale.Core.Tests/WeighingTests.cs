using TruckScale.Core.Data;

namespace TruckScale.Core.Tests;

public sealed class WeighingTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"ts-test-{Guid.NewGuid():N}.db");
    private readonly TruckScaleDb _db;
    private static readonly DateTime T0 = new(2026, 7, 1, 8, 0, 0);

    public WeighingTests() => _db = TruckScaleDb.OpenOrCreate(_path);

    public void Dispose()
    {
        _db.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    private static WeighingDraft Draft(Direction dir = Direction.In, string plate = "ABC123/XYZ063") =>
        new(dir, "Teszt Ügyfél", plate, "Teszt Sofőr", "Árpa", null);

    [Fact]
    public void Two_weighings_in_close_with_ticket_and_store_tare()
    {
        var open = _db.StartWeighing(Draft(), 26440, T0);
        Assert.Equal(WeighingStatus.Open, open.Status);
        Assert.Null(open.TicketNo);
        Assert.Single(_db.OpenWeighings());

        var done = _db.CloseWeighing(open.Id, 11300, T0.AddMinutes(7));
        Assert.Equal(WeighingStatus.Done, done.Status);
        Assert.Equal((26440, 11300, 15140), (done.Gross!.Value, done.Tare!.Value, done.Net!.Value));
        Assert.Equal(1, done.TicketNo);
        Assert.Empty(_db.OpenWeighings());

        var v = _db.FindVehicle("ABC-123/XYZ-063")!;
        Assert.Equal(11300, v.LastTare);
        Assert.Equal(done.DriverId, v.DefaultDriverId);
    }

    [Fact]
    public void Out_weighs_empty_first()
    {
        var open = _db.StartWeighing(Draft(Direction.Out), 9000, T0);
        var done = _db.CloseWeighing(open.Id, 30000, T0.AddMinutes(20));
        Assert.Equal((30000, 9000, 21000), (done.Gross!.Value, done.Tare!.Value, done.Net!.Value));
    }

    [Fact]
    public void Same_truck_cannot_enter_twice()
    {
        _db.StartWeighing(Draft(), 20000, T0);
        Assert.Throws<InvalidOperationException>(() => _db.StartWeighing(Draft(plate: "abc 123 xyz 063"), 20000, T0));
    }

    [Fact]
    public void Stored_tare_closes_in_one_weighing()
    {
        var first = _db.StartWeighing(Draft(), 25000, T0);
        _db.CloseWeighing(first.Id, 11000, T0.AddMinutes(5));

        var single = _db.SingleWeighing(Draft(), 24000, T0.AddHours(1));
        Assert.Equal(WeighingStatus.Done, single.Status);
        Assert.True(single.StoredTare);
        Assert.Equal((24000, 11000, 13000), (single.Gross!.Value, single.Tare!.Value, single.Net!.Value));
        Assert.Equal(2, single.TicketNo);
    }

    [Fact]
    public void Stored_tare_requires_a_known_tare() =>
        Assert.ThrowsAny<Exception>(() => _db.SingleWeighing(Draft(plate: "UJ-123"), 24000, T0));

    [Fact]
    public void Manual_tare_closes_in_one_weighing_and_keeps_the_stored_tare()
    {
        var first = _db.StartWeighing(Draft(), 25000, T0);
        _db.CloseWeighing(first.Id, 11000, T0.AddMinutes(5));

        var w = _db.ManualTareWeighing(Draft(Direction.Out), 30000, 12500, T0.AddHours(1));
        Assert.Equal(WeighingStatus.Done, w.Status);
        Assert.True(w.ManualTare);
        Assert.False(w.StoredTare);
        Assert.Equal((30000, 12500, 17500), (w.Gross!.Value, w.Tare!.Value, w.Net!.Value));
        Assert.Equal(2, w.TicketNo);
        Assert.Empty(_db.OpenWeighings());
        Assert.Equal(11000, _db.FindVehicle("ABC123/XYZ063")!.LastTare);   // a typed value is not a measurement
    }

    [Theory]
    [InlineData(12500, 12500)]   // not above the tare
    [InlineData(12500, 20000)]
    [InlineData(12500, 0)]       // no tare
    public void Manual_tare_must_be_valid(int weight, int tare)
    {
        Assert.ThrowsAny<Exception>(() => _db.ManualTareWeighing(Draft(Direction.Out), weight, tare, T0));
        Assert.Equal(0, _db.WeighingCount());
    }

    [Theory]
    [InlineData("12500", 12500)]
    [InlineData(" 12 300 kg ", 12300)]
    [InlineData("12\u00a0300KG", 12300)]
    [InlineData("", null)]
    [InlineData("0", null)]
    [InlineData("-5", null)]
    [InlineData("12,5", null)]
    [InlineData("abc", null)]
    public void Typed_tare_is_parsed(string text, int? expected) =>
        Assert.Equal(expected, WeighingMath.ParseTare(text));

    [Fact]
    public void Old_database_is_upgraded_and_keeps_its_data()
    {
        var w = _db.StartWeighing(Draft(), 25000, T0);
        var closed = _db.CloseWeighing(w.Id, 11000, T0.AddMinutes(5));
        var inYard = _db.StartWeighing(Draft(plate: "DEF-456"), 20000, T0);
        using (var c = Schema.Open(_path))
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "ALTER TABLE weighings DROP COLUMN manual_tare; PRAGMA user_version = 1;";
            cmd.ExecuteNonQuery();
        }
        _db.Dispose();

        using var old = TruckScaleDb.OpenOrCreate(_path);
        Assert.Equal(closed, old.GetWeighing(w.Id));
        Assert.Equal(inYard.Id, Assert.Single(old.OpenWeighings()).Id);
        Assert.True(old.ManualTareWeighing(Draft(Direction.Out, "GHI-789"), 30000, 12500, T0).ManualTare);
    }

    [Fact]
    public void Material_can_be_changed_at_the_second_weighing()
    {
        var open = _db.StartWeighing(Draft(Direction.Out), 9000, T0);
        var done = _db.CloseWeighing(open.Id, 30000, T0.AddMinutes(20), material: "Búza");
        Assert.Equal("Búza", done.MaterialName);
        Assert.NotEqual(open.MaterialId, done.MaterialId);
        Assert.Equal(2, _db.Materials().Count);
    }

    [Fact]
    public void Unchanged_material_keeps_the_snapshot()
    {
        var open = _db.StartWeighing(Draft(Direction.Out), 9000, T0);
        var done = _db.CloseWeighing(open.Id, 30000, T0.AddMinutes(20), material: "árpa");
        Assert.Equal("Árpa", done.MaterialName);
        Assert.Equal(open.MaterialId, done.MaterialId);
    }

    [Fact]
    public void Empty_material_blocks_the_second_weighing_and_leaves_it_open()
    {
        var open = _db.StartWeighing(Draft(Direction.Out), 9000, T0);
        Assert.Throws<ArgumentException>(() => _db.CloseWeighing(open.Id, 30000, T0.AddMinutes(20), material: "  "));
        Assert.Equal(WeighingStatus.Open, _db.GetWeighing(open.Id).Status);
    }

    [Fact]
    public void Storno_voids_without_ticket_number()
    {
        var open = _db.StartWeighing(Draft(), 20000, T0);
        _db.VoidWeighing(open.Id, T0.AddMinutes(1));
        var w = _db.GetWeighing(open.Id);
        Assert.Equal(WeighingStatus.Void, w.Status);
        Assert.Null(w.TicketNo);
        Assert.Throws<InvalidOperationException>(() => _db.CloseWeighing(open.Id, 10000, T0.AddMinutes(2)));
    }

    [Fact]
    public void Ticket_numbers_continue_from_setting()
    {
        _db.SetSetting("ticket.next_no", "417");
        var open = _db.StartWeighing(Draft(), 20000, T0);
        Assert.Equal(417, _db.CloseWeighing(open.Id, 10000, T0.AddMinutes(1)).TicketNo);
        Assert.Equal("418", _db.GetSetting("ticket.next_no"));
    }

    [Fact]
    public void Master_data_is_reused_case_and_accent_insensitive()
    {
        var a = _db.StartWeighing(Draft(), 20000, T0);
        _db.CloseWeighing(a.Id, 10000, T0.AddMinutes(1));
        _db.StartWeighing(new WeighingDraft(Direction.In, "teszt ugyfel", "DEF-456", "TESZT SOFŐR", "árpa", "x"), 20000, T0.AddHours(1));
        Assert.Single(_db.Partners());
        Assert.Single(_db.Drivers());
        Assert.Single(_db.Materials());
        Assert.Equal(2, _db.Vehicles().Count);
    }
}

public sealed class PartnerDetailsTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"ts-test-{Guid.NewGuid():N}.db");
    private readonly TruckScaleDb _db;
    public PartnerDetailsTests() => _db = TruckScaleDb.OpenOrCreate(_path);
    public void Dispose() { _db.Dispose(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); File.Delete(_path); }

    [Fact]
    public void Details_are_saved_for_new_and_updated_for_existing_partner()
    {
        var t = new DateTime(2026, 7, 1, 8, 0, 0);
        var w1 = _db.StartWeighing(new WeighingDraft(Direction.In, "Új Ügyfél", "AAA111", null, null, null,
            new PartnerDetails("Fő utca 1", "Falu", "1234", "első")), 20000, t);
        Assert.Equal("Fő utca 1, 1234 Falu", w1.PartnerAddress);
        var p = Assert.Single(_db.Partners());
        Assert.Equal(("1234", "Falu", "Fő utca 1", "első"), (p.Zip, p.Town, p.Street, p.Note));

        var w2 = _db.StartWeighing(new WeighingDraft(Direction.In, "új ügyfél", "BBB222", null, null, null,
            new PartnerDetails("Kossuth út 2", "Falu", "1234", null)), 20000, t);
        Assert.Equal("Kossuth út 2, 1234 Falu", w2.PartnerAddress);
        p = Assert.Single(_db.Partners());
        Assert.Equal(("Kossuth út 2", null), (p.Street, p.Note));
        Assert.Equal("Fő utca 1, 1234 Falu", _db.GetWeighing(w1.Id).PartnerAddress); // old snapshot unchanged
    }
}

public sealed class PartnerEditTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"ts-test-{Guid.NewGuid():N}.db");
    private readonly TruckScaleDb _db;
    public PartnerEditTests() => _db = TruckScaleDb.OpenOrCreate(_path);
    public void Dispose() { _db.Dispose(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); File.Delete(_path); }

    [Fact]
    public void Create_update_delete()
    {
        var a = _db.SavePartner(new Partner(0, "  Első  Kft ", "1234", "Falu", "Fő u. 1", null));
        Assert.NotEqual(0, a.Id);
        Assert.Equal("Első Kft", a.Name);
        _db.SavePartner(a with { Street = "Kossuth 2", Note = "x" });
        Assert.Equal("Kossuth 2", Assert.Single(_db.Partners()).Street);

        Assert.Throws<InvalidOperationException>(() => _db.SavePartner(new Partner(0, "első kft", null, null, null)));
        Assert.Throws<ArgumentException>(() => _db.SavePartner(new Partner(0, "  ", null, null, null)));

        _db.DeletePartner(a.Id);
        Assert.Empty(_db.Partners());
        // a deleted name can be used again
        _db.SavePartner(new Partner(0, "Első Kft", null, null, null));
    }

    [Fact]
    public void Cannot_delete_partner_with_truck_in_yard()
    {
        var w = _db.StartWeighing(new WeighingDraft(Direction.In, "Ügyfél", "AAA111", null, null, null), 20000, DateTime.Now);
        Assert.Throws<InvalidOperationException>(() => _db.DeletePartner(w.PartnerId!.Value));
        Assert.Equal(1, _db.PartnerUsage()[w.PartnerId!.Value].Count);
    }
}
