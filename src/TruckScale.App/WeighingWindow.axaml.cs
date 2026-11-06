using TruckScale.Core.Localization;
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
        Title = L.T(direction == Direction.In ? "w.titleIn" : "w.titleOut");
        (direction == Direction.In ? DirIn : DirOut).IsChecked = true;

        // Pre-fill with the previous weighing; every field can be changed.
        if (state.Db.LastWeighing() is { } last)
        {
            PartnerBox.Text = last.PartnerName;
            PlateBox.Text = last.Plate;
            DriverBox.Text = last.DriverName;
            MaterialBox.Text = last.MaterialDisplay;
        }
        UpdateInfo();
    }

    /// <summary>2nd weighing / Stornó of a truck in the yard.</summary>
    public WeighingWindow(AppState state, Weighing open) : this(state, open, true)
    {
        Title = L.F("w.titleLeave", open.DirectionText);
        (open.Direction == Direction.In ? DirIn : DirOut).IsChecked = true;
        PartnerBox.Text = open.PartnerName;
        PlateBox.Text = open.Plate;
        DriverBox.Text = open.DriverName;
        MaterialBox.Text = open.MaterialDisplay;
        MarkingBox.Text = open.Marking;
        NoteBox.Text = open.Note;

        if (_partners.FirstOrDefault(p => p.Id == open.PartnerId) is { } partner)
        {
            FillDetails(partner);
            PartnerInfo.Text = string.IsNullOrWhiteSpace(partner.Note) ? "" : L.F("w.partnerNote", partner.Note);
        }
        PartnerNew.IsVisible = PartnerEdit.IsVisible = false;
        foreach (var c in new Control[] { DirIn, DirOut, PartnerBox, PartnerList,
                     PlateBox, PlateList, DriverBox, DriverList, UseStoredTare, MarkingBox })
            c.IsEnabled = false;
        // Kiszállítás: what was loaded is often only settled by the 2nd weighing.
        MaterialBox.IsEnabled = MaterialList.IsEnabled = MaterialEditable;
        UseStoredTare.IsVisible = false;
        FirstPanel.IsVisible = true;
        FirstInfo.Text = $"{AppState.Kg(open.FirstWeight)}   {AppState.When(open.FirstAt)}";
        WeightTitle.Text = L.T("w.second");
        VoidButton.IsVisible = true;
        CancelButton.Content = L.T("common.close");
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
        UseStoredTare.IsCheckedChanged += (_, _) =>
        {
            if (UseStoredTare.IsChecked == true) UseManualTare.IsChecked = false;
            UpdateHint();
        };
        UseManualTare.IsCheckedChanged += (_, _) =>
        {
            ManualTareBox.IsEnabled = UseManualTare.IsChecked == true;
            if (ManualTareBox.IsEnabled)
            {
                UseStoredTare.IsChecked = false;
                ManualTareBox.Focus();
            }
            UpdateHint();
        };
        ManualTareBox.TextChanged += (_, _) => UpdateHint();
        // Manual tare is for Kiszállítás only, and not for a truck already in the yard (its tare is the 1st weighing).
        DirOut.IsCheckedChanged += (_, _) =>
        {
            ManualTarePanel.IsVisible = _open is null && DirOut.IsChecked == true;
            if (!ManualTarePanel.IsVisible) UseManualTare.IsChecked = false;
        };
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
        _detailsFor = p?.Id;
    }

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
            TareInfo.Text = "";
            UpdateMaterialInfo();
            UpdateHint();
            return;
        }

        var p = CurrentPartner;
        // Switching to another known customer loads its details; switching to a new name clears them.
        if (p is not null && p.Id != _detailsFor) FillDetails(p);
        else if (p is null && _detailsFor is not null) { FillDetails(null); }
        PartnerInfo.Text = p is null
            ? (string.IsNullOrWhiteSpace(PartnerBox.Text) ? "" : L.T("w.unknownPartnerInfo"))
            : (string.IsNullOrWhiteSpace(p.Note) ? "" : L.F("w.partnerNote", p.Note));
        PartnerEdit.IsEnabled = p is not null;

        var v = CurrentVehicle;
        if (v?.LastTare is int tare)
        {
            TareInfo.Text = L.F("w.storedTare", AppState.Kg(tare)) + (v.LastTareAt is { } at ? $" ({L.Date(at)})" : "");
            UseStoredTare.IsEnabled = true;
        }
        else
        {
            TareInfo.Text = L.T(v is null && !string.IsNullOrWhiteSpace(PlateBox.Text) ? "w.newPlate" : "w.noTare");
            UseStoredTare.IsChecked = false;
            UseStoredTare.IsEnabled = false;
        }

        UpdateMaterialInfo();
        UpdateHint();
    }

    private void UpdateMaterialInfo()
    {
        var m = _materials.FirstOrDefault(x => Normalize.NameKey(x.Display) == Normalize.NameKey(MaterialBox.Text));
        MaterialInfo.Text = m?.Comment is { } c ? L.F("w.materialComment", c) : "";
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
        UpdateNetPreview();
        if (_done is not null)
        {
            WeighButton.IsEnabled = false;
            PrintButton.Content = L.T("common.print");
            PrintButton.IsEnabled = !_busy;
            return;
        }
        HintText.Text = problem ?? (OneWeighingTare is int tare ? L.F("w.oneWeighing", AppState.Kg(tare)) : "");
        WeighButton.IsEnabled = problem is null && !_busy;
        // When the ticket gets closed, "Mérés + nyomtatás" does everything in one go (and is the Enter key).
        PrintButton.Content = L.T("w.weighPrint");
        PrintButton.IsEnabled = WeighButton.IsEnabled && ClosesTicket;
        PrintButton.IsDefault = ClosesTicket;
        WeighButton.IsDefault = !ClosesTicket;
    }

    /// <summary>
    /// The net weight that Mérés would give now: 2nd weighing = difference to the 1st weight,
    /// "Használ" / "Kézi tára" = reading minus that tare. Not shown at a plain 1st weighing or after closing.
    /// </summary>
    private void UpdateNetPreview()
    {
        int? net = null;
        if (_done is null && _state.Reading is { } r && r.Status != ScaleStatus.Overload)
        {
            if (_open is not null) net = Math.Abs(r.Weight - (_open.FirstWeight ?? 0));
            else if (OneWeighingTare is int t) net = r.Weight - t;
        }
        NetPreview.IsVisible = net is not null;
        NetPreview.Text = net is null ? "" : L.F("w.netPreview", AppState.Kg(net));
        NetPreview.Opacity = _state.Reading?.IsStable == true ? 1 : 0.5;   // not stable yet: greyed
    }

    private string? Problem()
    {
        if (_done is not null) return "";
        var r = _state.Reading;
        if (r is null) return L.T("w.p.noConnection");
        if (r.Status == ScaleStatus.Overload) return L.T("w.p.overload");
        if (!r.IsStable) return L.T("w.p.unstable");
        if (r.Weight < 0) return L.T("w.p.negative");   // 0 kg is allowed (e.g. bagged goods), with a question
        if (_open is not null) return MaterialEditable && string.IsNullOrWhiteSpace(MaterialBox.Text) ? L.T("w.p.material") : null;
        if (string.IsNullOrWhiteSpace(PartnerBox.Text)) return L.T("w.p.choosePartner");
        if (CurrentPartner is null) return L.T("w.p.unknownPartner");
        if (Normalize.PlateKey(PlateBox.Text).Length == 0) return L.T("w.p.plate");
        if (string.IsNullOrWhiteSpace(MaterialBox.Text)) return L.T("w.p.material");
        if (UseStoredTare.IsChecked == true && CurrentVehicle?.LastTare is int t && r.Weight <= t)
            return L.F("w.p.belowTare", AppState.Kg(t));
        if (UseManualTare.IsChecked == true)
        {
            if (ManualTare is not int mt) return L.T("w.p.manualTare");
            if (r.Weight <= mt) return L.F("w.p.belowManualTare", AppState.Kg(mt));
        }
        return null;
    }

    private async void OnWeigh(object? sender, RoutedEventArgs e)
    {
        if (await Weigh() is { } done) ShowDone(done);
    }

    /// <summary>
    /// Mérés. Returns the closed weighing (2nd weighing, "Használ" or "Kézi tára"), or null if nothing was closed
    /// (1st weighing: the window closes; cancelled or failed: the window stays).
    /// </summary>
    private async Task<Weighing?> Weigh()
    {
        if (_busy || Problem() is not null) return null;
        int weight = _state.Reading!.Weight;
        var now = DateTime.Now;
        _busy = true;
        UpdateHint();
        try
        {
            if (weight == 0 && !await Dialog.Confirm(this, L.T("w.weigh"), L.T("w.zero")))
                return null;
            if (_open is not null)
            {
                if (Math.Abs(weight - (_open.FirstWeight ?? 0)) < 200 &&
                    !await Dialog.Confirm(this, L.T("w.weigh"), L.F("w.similar", AppState.Kg(_open.FirstWeight), AppState.Kg(weight))))
                    return null;
                return _state.Db.CloseWeighing(_open.Id, weight, now, NoteBox.Text ?? "", MaterialEditable ? MaterialBox.Text : null);
            }

            var draft = new WeighingDraft(
                DirIn.IsChecked == true ? Direction.In : Direction.Out,
                CurrentPartner!.Name, PlateBox.Text ?? "", DriverBox.Text, MaterialBox.Text, MarkingBox.Text, Note: NoteBox.Text);

            if (UseStoredTare.IsChecked == true)
                return _state.Db.SingleWeighing(draft, weight, now);
            if (UseManualTare.IsChecked == true)
                return _state.Db.ManualTareWeighing(draft, weight, ManualTare!.Value, now);

            var w = _state.Db.StartWeighing(draft, weight, now);
            await Dialog.Info(this, L.T("w.first"), L.F("w.firstRecorded", w.Plate, AppState.Kg(weight)));
            Close();
            return null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            await Dialog.Info(this, L.T("w.weigh"), ex.Message);
            return null;
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
                     DriverBox, DriverList, MaterialBox, MaterialList, UseStoredTare, UseManualTare, ManualTareBox, MarkingBox, NoteBox })
            c.IsEnabled = false;
        VoidButton.IsVisible = false;
        CancelButton.Content = L.T("common.close");
        Title = L.F("w.titleTicket", w.TicketNo, w.DirectionText);
        ResultText.Text = L.F("w.result", w.TicketNo, AppState.Kg(w.Gross), AppState.Kg(w.Tare),
            w.StoredTare ? L.T("w.resultStored") : w.ManualTare ? L.T("w.resultManual") : "", AppState.Kg(w.Net));
        ResultPanel.IsVisible = true;
        PrintButton.IsEnabled = true;
        PrintButton.IsDefault = true;
        WeighButton.IsDefault = false;
        UpdateHint();
        PrintButton.Focus();
    }

    /// <summary>"Mérés + nyomtatás": closes the ticket (if not yet), prints it and closes the window.</summary>
    private async void OnPrint(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var done = _done ?? await Weigh();
        if (done is null) return;
        // Show the result (ticket number, net weight) right away; the window closes when printing is done.
        if (_done is null) ShowDone(done);
        _busy = true;
        UpdateHint();
        HintText.Foreground = Avalonia.Media.Brushes.DarkGreen;
        HintText.Text = L.F("w.printing", done.TicketNo);
        var err = await TicketService.PrintAsync(_state, done);
        _busy = false;
        if (err is not null)
        {
            HintText.Text = "";
            UpdateHint();
            await Dialog.Info(this, L.T("common.print"), L.F("w.printFailed", err));
            return;
        }
        Close();
    }

    /// <summary>Closing the ticket (2nd weighing, "Használ" or "Kézi tára") – then printing makes sense.</summary>
    private bool ClosesTicket => _open is not null || UseStoredTare.IsChecked == true || UseManualTare.IsChecked == true;

    /// <summary>2nd weighing of a Kiszállítás: the material can still be changed, and must be given.</summary>
    private bool MaterialEditable => _open?.Direction == Direction.Out;

    private int? ManualTare => WeighingMath.ParseTare(ManualTareBox.Text);

    /// <summary>The tare of a weighing closed in one step ("Használ" or "Kézi tára"), if already known.</summary>
    private int? OneWeighingTare =>
        _open is not null ? null
        : UseStoredTare.IsChecked == true ? CurrentVehicle?.LastTare
        : UseManualTare.IsChecked == true ? ManualTare
        : null;

    private async void OnVoid(object? sender, RoutedEventArgs e)
    {
        if (_open is null) return;
        if (!await Dialog.Confirm(this, L.T("status.void"), L.F("w.voidConfirm", _open.Plate),
                L.T("status.void"), L.T("common.cancel")))
            return;
        _state.Db.VoidWeighing(_open.Id, DateTime.Now);
        Close();
    }
}
