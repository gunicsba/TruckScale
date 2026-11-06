using TruckScale.Core.Localization;
namespace TruckScale.Core.Data;

public enum Direction { In, Out }

public enum WeighingStatus { Open, Done, Void }

public sealed record Partner(long Id, string Name, string? Zip, string? Town, string? Street, string? Note = null)
{
    public string Address => string.Join(", ", new[] { Street, string.Join(' ', new[] { Zip, Town }.Where(s => !string.IsNullOrWhiteSpace(s))) }
        .Where(s => !string.IsNullOrWhiteSpace(s)));
    public override string ToString() => Name;
}

public sealed record Vehicle(long Id, string Plate, int? LastTare, DateTime? LastTareAt, long? DefaultDriverId)
{
    public override string ToString() => Plate;
}

public sealed record Driver(long Id, string FamilyName, string? GivenName)
{
    public string FullName => string.IsNullOrWhiteSpace(GivenName) ? FamilyName : $"{FamilyName} {GivenName}";
    public override string ToString() => FullName;
}

public sealed record Material(long Id, string Name, string? Note, string? Comment)
{
    public string Display => string.IsNullOrWhiteSpace(Note) ? Name : $"{Name} ({Note})";
    public override string ToString() => Display;
}

/// <summary>Customer details as edited on the arrival screen; saved to the customer on Mérés.</summary>
public sealed record PartnerDetails(string? Street, string? Town, string? Zip, string? Note);

/// <summary>What the operator chose on the arrival screen before the first Mérés.</summary>
public sealed record WeighingDraft(
    Direction Direction,
    string PartnerName,
    string Plate,
    string? DriverName,
    string? MaterialName,
    string? Marking,
    PartnerDetails? Partner = null,
    string? Note = null);

public sealed record Weighing
{
    public long Id { get; init; }
    public int? TicketNo { get; init; }
    public Direction Direction { get; init; }
    public WeighingStatus Status { get; init; }
    public long? PartnerId { get; init; }
    public long? VehicleId { get; init; }
    public long? DriverId { get; init; }
    public long? MaterialId { get; init; }
    public string? PartnerName { get; init; }
    public string? PartnerAddress { get; init; }
    public string? Plate { get; init; }
    public string? DriverName { get; init; }
    public string? MaterialName { get; init; }
    public string? MaterialNote { get; init; }
    public string? Marking { get; init; }
    public int? FirstWeight { get; init; }
    public DateTime? FirstAt { get; init; }
    public int? SecondWeight { get; init; }
    public DateTime? SecondAt { get; init; }
    public DateTime? ClosedAt { get; init; }
    public int? Gross { get; init; }
    public int? Tare { get; init; }
    public int? Net { get; init; }
    public bool StoredTare { get; init; }
    /// <summary>The tare was typed in by hand, not measured (shown on the ticket).</summary>
    public bool ManualTare { get; init; }
    /// <summary>Note of this weighing (editable at both weighings, printed on the ticket).</summary>
    public string? Note { get; init; }

    /// <summary>The material as shown on the arrival screen: "Name (note)".</summary>
    public string? MaterialDisplay => string.IsNullOrWhiteSpace(MaterialNote) ? MaterialName : $"{MaterialName} ({MaterialNote})";
    public string DirectionText => L.T(Direction == Direction.In ? "dir.in" : "dir.out");
    public string StatusText => L.T(Status switch { WeighingStatus.Open => "status.open", WeighingStatus.Void => "status.void", _ => "status.done" });
    public string FirstAtText => L.DateTime(FirstAt);
    public string ClosedAtText => L.DateTime(ClosedAt);
    public string ClosedTimeText => L.Time(ClosedAt);
}
