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
        return new Rect(Math.Clamp(s.X * width, 0, Math.Max(0, width - w)), Math.Clamp(s.Y * height, 0, Math.Max(0, height - h)), w, h);
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

    void Widget_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (dragging is not null || sender is not FrameworkElement { Tag: string tag } element) return;
        if (!e.GetCurrentPoint(element).Properties.IsLeftButtonPressed) return;
        dragging = tag;
        dragStart = e.GetCurrentPoint(WidgetLayer).Position;
        dragFrom = Box(Owner(tag));
        live = dragFrom;
        element.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    void Widget_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (dragging is null) return;
        var p = e.GetCurrentPoint(WidgetLayer).Position;
        double dx = p.X - dragStart.X, dy = p.Y - dragStart.Y, width = WidgetLayer.ActualWidth, height = WidgetLayer.ActualHeight;
        var f = dragFrom;
        if (dragging == "pipsize")
        {
            // Resizing keeps the video's shape.
            var aspect = (f.Height - PipBar) / f.Width;
            var w = Math.Clamp(f.Width + Math.Max(dx, dy / aspect), 200, Math.Max(200, Math.Min(900, Math.Min(width - f.X, (height - f.Y - PipBar) / aspect))));
            live = new Rect(f.X, f.Y, w, w * aspect + PipBar);
        }
        else live = new Rect(Math.Clamp(f.X + dx, 0, Math.Max(0, width - f.Width)), Math.Clamp(f.Y + dy, 0, Math.Max(0, height - f.Height)), f.Width, f.Height);
        PlaceWidgets();
    }

    void Widget_PointerReleased(object sender, PointerRoutedEventArgs e) => EndDrag();
    void Widget_PointerCaptureLost(object sender, PointerRoutedEventArgs e) => EndDrag();

    void EndDrag()
    {
        if (dragging is not { } tag || live is not { } r) return;
        var widget = Owner(tag);
        dragging = null;
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
