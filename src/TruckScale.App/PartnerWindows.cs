using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using TruckScale.Core.Data;
using TruckScale.Core.Text;

namespace TruckScale.App;

/// <summary>Create or edit one customer. Returns the saved customer, or null if cancelled.</summary>
public sealed class PartnerEditWindow : Window
{
    private readonly AppState _state;
    private readonly Partner _original;
    private readonly TextBox _name = new(), _street = new() { PlaceholderText = "utca, házszám" }, _town = new(),
        _zip = new() { Width = 90 }, _note = new() { PlaceholderText = "nem kötelező" };
    private readonly TextBlock _error = new() { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap };

    public PartnerEditWindow(AppState state, Partner? partner, string? newName = null)
    {
        _state = state;
        _original = partner ?? new Partner(0, newName ?? "", null, null, null);
        Title = partner is null ? "Új ügyfél" : "Ügyfél szerkesztése";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        _name.Text = _original.Name;
        _street.Text = _original.Street;
        _town.Text = _original.Town;
        _zip.Text = _original.Zip;
        _note.Text = _original.Note;

        var grid = new Grid { ColumnDefinitions = new("110,*"), RowSpacing = 8 };
        void Row(string label, Control c)
        {
            int r = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var l = new TextBlock { Text = label, Classes = { "label" } };
            Grid.SetRow(l, r); Grid.SetRow(c, r); Grid.SetColumn(c, 1);
            grid.Children.Add(l); grid.Children.Add(c);
        }
        Row("Név", _name);
        Row("Utca", _street);
        var townRow = new Grid { ColumnDefinitions = new("*,Auto,Auto") };
        var zipLabel = new TextBlock { Text = "Ir.szám", Classes = { "label" }, Margin = new Thickness(12, 0, 8, 0) };
        Grid.SetColumn(zipLabel, 1); Grid.SetColumn(_zip, 2);
        townRow.Children.Add(_town); townRow.Children.Add(zipLabel); townRow.Children.Add(_zip);
        Row("Település", townRow);
        Row("Megjegyzés", _note);

        var save = new Button { Content = "Mentés", MinWidth = 100, IsDefault = true };
        var cancel = new Button { Content = "Mégse", MinWidth = 100, IsCancel = true };
        save.Click += (_, _) => Save();
        cancel.Click += (_, _) => Close(null);

        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 12,
            Children =
            {
                grid, _error,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, save } },
            },
        };
        Opened += (_, _) => _name.Focus();
    }

    private void Save()
    {
        try
        {
            var saved = _state.Db.SavePartner(_original with
            {
                Name = _name.Text ?? "", Street = _street.Text, Town = _town.Text, Zip = _zip.Text, Note = _note.Text,
            });
            Close(saved);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            _error.Text = ex.Message;
        }
    }
}

/// <summary>Adatállomány → Ügyfelek: search, create, edit, delete customers.</summary>
public sealed class PartnersWindow : Window
{
    private readonly AppState _state;
    private readonly TextBox _search = new() { PlaceholderText = "Keresés (név, település, utca)…" };
    private readonly DataGrid _grid = new()
    {
        IsReadOnly = true, SelectionMode = DataGridSelectionMode.Single, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
        CanUserResizeColumns = true,
    };
    private readonly TextBlock _count = new() { Classes = { "hint" }, VerticalAlignment = VerticalAlignment.Center };
    private List<Row> _all = [];

    public sealed record Row(Partner Partner, int Count, DateTime? Last)
    {
        public string Name => Partner.Name;
        public string? Zip => Partner.Zip;
        public string? Town => Partner.Town;
        public string? Street => Partner.Street;
        public string? Note => Partner.Note;
        public string LastText => AppState.When(Last);
    }

    public PartnersWindow(AppState state)
    {
        _state = state;
        Title = "Adatállomány – Ügyfelek";
        Width = 1000;
        Height = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        foreach (var (h, b, w) in new[]
                 {
                     ("Név", "Name", "2*"), ("Ir.szám", "Zip", "Auto"), ("Település", "Town", "*"), ("Utca", "Street", "*"),
                     ("Megjegyzés", "Note", "*"), ("Mérések", "Count", "Auto"), ("Utolsó mérés", "LastText", "Auto"),
                 })
            _grid.Columns.Add(new DataGridTextColumn { Header = h, Binding = new Avalonia.Data.Binding(b), Width = w == "Auto" ? DataGridLength.Auto : new DataGridLength(double.Parse(w.TrimEnd('*') is { Length: > 0 } n ? n : "1"), DataGridLengthUnitType.Star) });

        var add = new Button { Content = "Új ügyfél…" };
        var edit = new Button { Content = "Szerkesztés…" };
        var delete = new Button { Content = "Törlés", Foreground = Brushes.DarkRed };
        var close = new Button { Content = "Bezárás", IsCancel = true };
        add.Click += async (_, _) => { if (await new PartnerEditWindow(_state, null).ShowDialog<Partner?>(this) is { } p) Reload(p.Id); };
        edit.Click += async (_, _) => await Edit();
        delete.Click += async (_, _) => await Delete();
        close.Click += (_, _) => Close();
        _grid.DoubleTapped += async (_, _) => await Edit();
        _grid.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await Edit(); } };
        _search.TextChanged += (_, _) => Filter();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { add, edit, delete } };
        var bottom = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(close, Dock.Right);
        bottom.Children.Add(close);
        bottom.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Children = { buttons, _count } });

        var root = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(_search, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        _search.Margin = new Thickness(0, 0, 0, 8);
        root.Children.Add(_search);
        root.Children.Add(bottom);
        root.Children.Add(_grid);
        Content = root;

        Opened += (_, _) => _search.Focus();
        Reload(null);
    }

    private void Reload(long? select)
    {
        var usage = _state.Db.PartnerUsage();
        _all = _state.Db.Partners().OrderBy(p => p.Name, StringComparer.Create(AppState.Hu, true)).Select(p =>
        {
            var u = usage.GetValueOrDefault(p.Id);
            return new Row(p, u.Count, u.Last);
        }).ToList();
        Filter(select);
    }

    private void Filter(long? select = null)
    {
        var key = Normalize.NameKey(_search.Text);
        var rows = _all.Where(r => key.Length == 0 ||
            Normalize.NameKey($"{r.Name} {r.Town} {r.Street} {r.Zip} {r.Note}").Contains(key)).ToList();
        _grid.ItemsSource = rows;
        _count.Text = $"{rows.Count} / {_all.Count} ügyfél";
        if (select is long id && rows.FirstOrDefault(r => r.Partner.Id == id) is { } row)
        {
            _grid.SelectedItem = row;
            _grid.ScrollIntoView(row, null);
        }
    }

    private async Task Edit()
    {
        if (_grid.SelectedItem is not Row r) return;
        if (await new PartnerEditWindow(_state, r.Partner).ShowDialog<Partner?>(this) is { } p) Reload(p.Id);
    }

    private async Task Delete()
    {
        if (_grid.SelectedItem is not Row r) return;
        var msg = $"Törli a(z) „{r.Name}” ügyfelet a listából?" +
                  (r.Count > 0 ? $"\nA {r.Count} korábbi mérése és mérlegjegye megmarad." : "");
        if (!await Dialog.Confirm(this, "Törlés", msg, "Törlés", "Mégse")) return;
        try
        {
            _state.Db.DeletePartner(r.Partner.Id);
            Reload(null);
        }
        catch (InvalidOperationException ex)
        {
            await Dialog.Info(this, "Törlés", ex.Message);
        }
    }
}
