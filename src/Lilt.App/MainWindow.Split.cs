using Lilt.App.Browser;
using Lilt.Core.Theming;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;

namespace Lilt.App;

// Which pages are on screen and where. Split view pairs two tabs: showing either shows both (the earlier tab in the
// list takes the first pane), showing any other tab shows it alone. A popped-out tab floats in its frame on top.
// Power saving hides them all (pagesHidden).
public sealed partial class MainWindow
{
    (BrowserTab A, BrowserTab B)? split;
    double? dragRatio;
    bool pagesHidden;

    bool InSplit(BrowserTab tab) => split is { } s && (s.A == tab || s.B == tab);
    BrowserTab? Partner(BrowserTab tab) => split is { } s ? s.A == tab ? s.B : s.B == tab ? s.A : null : null;

    void LayoutPages()
    {
        var partner = active is null ? null : Partner(active);
        var (first, second) = partner is null ? (null, null) : tabs.IndexOf(active!) < tabs.IndexOf(partner) ? (active, partner) : (partner, active);
        var horizontal = theme.Split.Orientation == SplitOrientation.Horizontal;
        var ratio = dragRatio ?? theme.Split.Ratio;
        if (PageHost.ColumnDefinitions.Count == 0)
            for (var i = 0; i < 3; i++)
            {
                PageHost.ColumnDefinitions.Add(new ColumnDefinition());
                PageHost.RowDefinitions.Add(new RowDefinition());
            }
        GridLength Star(double v) => new(v, GridUnitType.Star);
        var zero = new GridLength(0);
        var gap = new GridLength(theme.Split.Gap);
        bool across = first is not null && horizontal, down = first is not null && !horizontal;
        PageHost.ColumnDefinitions[0].Width = across ? Star(ratio) : Star(1);
        PageHost.ColumnDefinitions[1].Width = across ? gap : zero;
        PageHost.ColumnDefinitions[2].Width = across ? Star(1 - ratio) : zero;
        PageHost.RowDefinitions[0].Height = down ? Star(ratio) : Star(1);
        PageHost.RowDefinitions[1].Height = down ? gap : zero;
        PageHost.RowDefinitions[2].Height = down ? Star(1 - ratio) : zero;

        foreach (var tab in tabs)
        {
            tab.IsShown = tab != active && (tab == partner || tab == floating);
            if (tab.View is not { } view) continue;
            view.Visibility = !pagesHidden && OnScreen(tab) ? Visibility.Visible : Visibility.Collapsed;
            if (tab == floating)
            {
                Canvas.SetZIndex(view, 3);
                Cell(view, 0, 3, 0, 3);
                view.HorizontalAlignment = HorizontalAlignment.Left;
                view.VerticalAlignment = VerticalAlignment.Top;
                continue;
            }
            Canvas.SetZIndex(view, 0);
            view.Margin = default;
            view.Width = view.Height = double.NaN;
            view.HorizontalAlignment = HorizontalAlignment.Stretch;
            view.VerticalAlignment = VerticalAlignment.Stretch;
            var at = tab == second ? 2 : 0;
            if (first is null || (tab != first && tab != second)) Cell(view, 0, 3, 0, 3);
            else if (horizontal) Cell(view, at, 1, 0, 3);
            else Cell(view, 0, 3, at, 1);
        }

        // The divider sits in the gap and is at least 8 px wide to grab; the outline marks the pane the toolbar acts on.
        var paired = first is not null && !pagesHidden;
        SplitHandle.Visibility = SplitFocus.Visibility = paired ? Visibility.Visible : Visibility.Collapsed;
        if (paired)
        {
            var grab = Math.Max(0, (8 - theme.Split.Gap) / 2);
            if (horizontal) Cell(SplitHandle, 1, 1, 0, 3);
            else Cell(SplitHandle, 0, 3, 1, 1);
            SplitHandle.Margin = horizontal ? new Thickness(-grab, 0, -grab, 0) : new Thickness(0, -grab, 0, -grab);
            SplitGrip.Width = horizontal ? 4 : 40;
            SplitGrip.Height = horizontal ? 40 : 4;
            var at = active == second ? 2 : 0;
            if (horizontal) Cell(SplitFocus, at, 1, 0, 3);
            else Cell(SplitFocus, 0, 3, at, 1);
        }
        PipFrame.Visibility = floating is not null && !pagesHidden ? Visibility.Visible : Visibility.Collapsed;
        ShowPanelPage();
        PlaceWidgets();
        UpdateMedia();
    }

    static void Cell(FrameworkElement e, int column, int columns, int row, int rows)
    {
        Grid.SetColumn(e, column);
        Grid.SetColumnSpan(e, columns);
        Grid.SetRow(e, row);
        Grid.SetRowSpan(e, rows);
    }

    void ToggleSplit()
    {
        if (active is null) return;
        if (Partner(active) is not null)
        {
            split = null;
            LayoutPages();
            ScheduleSave();
            return;
        }
        // The tab you were on before this one, or a new home tab.
        var other = tabs.Where(t => t != active && t != floating).MaxBy(t => t.LastActive);
        if (other is null)
        {
            other = new BrowserTab(BrowserEngine.NewTabUri.AbsoluteUri);
            tabs.Insert(tabs.IndexOf(active) + 1, other);
        }
        SplitWith(other);
    }

    void SplitWith(BrowserTab other)
    {
        if (active is null || other == active) return;
        if (other == floating) DockFloating(activate: false);
        split = (active, other);
        other.LastActive = Environment.TickCount64;
        if (other.View is null && other.Loading is null && started) Load(other);
        if (other.Core is { } c) c.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal;
        LayoutPages();
        ScheduleSave();
    }

    void SwapSplit()
    {
        if (split is not { } s) return;
        int a = tabs.IndexOf(s.A), b = tabs.IndexOf(s.B);
        if (a < 0 || b < 0) return;
        tabs.Move(a, b);
        tabs.Move(b + (a < b ? -1 : 1), a);
        TabList.SelectedItem = active;
        LayoutPages();
    }

    void SplitButton_Click(object sender, RoutedEventArgs e) => ToggleSplit();

    void SplitHandle_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (dragging is not null) return;
        dragRatio = theme.Split.Ratio;
        StartDrag("split", SplitHandle, e);
    }

    void SplitDragTo(Windows.Foundation.Point p)
    {
        var horizontal = theme.Split.Orientation == SplitOrientation.Horizontal;
        var size = (horizontal ? PageHost.ActualWidth : PageHost.ActualHeight) - theme.Split.Gap;
        if (size <= 0) return;
        dragRatio = Math.Clamp(((horizontal ? p.X : p.Y) - theme.Split.Gap / 2) / size, 0.15, 0.85);
        LayoutPages();
    }

    void EndSplitDrag()
    {
        if (dragRatio is not { } r) return;
        dragRatio = null;
        Queue(Current with { Split = Current.Split with { Ratio = Math.Round(r, 3) } });
    }

    void SplitHandle_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) =>
        Queue(Current with { Split = Current.Split with { Ratio = 0.5 } });
}
