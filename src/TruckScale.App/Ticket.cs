using TruckScale.Core.Localization;
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
        return (TicketForm.Parse(TicketForm.DefaultForm), L.T("ticket.builtIn"));
    }

    public static List<TicketItem> Build(AppState state, Weighing w) =>
        TicketForm.Layout(LoadForm(state).Form, w, state.Db.AllSettings());

    public static string? PrinterName(AppState state) => state.Db.GetSetting("printer.name");

    /// <summary>Everything needed to print a ticket; built on the UI thread (database), printed on any thread.</summary>
    public sealed record PrintJob(List<TicketItem> Items, string? Printer, string FontName, string DocName);

    public static PrintJob Prepare(AppState state, Weighing w) =>
        new(Build(state, w), PrinterName(state), state.Db.GetSetting("printer.font_name") ?? "Arial", L.F("ticket.docName", w.TicketNo));

    /// <summary>Prints a prepared ticket (can run on a background thread). Returns an error message, or null on success.</summary>
    public static string? Run(PrintJob job)
    {
        if (!OperatingSystem.IsWindows())
            return L.T("print.windowsOnly");
        try
        {
            PrintWindows(job.Items, job.Printer, job.FontName, job.DocName);
            return null;
        }
        catch (Exception ex)
        {
            return L.F("print.error", ex.Message);
        }
    }

    /// <summary>Prints on a background thread so the window stays responsive.</summary>
    public static Task<string?> PrintAsync(AppState state, Weighing w)
    {
        var job = Prepare(state, w);
        return Task.Run(() => Run(job));
    }

    /// <summary>
    /// Loads the printer driver in the background. The first use of the driver after the program starts can
    /// take 10+ seconds (measured: 13 s for the HP); afterwards it is fast. Called at startup and when the printer changes.
    /// </summary>
    public static void WarmUp(string? printer)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) return;
        Task.Run(() =>
        {
            try
            {
                var ps = new PrinterSettings();
                if (!string.IsNullOrWhiteSpace(printer)) ps.PrinterName = printer;
                if (ps.IsValid) _ = ps.DefaultPageSettings.HardMarginX;
            }
            catch { /* only a speed-up */ }
        });
    }

    [SupportedOSPlatform("windows")]
    private static void PrintWindows(List<TicketItem> items, string? printer, string fontName, string docName)
    {
        using var doc = new PrintDocument { DocumentName = docName, PrintController = new StandardPrintController() };
        if (!string.IsNullOrWhiteSpace(printer))
        {
            doc.PrinterSettings.PrinterName = printer;
            if (!doc.PrinterSettings.IsValid)
                throw new InvalidOperationException(L.F("print.noPrinter", printer));
        }
        var fonts = new Dictionary<double, System.Drawing.Font>();
        doc.EndPrint += (_, _) => { foreach (var f in fonts.Values) f.Dispose(); };
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
                        if (!fonts.TryGetValue(t.FontSize, out var font))
                            fonts[t.FontSize] = font = new System.Drawing.Font(fontName, (float)t.FontSize, System.Drawing.GraphicsUnit.Point);
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
        WindowFit.ClampToScreen(this);
        Title = L.F("ticket.previewTitle", w.TicketNo);
        Width = 980;
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

        var print = new Button { Content = L.T("common.print"), MinWidth = 120, IsDefault = true };
        var close = new Button { Content = L.T("common.close"), MinWidth = 100, IsCancel = true };
        var info = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = L.T("common.printer"), VerticalAlignment = VerticalAlignment.Center },
                Printers.Chooser(state),
            },
        };
        print.Click += async (_, _) =>
        {
            print.IsEnabled = false;
            print.Content = L.T("common.printing");
            var err = await TicketService.PrintAsync(state, w);
            if (err is not null) { await Dialog.Info(this, L.T("common.print"), err); print.IsEnabled = true; print.Content = L.T("common.print"); }
            else Close();
        };
        close.Click += (_, _) => Close();

        // Zoom: the page is scaled; "Egész oldal" fits the whole A4 page into the window (default).
        var scale = new ScaleTransform(1, 1);
        var zoomed = new LayoutTransformControl
        {
            LayoutTransform = scale,
            Child = new Border { Child = page, BoxShadow = BoxShadows.Parse("0 2 8 0 #60000000") },
        };
        var scroller = new ScrollViewer
        {
            Background = Brushes.Gray,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = new Border { Margin = new Thickness(20), HorizontalAlignment = HorizontalAlignment.Center, Child = zoomed },
        };
        var zoomText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MinWidth = 44 };
        void SetZoom(double z)
        {
            z = Math.Clamp(z, 0.3, 3);
            scale.ScaleX = scale.ScaleY = z;
            zoomText.Text = $"{z * 100:0}%";
        }
        void Fit()
        {
            var h = scroller.Bounds.Height - 40;
            var w = scroller.Bounds.Width - 40;
            if (h > 0 && w > 0) SetZoom(Math.Min(h / page.Height, w / page.Width));
        }
        var zoomOut = new Button { Content = "−", MinWidth = 36 };
        var zoomIn = new Button { Content = "+", MinWidth = 36 };
        var fit = new Button { Content = L.T("common.fitPage") };
        zoomOut.Click += (_, _) => SetZoom(scale.ScaleX / 1.25);
        zoomIn.Click += (_, _) => SetZoom(scale.ScaleX * 1.25);
        fit.Click += (_, _) => Fit();
        // Ctrl + mouse wheel zooms as well.
        scroller.AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            if (!e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control)) return;
            SetZoom(scale.ScaleX * (e.Delta.Y > 0 ? 1.1 : 1 / 1.1));
            e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Opened += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(Fit, Avalonia.Threading.DispatcherPriority.Loaded);
        SetZoom(0.7);

        var bar = new DockPanel { Margin = new Thickness(8) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { close, print } };
        var zoomBar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(0, 0, 16, 0), Children = { zoomOut, zoomText, zoomIn, fit } };
        DockPanel.SetDock(buttons, Dock.Right);
        DockPanel.SetDock(zoomBar, Dock.Left);
        bar.Children.Add(buttons);
        bar.Children.Add(zoomBar);
        bar.Children.Add(info);

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Bottom);
        root.Children.Add(bar);
        root.Children.Add(scroller);
        Content = root;
    }
}
