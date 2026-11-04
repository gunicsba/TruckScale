using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using TruckScale.Core;
using TruckScale.Core.Data;
using TruckScale.Core.Scale;
using TruckScale.Core.Text;

namespace TruckScale.App;

/// <summary>
/// "Jármű érkezése": a new weighing (everything editable, pre-filled from the previous one), or
/// the departure of a truck in the yard (read-only, only the 2nd Mérés or Stornó).
/// </summary>
public partial class WeighingWindow : Window
{
    private readonly AppState _state = null!;
    private readonly Weighing? _open;          // set when this is the 2nd weighing of a truck in the yard
    private readonly WeightDisplay _weight = new(56);
    private readonly List<Partner> _partners = [];
    private readonly List<Vehicle> _vehicles = [];
    private readonly List<Driver> _drivers = [];
    private readonly List<Material> _materials = [];
    private bool _busy;
    private Weighing? _done;                   // set once the ticket is closed: only Nyomtatás / Bezárás remain
    private long? _detailsFor;                 // customer whose details are currently in the fields

    public WeighingWindow() => InitializeComponent(); // XAML designer only

    /// <summary>New weighing.</summary>
    public WeighingWindow(AppState state, Direction direction) : this(state, null, true)
    {
        Title = direction == Direction.In ? "Jármű érkezése – Beszállítás" : "Jármű érkezése – Kiszállítás";
        (direction == Direction.In ? DirIn : DirOut).IsChecked = true;

        // Pre-fill with the previous weighing; every field can be changed.
        if (state.Db.LastWeighing() is { } last)
        {
            PartnerBox.Text = last.PartnerName;
            PlateBox.Text = last.Plate;
            DriverBox.Text = last.DriverName;
            MaterialBox.Text = MaterialText(last);
        }
        UpdateInfo();
    }

    /// <summary>2nd weighing / Stornó of a truck in the yard.</summary>
    public WeighingWindow(AppState state, Weighing open) : this(state, open, true)
    {
        Title = $"Jármű távozása – {open.DirectionText}";
        (open.Direction == Direction.In ? DirIn : DirOut).IsChecked = true;
        PartnerBox.Text = open.PartnerName;
        PlateBox.Text = open.Plate;
        DriverBox.Text = open.DriverName;
        MaterialBox.Text = MaterialText(open);
        MarkingBox.Text = open.Marking;

        if (_partners.FirstOrDefault(p => p.Id == open.PartnerId) is { } partner) FillDetails(partner);
        PartnerNew.IsVisible = PartnerEdit.IsVisible = false;
        foreach (var c in new Control[] { DirIn, DirOut, PartnerBox, PartnerList,
                     PlateBox, PlateList, DriverBox, DriverList, MaterialBox, MaterialList, UseStoredTare, MarkingBox })
            c.IsEnabled = false;
        UseStoredTare.IsVisible = false;
        FirstPanel.IsVisible = true;
        FirstInfo.Text = $"{AppState.Kg(open.FirstWeight)}   {AppState.When(open.FirstAt)}";
        WeightTitle.Text = "2. mérés";
        VoidButton.IsVisible = true;
        CancelButton.Content = "Bezárás";
        UpdateInfo();
    }

