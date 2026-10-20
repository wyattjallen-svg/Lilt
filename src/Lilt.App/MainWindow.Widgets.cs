using Lilt.Core.Theming;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;

namespace Lilt.App;

// Floating widgets over the page: the player, the visualizer and the popped-out video's frame. Drag one anywhere (the
// pop-out also resizes from its corner); where it lands is saved to the theme as a fraction of the page area.
public sealed partial class MainWindow
{
    const double PipBar = 30;
    string? dragging;
    Point dragStart;
    Rect dragFrom;
    Rect? live;

    WidgetSpec Spec(string widget) => widget switch
    {
        "player" => theme.Media.Player,
        "visualizer" => theme.Media.Visualizer,
        _ => theme.Media.Pip,
    };

    static string Owner(string tag) => tag == "pipsize" ? "pip" : tag;

    // Where a widget goes: its saved place, kept inside the page area.
    Rect Box(string widget)
    {
        if (live is { } r && dragging is { } d && Owner(d) == widget) return r;
        var s = Spec(widget);
        double width = WidgetLayer.ActualWidth, height = WidgetLayer.ActualHeight;
        var w = Math.Min(s.Width, width);
        var h = widget switch
        {
            "player" => s.Height > 0 ? s.Height : MediaWidget.ActualHeight > 0 ? MediaWidget.ActualHeight : 110,
            "pip" => (s.Height > 0 ? s.Height * w / s.Width : w * 9 / 16) + PipBar,
            _ => s.Height,
        };
        h = Math.Min(h, height);
        return new Rect(Inside(s.X * width, w, width), Inside(s.Y * height, h, height), w, h);
    }

    // Keeps a widget a few pixels inside the page area's edges (or at the edge when it barely fits).
    static double Inside(double at, double size, double room)
    {
        var margin = Math.Clamp((room - size) / 2, 0, 6);
        return Math.Clamp(at, margin, Math.Max(margin, room - size - margin));
    }

    void PlaceWidgets()
    {
        foreach (var (element, widget) in new (FrameworkElement, string)[] { (MediaWidget, "player"), (VisualizerWidget, "visualizer"), (PipFrame, "pip") })
        {
            if (element.Visibility != Visibility.Visible) continue;
            var b = Box(widget);
            Canvas.SetLeft(element, b.X);
            Canvas.SetTop(element, b.Y);
            element.Width = b.Width;
            element.Height = widget == "player" && Spec(widget).Height <= 0 ? double.NaN : b.Height;
            if (widget != "pip") element.Opacity = Spec(widget).Opacity;
        }
        // The popped-out page sits exactly in the frame's empty area (the page host and the widget layer share a cell).
        if (floating?.View is { } view)
        {
            var b = Box("pip");
            view.Margin = new Thickness(b.X, b.Y + PipBar, 0, 0);
            view.Width = b.Width;
            view.Height = Math.Max(1, b.Height - PipBar);
        }
    }

    void WidgetLayer_SizeChanged(object sender, SizeChangedEventArgs e) => PlaceWidgets();

    // A drag starts on the widget; its moves and the release are followed at the content area (handled events
    // included), so it keeps going over the page and ends wherever the button comes up.
    void Widget_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (dragging is not null || sender is not FrameworkElement { Tag: string tag } element) return;
        if (!e.GetCurrentPoint(element).Properties.IsLeftButtonPressed) return;
        StartDrag(tag, element, e);
        dragFrom = Box(Owner(tag));
        live = dragFrom;
    }

    void StartDrag(string tag, UIElement element, PointerRoutedEventArgs e)
    {
        dragging = tag;
        dragMoves = 0;
        dragStart = e.GetCurrentPoint(WidgetLayer).Position;
        element.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    int dragMoves;

    void DragTo(PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(WidgetLayer);
        if (!point.Properties.IsLeftButtonPressed)
        {
            report.Key("drag", "button up");
            EndDrag();
            return;
        }
        dragMoves++;
        var p = point.Position;
        if (dragging == "split")
        {
            SplitDragTo(e.GetCurrentPoint(PageHost).Position);
            return;
        }
        double dx = p.X - dragStart.X, dy = p.Y - dragStart.Y, width = WidgetLayer.ActualWidth, height = WidgetLayer.ActualHeight;
        var f = dragFrom;
        if (dragging == "pipsize")
        {
            // Resizing keeps the video's shape.
            var aspect = (f.Height - PipBar) / f.Width;
            var w = Math.Clamp(f.Width + Math.Max(dx, dy / aspect), 200, Math.Max(200, Math.Min(900, Math.Min(width - f.X, (height - f.Y - PipBar) / aspect))));
            live = new Rect(f.X, f.Y, w, w * aspect + PipBar);
        }
        else live = new Rect(Inside(f.X + dx, f.Width, width), Inside(f.Y + dy, f.Height, height), f.Width, f.Height);
        PlaceWidgets();
    }

    void EndDrag()
    {
        if (dragging is not { } tag) return;
        report.Key("drag", $"{tag} after {dragMoves} moves");
        if (tag == "split")
        {
            dragging = null;
            EndSplitDrag();
            return;
        }
        var widget = Owner(tag);
        dragging = null;
        // A click that didn't move anything leaves the theme alone.
        if (live is not { } r || r == dragFrom)
        {
            live = null;
            return;
        }
        live = null;
        double width = WidgetLayer.ActualWidth, height = WidgetLayer.ActualHeight;
        if (width <= 0 || height <= 0) return;
        var s = Spec(widget) with { X = Math.Round(r.X / width, 4), Y = Math.Round(r.Y / height, 4) };
        if (tag == "pipsize") s = s with { Width = Math.Round(r.Width), Height = s.Height > 0 ? Math.Round(r.Height - PipBar) : 0 };
        var m = Current.Media;
        Queue(Current with
        {
            Media = widget switch
            {
                "player" => m with { Player = s },
                "visualizer" => m with { Visualizer = s },
                _ => m with { Pip = s },
            },
        });
    }

    void ResetWidgets_Click(object sender, RoutedEventArgs e)
    {
        var d = Theme.Default.Media;
        var m = Current.Media;
        playerToggle = visualizerToggle = null;
        Queue(Current with
        {
            Media = m with
            {
                Player = m.Player with { X = d.Player.X, Y = d.Player.Y, Width = d.Player.Width, Height = d.Player.Height },
                Visualizer = m.Visualizer with { X = d.Visualizer.X, Y = d.Visualizer.Y, Width = d.Visualizer.Width, Height = d.Visualizer.Height },
                Pip = m.Pip with { X = d.Pip.X, Y = d.Pip.Y, Width = d.Pip.Width, Height = d.Pip.Height },
            },
        });
    }
}
