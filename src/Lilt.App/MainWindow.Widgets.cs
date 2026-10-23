using Lilt.Core.Theming;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;

namespace Lilt.App;

// Floating widgets over the page: the player, the visualizer and the popped-out video's frame. Drag one anywhere, or resize
// it from its corner (the pop-out keeps its video's shape; the player's contents grow with it); where it lands is saved
// to the theme as a fraction of the page area, and its size at 100% interface size.
public sealed partial class MainWindow
{
    double PipBar => metrics.Px(30);
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

    static string Owner(string tag) => tag switch
    {
        "pipsize" => "pip",
        "playersize" => "player",
        "vizsize" => "visualizer",
        _ => tag,
    };

    // Where a widget goes: its saved place, kept inside the page area.
    Rect Box(string widget)
    {
        if (live is { } r && dragging is { } d && Owner(d) == widget) return r;
        var s = Spec(widget);
        var sc = metrics.Scale;
        double width = WidgetLayer.ActualWidth, height = WidgetLayer.ActualHeight;
        var w = Math.Min(Math.Round(s.Width * sc), width);
        var h = widget switch
        {
            "player" => s.Height > 0 ? Math.Round(s.Height * sc) : MediaWidget.ActualHeight > 0 ? MediaWidget.ActualHeight : Math.Round(110 * sc),
            "pip" => (s.Height > 0 ? s.Height * w / s.Width : w * 9 / 16) + PipBar,
            _ => Math.Round(s.Height * sc),
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

    double noticeLift = 16;

    void PlaceWidgets()
    {
        var height = WidgetLayer.ActualHeight;
        var lift = 16d;
        foreach (var (element, widget) in new (FrameworkElement, string)[] { (MediaWidget, "player"), (VisualizerWidget, "visualizer"), (PipFrame, "pip") })
        {
            if (element.Visibility != Visibility.Visible || PanelHost.Children.Contains(element)) continue;
            var b = Box(widget);
            if (b.Bottom > height - metrics.Px(120)) lift = Math.Max(lift, height - b.Y + 8);
            Canvas.SetLeft(element, b.X);
            Canvas.SetTop(element, b.Y);
            element.Width = b.Width;
            element.Height = widget == "player" && Spec(widget).Height <= 0 ? double.NaN : b.Height;
            if (widget != "pip") element.Opacity = Spec(widget).Opacity;
        }
        // Notices rise above floating widgets parked along the bottom instead of covering their controls.
        noticeLift = Math.Min(lift, Math.Max(16, height - metrics.Px(140)));
        var m = NoticeBar.Margin;
        NoticeBar.Margin = new Thickness(m.Left, m.Top, m.Right, noticeLift);
        PipFrame.RowDefinitions[0].Height = new GridLength(PipBar);
        ScalePlayer();
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
        // Docked in the panel, the panel places and sizes it.
        if (Owner(tag) is "player" or "visualizer" && PanelHost.Children.Contains(Owner(tag) == "player" ? MediaWidget : VisualizerWidget)) return;
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
        if (dragging == "panel")
        {
            PanelDragTo(e.GetCurrentPoint(PanelHost).Position);
            return;
        }
        if (dragging is "player" or "visualizer" or "pip")
        {
            var over = OverDock(e);
            if (over != dockOver)
            {
                dockOver = over;
                ShowDropZone(over);
            }
        }
        double dx = p.X - dragStart.X, dy = p.Y - dragStart.Y, width = WidgetLayer.ActualWidth, height = WidgetLayer.ActualHeight;
        var f = dragFrom;
        if (dragging == "pipsize")
        {
            // Resizing keeps the video's shape.
            var aspect = (f.Height - PipBar) / f.Width;
            var w = Math.Clamp(f.Width + Math.Max(dx, dy / aspect), metrics.Px(200), Math.Max(metrics.Px(200), Math.Min(metrics.Px(900), Math.Min(width - f.X, (height - f.Y - PipBar) / aspect))));
            live = new Rect(f.X, f.Y, w, w * aspect + PipBar);
        }
        else if (dragging is "playersize" or "vizsize")
        {
            var sc = metrics.Scale;
            var w = Math.Clamp(f.Width + dx, 200 * sc, Math.Max(200 * sc, Math.Min(900 * sc, width - f.X)));
            var h = dragging == "vizsize" ? Math.Clamp(f.Height + dy, 40 * sc, Math.Max(40 * sc, Math.Min(400 * sc, height - f.Y))) : f.Height;
            live = new Rect(f.X, f.Y, w, h);
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
        if (tag == "panel")
        {
            dragging = null;
            EndPanelDrag();
            return;
        }
        var widget = Owner(tag);
        dragging = null;
        if (dockOver)
        {
            dockOver = false;
            ShowDropZone(false);
            live = null;
            if (widget == "pip") { if (floating is { } video) _ = MoveToPanelAsync(video); }
            else DockWidget(widget);
            return;
        }
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
        var sc = metrics.Scale;
        if (tag == "pipsize") s = s with { Width = Math.Round(r.Width / sc), Height = s.Height > 0 ? Math.Round((r.Height - PipBar) / sc) : 0 };
        else if (tag == "playersize") s = s with { Width = Math.Round(r.Width / sc) };
        else if (tag == "vizsize") s = s with { Width = Math.Round(r.Width / sc), Height = Math.Round(r.Height / sc) };
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