    private WeighingWindow(AppState state, Weighing? open, bool _)
    {
        _state = state;
        _open = open;
        InitializeComponent();

        WeightHost.Child = _weight;
        state.ReadingUpdated += OnReading;
        Closed += (_, _) => state.ReadingUpdated -= OnReading;

        _partners.AddRange(state.Db.Partners().OrderBy(p => p.Name, StringComparer.Create(AppState.Hu, true)));
        _vehicles.AddRange(state.Db.Vehicles());
        _drivers.AddRange(state.Db.Drivers());
        _materials.AddRange(state.Db.Materials());
        Setup(PartnerBox, PartnerList, _partners);
        Setup(PlateBox, PlateList, _vehicles);
        Setup(DriverBox, DriverList, _drivers);
        Setup(MaterialBox, MaterialList, _materials);

        PartnerBox.TextChanged += (_, _) => UpdateInfo();
        PlateBox.TextChanged += (_, _) => UpdateInfo();
        MaterialBox.TextChanged += (_, _) => UpdateInfo();
        UseStoredTare.IsCheckedChanged += (_, _) => UpdateHint();
        // Picking a plate from the list suggests its usual driver.
        PlateBox.SelectionChanged += (_, _) =>
        {
            if (_open is null && PlateBox.SelectedItem is Vehicle { DefaultDriverId: long d } && _drivers.FirstOrDefault(x => x.Id == d) is { } drv)
                DriverBox.Text = drv.FullName;
        };

        PartnerNew.Click += async (_, _) =>
        {
            var typed = CurrentPartner is null ? Normalize.CleanOrNull(PartnerBox.Text) : null;
            if (await new PartnerEditWindow(_state, null, typed).ShowDialog<Partner?>(this) is { } p) UsePartner(p);
        };
        PartnerEdit.Click += async (_, _) =>
        {
            if (CurrentPartner is { } cur && await new PartnerEditWindow(_state, cur).ShowDialog<Partner?>(this) is { } p) UsePartner(p);
        };
        PartnerNew.IsTabStop = PartnerEdit.IsTabStop = false;
        Opened += (_, _) => { if (_open is null) PartnerBox.Focus(); };
        WeighButton.Click += OnWeigh;
        PrintButton.Click += OnPrint;
        VoidButton.Click += OnVoid;
        CancelButton.Click += (_, _) => Close();
        OnReading();
    }

    /// <summary>After creating/editing a customer: refresh the list and select it.</summary>
    private void UsePartner(Partner p)
    {
        _partners.Clear();
        _partners.AddRange(_state.Db.Partners().OrderBy(p => p.Name, StringComparer.Create(AppState.Hu, true)));
        PartnerBox.ItemsSource = null;
        PartnerBox.ItemsSource = _partners;
        _detailsFor = null;
        PartnerBox.Text = p.Name;
        UpdateInfo();
    }

    private void FillDetails(Partner? p)
    {
        StreetBox.Text = p?.Street;
        TownBox.Text = p?.Town;
        ZipBox.Text = p?.Zip;
        PartnerNoteBox.Text = p?.Note;
        _detailsFor = p?.Id;
    }

    private static string? MaterialText(Weighing w) =>
        string.IsNullOrWhiteSpace(w.MaterialNote) ? w.MaterialName : $"{w.MaterialName} ({w.MaterialNote})";

