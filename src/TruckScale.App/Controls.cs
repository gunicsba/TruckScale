using TruckScale.Core.Localization;
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
            _status.Text = L.T("scale.noConnection");
            _status.Foreground = Brushes.Orange;
            return;
        }
        _weight.Text = r.Status == ScaleStatus.Overload ? "OL" : r.Weight.ToString("N0", AppState.Hu);
        (_status.Text, _status.Foreground) = r.Status switch
        {
            ScaleStatus.Stable => (L.T("scale.stable"), Brushes.LightGreen),
            ScaleStatus.Unstable => (L.T("scale.unstable"), Brushes.Yellow),
            _ => (L.T("scale.overload"), Brushes.Orange),
        };
    }
}

/// <summary>Small message / confirmation dialogs.</summary>
public static class Dialog
{
    public static Task Info(Window owner, string title, string message) => Show(owner, title, message, L.T("common.ok"));

    public static async Task<bool> Confirm(Window owner, string title, string message, string? yes = null, string? no = null)
    {
        yes ??= L.T("common.yes");
        return await Show(owner, title, message, yes, no ?? L.T("common.no")) == yes;
    }

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

public static class WindowFit
{
    /// <summary>Shrinks a window to the screen's working area (above the taskbar) and centres it.</summary>
    public static void ClampToScreen(Window w)
    {
        w.Opened += (_, _) =>
        {
            var screen = w.Screens.ScreenFromWindow(w) ?? w.Screens.Primary;
            if (screen is null) return;
            double scale = screen.Scaling;
            // Width/Height are the client area; the frame (title bar, borders) comes on top of it.
            var frame = w.FrameSize ?? new Size(w.Width + 16, w.Height + 40);
            double extraW = Math.Max(0, frame.Width - w.Width), extraH = Math.Max(0, frame.Height - w.Height);
            double maxW = screen.WorkingArea.Width / scale - extraW - 8, maxH = screen.WorkingArea.Height / scale - extraH - 8;
            if (w.Width <= maxW && w.Height <= maxH) return;
            w.Width = Math.Min(w.Width, maxW);
            w.Height = Math.Min(w.Height, maxH);
            w.Position = new PixelPoint(
                screen.WorkingArea.X + (int)((screen.WorkingArea.Width - (w.Width + extraW) * scale) / 2),
                screen.WorkingArea.Y + (int)((screen.WorkingArea.Height - (w.Height + extraH) * scale) / 2));
        };
    }
}
