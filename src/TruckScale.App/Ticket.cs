using System.Drawing.Printing;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using TruckScale.Core.Data;
using TruckScale.Core.Ticket;

namespace TruckScale.App;

/// <summary>Builds the ticket of a weighing from the configured .form file and prints it.</summary>
public static class TicketService
{
    /// <summary>The configured form file, searched next to the database, then next to the program.</summary>
    public static (List<FormField> Form, string Source) LoadForm(AppState state)
    {
        var name = state.Db.GetSetting("ticket.form");
        if (!string.IsNullOrWhiteSpace(name))
        {
            var candidates = System.IO.Path.IsPathRooted(name)
                ? [name]
                : new[]
                {
                    System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(state.Db.Path))!, name),
                    System.IO.Path.Combine(AppContext.BaseDirectory, name),
                    System.IO.Path.GetFullPath(name),
                };
            foreach (var path in candidates)
                if (File.Exists(path)) return (TicketForm.Load(path), path);
        }
        return (TicketForm.Parse(TicketForm.DefaultForm), "beépített minta");
    }

    public static List<TicketItem> Build(AppState state, Weighing w) =>
        TicketForm.Layout(LoadForm(state).Form, w, state.Db.AllSettings());

    public static string? PrinterName(AppState state) => state.Db.GetSetting("printer.name");

    /// <summary>Prints the ticket. Returns an error message, or null on success.</summary>
    public static string? Print(AppState state, Weighing w)
    {
        if (!OperatingSystem.IsWindows())
            return "A közvetlen nyomtatás egyelőre csak Windows alatt működik.";
        try
        {
            PrintWindows(Build(state, w), PrinterName(state), state.Db.GetSetting("printer.font_name") ?? "Arial", $"Mérlegjegy {w.TicketNo}");
            return null;
        }
        catch (Exception ex)
        {
            return "Nyomtatási hiba: " + ex.Message;
        }
    }

    [SupportedOSPlatform("windows")]
    private static void PrintWindows(List<TicketItem> items, string? printer, string fontName, string docName)
    {
        using var doc = new PrintDocument { DocumentName = docName, PrintController = new StandardPrintController() };
        if (!string.IsNullOrWhiteSpace(printer))
        {
            if (!PrinterSettings.InstalledPrinters.Cast<string>().Contains(printer, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException($"A(z) „{printer}” nyomtató nem található.");
            doc.PrinterSettings.PrinterName = printer;
        }
        doc.PrintPage += (_, e) =>
        {
            var g = e.Graphics!;
            g.PageUnit = System.Drawing.GraphicsUnit.Millimeter;
            // Form coordinates are from the paper edge; the printer's origin is at its hard margin.
            g.TranslateTransform(-(float)(e.PageSettings.HardMarginX * 0.254), -(float)(e.PageSettings.HardMarginY * 0.254));
            foreach (var item in items)
            {
                switch (item)
                {
                    case TicketLine l:
                        using (var pen = new System.Drawing.Pen(System.Drawing.Color.Black, (float)l.Thickness))
                            g.DrawLine(pen, (float)l.X, (float)l.Y, (float)(l.X + l.Length), (float)l.Y);
                        break;
                    case TicketText t:
                        using (var font = new System.Drawing.Font(fontName, (float)t.FontSize, System.Drawing.GraphicsUnit.Point))
                        using (var sf = new System.Drawing.StringFormat(System.Drawing.StringFormatFlags.NoWrap))
                        {
                            sf.Alignment = t.Align switch
                            {
                                TextAlign.Center => System.Drawing.StringAlignment.Center,
                                TextAlign.Right => System.Drawing.StringAlignment.Far,
                                _ => System.Drawing.StringAlignment.Near,
                            };
                            float x = t.Align switch { TextAlign.Center => (float)t.X - 100, TextAlign.Right => (float)t.X - 200, _ => (float)t.X };
                            g.DrawString(t.Text, font, System.Drawing.Brushes.Black, new System.Drawing.RectangleF(x, (float)t.Y, 200, 20), sf);
                        }
                        break;
                }
            }
            e.HasMorePages = false;
        };
        doc.Print();
    }
}

/// <summary>Shows the ticket on screen (A4) with a print button.</summary>
public sealed class TicketPreviewWindow : Window
{
    private const double PxPerMm = 3.2;

    public TicketPreviewWindow(AppState state, Weighing w)
    {
        Title = $"Mérlegjegy {w.TicketNo} – előnézet";
        Width = 210 * PxPerMm + 60;
        Height = 760;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var (form, source) = TicketService.LoadForm(state);
        var items = TicketForm.Layout(form, w, state.Db.AllSettings());
        var font = new FontFamily(state.Db.GetSetting("printer.font_name") ?? "Arial");
        var page = new Canvas { Width = 210 * PxPerMm, Height = 297 * PxPerMm, Background = Brushes.White };
        foreach (var item in items)
        {
            switch (item)
            {
                case TicketLine l:
                    page.Children.Add(new Line
                    {
                        StartPoint = new Point(l.X * PxPerMm, l.Y * PxPerMm),
                        EndPoint = new Point((l.X + l.Length) * PxPerMm, l.Y * PxPerMm),
                        Stroke = Brushes.Black, StrokeThickness = Math.Max(1, l.Thickness * PxPerMm),
                    });
                    break;
                case TicketText t:
                    var tb = new TextBlock
                    {
                        Text = t.Text, FontFamily = font, Foreground = Brushes.Black, Width = 200 * PxPerMm,
                        FontSize = t.FontSize * 0.3528 * PxPerMm, // pt -> mm -> px
                        TextAlignment = t.Align switch { TextAlign.Center => TextAlignment.Center, TextAlign.Right => TextAlignment.Right, _ => TextAlignment.Left },
                    };
                    Canvas.SetLeft(tb, (t.Align switch { TextAlign.Center => t.X - 100, TextAlign.Right => t.X - 200, _ => t.X }) * PxPerMm);
                    Canvas.SetTop(tb, t.Y * PxPerMm);
                    page.Children.Add(tb);
                    break;
            }
        }

        var print = new Button { Content = "Nyomtatás", MinWidth = 120, IsDefault = true };
        var close = new Button { Content = "Bezárás", MinWidth = 100, IsCancel = true };
        var info = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = "Nyomtató:", VerticalAlignment = VerticalAlignment.Center },
                Printers.Chooser(state),
                new TextBlock { Text = $"Forma: {System.IO.Path.GetFileName(source)}", Classes = { "hint" }, VerticalAlignment = VerticalAlignment.Center },
            },
        };
        print.Click += async (_, _) =>
        {
            if (TicketService.Print(state, w) is { } err) await Dialog.Info(this, "Nyomtatás", err);
            else Close();
        };
        close.Click += (_, _) => Close();

        var bar = new DockPanel { Margin = new Thickness(8) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { close, print } };
        DockPanel.SetDock(buttons, Dock.Right);
        bar.Children.Add(buttons);
        bar.Children.Add(info);

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Bottom);
        root.Children.Add(bar);
        root.Children.Add(new ScrollViewer
        {
            Background = Brushes.Gray,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = new Border { Margin = new Thickness(20), Child = page, BoxShadow = BoxShadows.Parse("0 2 8 0 #60000000") },
        });
        Content = root;
    }
}
