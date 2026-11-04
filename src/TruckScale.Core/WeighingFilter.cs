using TruckScale.Core.Localization;
using System.Globalization;
using TruckScale.Core.Data;
using TruckScale.Core.Text;

namespace TruckScale.Core;

/// <summary>
/// Per-column text filter (Napló, Áttekintés). Every non-empty field must match ("contains",
/// ignoring case and accents; plates also ignore spaces/dashes). Date matches the "yyyy.MM.dd. HH:mm" text.
/// </summary>
public sealed record WeighingFilter(
    string? Ticket = null, string? Date = null, Direction? Direction = null, string? Plate = null,
    string? Partner = null, string? Driver = null, string? Material = null, string? Marking = null)
{
    public bool IsEmpty => this == new WeighingFilter();

    public bool Matches(Weighing w) =>
        (Direction is null || w.Direction == Direction) &&
        Has(Ticket, w.TicketNo?.ToString(CultureInfo.InvariantCulture)) &&
        Has(Date, $"{w.ClosedAt?.ToString("yyyy.MM.dd. HH:mm", CultureInfo.InvariantCulture)} {w.ClosedAt?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} {L.DateTime(w.ClosedAt)}") &&
        (string.IsNullOrWhiteSpace(Plate) || Normalize.PlateKey(w.Plate).Contains(Normalize.PlateKey(Plate))) &&
        Has(Partner, w.PartnerName) &&
        Has(Driver, w.DriverName) &&
        Has(Material, $"{w.MaterialName} {w.MaterialNote}") &&
        Has(Marking, w.Marking);

    private static bool Has(string? search, string? value) =>
        string.IsNullOrWhiteSpace(search) || Normalize.NameKey(value).Contains(Normalize.NameKey(search));
}
