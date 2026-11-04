using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TruckScale.Core.Scale;

namespace TruckScale.App;

/// <summary>The big red weight display (like the old program), with the scale status below it.</summary>
public sealed class WeightDisplay : UserControl
{
    private readonly TextBlock _weight = new()
    {
        FontFamily = new FontFamily("Consolas, DejaVu Sans Mono, Menlo, monospace"),
        FontWeight = FontWeight.Bold,
        Foreground = Brushes.Red,
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly TextBlock _status = new()
    {
        Foreground = Brushes.White,
        FontSize = 13,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Bottom,
    };

    public WeightDisplay(double fontSize = 64)
    {
        _weight.FontSize = fontSize;
        Content = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#5a1414")),
            BorderBrush = new SolidColorBrush(Color.Parse("#c8a850")),
            BorderThickness = new Thickness(3),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(12, 4),
            Child = new Grid { Children = { _weight, _status } },
        };
        Update(null);
    }

    public void Update(ScaleReading? r)
    {
        if (r is null)
        {
            _weight.Text = "-------";
            _status.Text = "NINCS KAPCSOLAT";
            _status.Foreground = Brushes.Orange;
            return;
        }
        _weight.Text = r.Status == ScaleStatus.Overload ? "OL" : r.Weight.ToString("N0", AppState.Hu);
        (_status.Text, _status.Foreground) = r.Status switch
        {
            ScaleStatus.Stable => ("STABIL  kg", Brushes.LightGreen),
            ScaleStatus.Unstable => ("MOZGÁS  kg", Brushes.Yellow),
            _ => ("TÚLTERHELÉS", Brushes.Orange),
        };
    }
}

/// <summary>Small message / confirmation dialogs.</summary>
public static class Dialog
{
    public static Task Info(Window owner, string title, string message) => Show(owner, title, message, "OK");

    public static async Task<bool> Confirm(Window owner, string title, string message, string yes = "Igen", string no = "Nem") =>
        await Show(owner, title, message, yes, no) == yes;

    public static Task<string?> Show(Window owner, string title, string message, params string[] buttons)
    {
        var win = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            MinWidth = 320,
            MaxWidth = 600,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };
        var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        foreach (var b in buttons)
        {
            var btn = new Button { Content = b, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
            btn.Click += (_, _) => win.Close(b);
            panel.Children.Add(btn);
        }
        win.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 16,
            Children = { new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 15 }, panel },
        };
        return win.ShowDialog<string?>(owner);
    }
}