    /// <summary>Accent- and case-insensitive "contains" search; the ▾ button lists everything.</summary>
    private static void Setup<T>(AutoCompleteBox box, Button listButton, List<T> items) where T : notnull
    {
        box.ItemsSource = items;
        box.FilterMode = AutoCompleteFilterMode.Custom;
        box.MinimumPrefixLength = 1;
        box.MaxDropDownHeight = 320;
        box.ItemFilter = (search, item) =>
            string.IsNullOrEmpty(search) || Normalize.NameKey(item?.ToString()).Contains(Normalize.NameKey(search));

        // Tab goes from field to field; the ▾ button is for the mouse.
        listButton.IsTabStop = false;

        // ↑/↓ (with the list closed) steps to the previous/next item.
        box.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (box.IsDropDownOpen || e.Key is not (Key.Up or Key.Down) || items.Count == 0) return;
            var key = Normalize.NameKey(box.Text);
            int i = items.FindIndex(x => Normalize.NameKey(x.ToString()) == key);
            if (i < 0) i = items.FindIndex(x => Normalize.NameKey(x.ToString()).StartsWith(key)) - (e.Key == Key.Down ? 1 : 0);
            int next = Math.Clamp(i < 0 && e.Key == Key.Up ? 0 : i + (e.Key == Key.Down ? 1 : -1), 0, items.Count - 1);
            box.Text = items[next].ToString();
            box.SelectedItem = items[next];
            box.IsDropDownOpen = false;
            box.CaretIndex = box.Text?.Length ?? 0;
            e.Handled = true;
        }, RoutingStrategies.Tunnel);

        string? before = null;
        listButton.Click += (_, _) =>
        {
            before = box.Text;
            box.MinimumPrefixLength = 0;
            box.Text = "";
            box.Focus();
            box.IsDropDownOpen = true;
        };
        box.DropDownClosed += (_, _) =>
        {
            box.MinimumPrefixLength = 1;
            if (string.IsNullOrEmpty(box.Text) && before is not null) box.Text = before;
            before = null;
        };
    }

    private Partner? CurrentPartner =>
        _partners.FirstOrDefault(p => Normalize.NameKey(p.Name) == Normalize.NameKey(PartnerBox.Text));

    private Vehicle? CurrentVehicle
    {
        get
        {
            var key = Normalize.PlateKey(PlateBox.Text);
            return key.Length == 0 ? null : _vehicles.FirstOrDefault(v => Normalize.PlateKey(v.Plate) == key);
        }
    }

    private void UpdateInfo()
    {
        if (_open is not null)
        {
            PartnerInfo.Text = "";
            TareInfo.Text = "";
            MaterialInfo.Text = "";
            UpdateHint();
            return;
        }

        var p = CurrentPartner;
        // Switching to another known customer loads its details; switching to a new name clears them.
        if (p is not null && p.Id != _detailsFor) FillDetails(p);
        else if (p is null && _detailsFor is not null) { FillDetails(null); }
        PartnerInfo.Text = p is null && !string.IsNullOrWhiteSpace(PartnerBox.Text)
            ? "Nincs ilyen ügyfél – válasszon a listából, vagy hozza létre: Új…"
            : "";
        PartnerEdit.IsEnabled = p is not null;

        var v = CurrentVehicle;
        if (v?.LastTare is int tare)
        {
            TareInfo.Text = $"Tárolt tára: {AppState.Kg(tare)}" + (v.LastTareAt is { } at ? $" ({at:yyyy.MM.dd.})" : "");
            UseStoredTare.IsEnabled = true;
        }
        else
        {
            TareInfo.Text = v is null && !string.IsNullOrWhiteSpace(PlateBox.Text) ? "Új rendszám – nincs tárolt tára." : "Nincs tárolt tára.";
            UseStoredTare.IsChecked = false;
            UseStoredTare.IsEnabled = false;
        }

        var m = _materials.FirstOrDefault(x => Normalize.NameKey(x.Display) == Normalize.NameKey(MaterialBox.Text));
        MaterialInfo.Text = m?.Comment is { } c ? $"Megjegyzés: {c}" : "";
        UpdateHint();
    }

    private void OnReading()
    {
        _weight.Update(_state.Reading);
        UpdateHint();
    }

    /// <summary>Explains why Mérés is not possible right now (or what it will do).</summary>
    private void UpdateHint()
    {
        var problem = Problem();
        if (_done is not null) { WeighButton.IsEnabled = false; return; }
        HintText.Text = problem ?? (_open is null && UseStoredTare.IsChecked == true
            ? $"Egy mérés: nettó = mért súly − {AppState.Kg(CurrentVehicle?.LastTare)}"
            : "");
        WeighButton.IsEnabled = problem is null && !_busy;
    }

    private string? Problem()
    {
        if (_done is not null) return "";
        var r = _state.Reading;
        if (r is null) return "Nincs kapcsolat a mérleggel.";
        if (r.Status == ScaleStatus.Overload) return "Túlterhelés.";
        if (!r.IsStable) return "A mérleg nem stabil – várjon.";
        if (r.Weight <= 0) return "A mérleg üres.";
        if (_open is not null) return null;
        if (string.IsNullOrWhiteSpace(PartnerBox.Text)) return "Válassza ki az ügyfelet.";
        if (CurrentPartner is null) return "Ismeretlen ügyfél – válasszon a listából, vagy hozza létre (Új…).";
        if (Normalize.PlateKey(PlateBox.Text).Length == 0) return "Adja meg a rendszámot.";
        if (UseStoredTare.IsChecked == true && CurrentVehicle?.LastTare is int t && r.Weight <= t)
            return $"A mért súly nem több, mint a tárolt tára ({AppState.Kg(t)}).";
        return null;
    }

    private async void OnWeigh(object? sender, RoutedEventArgs e)
    {
        if (_busy || Problem() is not null) return;
        int weight = _state.Reading!.Weight;
        var now = DateTime.Now;
        _busy = true;
        UpdateHint();
        try
        {
            if (_open is not null)
            {
                if (Math.Abs(weight - (_open.FirstWeight ?? 0)) < 200 &&
                    !await Dialog.Confirm(this, "Mérés", $"A két mérés szinte azonos ({AppState.Kg(_open.FirstWeight)} / {AppState.Kg(weight)}).\nBiztosan lezárja?"))
                    return;
                ShowDone(_state.Db.CloseWeighing(_open.Id, weight, now));
                return;
            }

            var draft = new WeighingDraft(
                DirIn.IsChecked == true ? Direction.In : Direction.Out,
                CurrentPartner!.Name, PlateBox.Text ?? "", DriverBox.Text, MaterialBox.Text, MarkingBox.Text);

            if (UseStoredTare.IsChecked == true)
            {
                ShowDone(_state.Db.SingleWeighing(draft, weight, now));
                return;
            }
            else
            {
                var w = _state.Db.StartWeighing(draft, weight, now);
                await Dialog.Info(this, "1. mérés", $"{w.Plate}: {AppState.Kg(weight)} rögzítve.\nA jármű az udvaron van, távozáskor válassza ki a listából.");
            }
            Close();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            await Dialog.Info(this, "Mérés", ex.Message);
        }
        finally
        {
            _busy = false;
            UpdateHint();
        }
    }

    /// <summary>The ticket is closed: show the result; only Nyomtatás and Bezárás remain.</summary>
    private void ShowDone(Weighing w)
    {
        _done = w;
        foreach (var c in new Control[] { DirIn, DirOut, PartnerBox, PartnerList, PartnerNew, PartnerEdit, PlateBox, PlateList,
                     DriverBox, DriverList, MaterialBox, MaterialList, UseStoredTare, MarkingBox })
            c.IsEnabled = false;
        VoidButton.IsVisible = false;
        CancelButton.Content = "Bezárás";
        Title = $"Mérlegjegy {w.TicketNo} – {w.DirectionText}";
        ResultText.Text = $"Mérlegjegy: {w.TicketNo}\n" +
                          $"Bruttó: {AppState.Kg(w.Gross),12}\n" +
                          $"Tára:   {AppState.Kg(w.Tare),12}{(w.StoredTare ? " (tárolt)" : "")}\n" +
                          $"Nettó:  {AppState.Kg(w.Net),12}";
        ResultPanel.IsVisible = true;
        PrintButton.IsEnabled = true;
        PrintButton.IsDefault = true;
        WeighButton.IsDefault = false;
        UpdateHint();
        PrintButton.Focus();
    }

    private async void OnPrint(object? sender, RoutedEventArgs e)
    {
        if (_done is null) return;
        if (TicketService.Print(_state, _done) is { } err) await Dialog.Info(this, "Nyomtatás", err);
        else HintText.Text = $"Mérlegjegy {_done.TicketNo} elküldve a nyomtatóra.";
    }

    private async void OnVoid(object? sender, RoutedEventArgs e)
    {
        if (_open is null) return;
        if (!await Dialog.Confirm(this, "Stornó",
                $"Stornózza a(z) {_open.Plate} mérését?\nA rekord megmarad „Stornó” állapotban, mérlegjegy nem készül.",
                "Stornó", "Mégse"))
            return;
        _state.Db.VoidWeighing(_open.Id, DateTime.Now);
        Close();
    }
}
