using System.Collections.ObjectModel;
using Lilt.App.Browser;
using Lilt.Core;
using Lilt.Core.Layout;
using Lilt.Core.Navigation;
using Lilt.Core.Theming;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;
using Windows.ApplicationModel.DataTransfer;

namespace Lilt.App;

// The panel beside the page (theme "panel"): sites from the tab menu (right-click → Move to the panel), one shown at a
// time, and they keep playing while you browse, switch workspaces or hide the panel. Pages aren't dragged in; the player
// and the visualizer are, by dragging them over the panel (or dock them from its menu). panel.sections sets the rows.
public sealed partial class MainWindow
{
    readonly ObservableCollection<BrowserTab> panelTabs = [];
    BrowserTab? panelTab;
    bool panelOpen, dockOver;
    double? dragPanelWidth, vizHeight;
    double vizStart;

    void InitPanel()
    {
        PanelSiteList.ItemsSource = panelTabs;
        panelTabs.CollectionChanged += (_, _) => ScheduleSave();
    }

    bool PanelShowsPages => panelOpen && !fullScreen && theme.Panel.Sections.Has("web");
    bool PanelShows(BrowserTab tab) => tab == panelTab && PanelShowsPages;
    bool Docked(string widget) => panelOpen && !fullScreen && theme.Panel.Sections.Has(widget);

    void TogglePanel()
    {
        panelOpen = !panelOpen;
        report.Key("panel", panelOpen ? "open" : "closed");
        LayoutPanel();
        ScheduleSave();
    }

    // Width and the room the page leaves for it; cheap enough for every move of a resize drag.
    void SizePanel()
    {
        var spec = theme.Panel;
        var left = spec.Edge == DockEdge.Left;
        var open = panelOpen && !fullScreen;
        var room = ContentArea.ActualWidth;
        var width = dragPanelWidth ?? spec.Width;
        if (room > 0) width = Math.Min(width, Math.Max(200, room * 0.75));
        PanelHost.Width = width;
        PanelHost.HorizontalAlignment = left ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        var bw = theme.Shape.BorderWidth;
        PanelHost.BorderThickness = left ? new Thickness(0, 0, bw, 0) : new Thickness(bw, 0, 0, 0);
        PanelGrip.HorizontalAlignment = left ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        var taken = open ? width : 0;
        PageHost.Margin = WidgetLayer.Margin = left ? new Thickness(taken, 0, 0, 0) : new Thickness(0, 0, taken, 0);
        // Notices and permission questions stay over the page.
        NoticeBar.Margin = left ? new Thickness(16 + taken, 16, 16, 16) : new Thickness(16, 16, 16 + taken, 16);
        PermissionBar.Margin = left ? new Thickness(12 + taken, 12, 12, 12) : new Thickness(12, 12, 12 + taken, 12);
    }

    void LayoutPanel()
    {
        var spec = theme.Panel;
        var open = panelOpen && !fullScreen;
        PanelHost.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        PanelToggle.IsChecked = panelOpen;
        SizePanel();

        // Rows top to bottom from panel.sections: the sites and their pages, the visualizer, the player.
        PanelHost.RowDefinitions.Clear();
        var web = spec.Sections.Has("web");
        var vizRow = -1;
        foreach (var id in spec.Sections.Items)
        {
            var row = PanelHost.RowDefinitions.Count;
            if (id == "web")
            {
                PanelHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                PanelHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                Grid.SetRow(PanelHeader, row);
                Grid.SetRow(PanelPages, row + 1);
                Grid.SetRow(PanelEmpty, row + 1);
                continue;
            }
            PanelHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var widget = id == "visualizer" ? VisualizerWidget : MediaWidget;
            if (id == "visualizer") vizRow = row;
            Dock(widget, open ? PanelHost : null);
            Grid.SetRow(widget, row);
        }
        if (!web) PanelHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        if (!spec.Sections.Has("visualizer")) Dock(VisualizerWidget, null);
        if (!spec.Sections.Has("player")) Dock(MediaWidget, null);
        PanelHeader.Visibility = PanelPages.Visibility = web ? Visibility.Visible : Visibility.Collapsed;
        PanelEmpty.Visibility = web && panelTabs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        // The visualizer's height grip sits on its bottom edge, clear of the panel's own edge grip.
        PanelVizGrip.Visibility = open && vizRow >= 0 ? Visibility.Visible : Visibility.Collapsed;
        if (vizRow >= 0) Grid.SetRow(PanelVizGrip, vizRow);
        PanelVizGrip.Margin = spec.Edge == DockEdge.Left ? new Thickness(0, 0, 12, 0) : new Thickness(12, 0, 0, 0);
        Grid.SetRowSpan(PanelGrip, PanelHost.RowDefinitions.Count);
        ShowPanelPage();
        PlaceWidgets();
        UpdateMedia();
    }

    // A docked widget fills the panel's width; floating, it goes back over the page where it was.
    // (FrameworkElement.Parent isn't reliable before the window shows, so the containers are asked directly.)
    void Dock(FrameworkElement widget, Grid? host)
    {
        Panel target = host ?? (Panel)WidgetLayer;
        if (!target.Children.Contains(widget))
        {
            (host is null ? (Panel)PanelHost : WidgetLayer).Children.Remove(widget);
            target.Children.Add(widget);
        }
        if (host is null)
        {
            widget.Margin = default;
            return;
        }
        widget.Width = double.NaN;
        widget.Height = widget == VisualizerWidget ? theme.Media.Visualizer.Height : double.NaN;
        widget.Margin = new Thickness(6, 0, 6, 6);
        widget.Opacity = 1;
    }

    void ShowPanelPage()
    {
        foreach (var t in panelTabs)
        {
            var on = PanelShows(t) && !pagesHidden;
            t.IsActive = t == panelTab;
            if (t.View is { } v) v.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (t.Core is { } c)
                try
                {
                    c.MemoryUsageTargetLevel = on ? CoreWebView2MemoryUsageTargetLevel.Normal : CoreWebView2MemoryUsageTargetLevel.Low;
                }
                catch (Exception) { } // closing
            if (on && t.View is null && t.Loading is null && started) Load(t);
        }
        PanelBack.IsEnabled = panelTab?.Core?.CanGoBack == true;
    }

    // Out of the tab list (another tab takes its place on screen), for the panel or another workspace.
    void TakeOut(BrowserTab tab)
    {
        if (floating == tab) DockFloating(activate: false);
        if (InSplit(tab)) split = null;
        if (tabs.Count == 1) AddTab(BrowserEngine.NewTabUri.AbsoluteUri);
        else if (tab == active)
        {
            var i = tabs.IndexOf(tab);
            Activate(tabs[i + 1 < tabs.Count ? i + 1 : i - 1]);
        }
        tabs.Remove(tab);
        tab.IsActive = tab.IsShown = false;
    }

    static void Fill(FrameworkElement view)
    {
        view.Margin = default;
        view.Width = view.Height = double.NaN;
        view.HorizontalAlignment = HorizontalAlignment.Stretch;
        view.VerticalAlignment = VerticalAlignment.Stretch;
        Grid.SetRow(view, 0);
        Grid.SetColumn(view, 0);
        Grid.SetRowSpan(view, 1);
        Grid.SetColumnSpan(view, 1);
        Canvas.SetZIndex(view, 0);
    }

    // Moving the page keeps it running: music doesn't stop, sign-ins and scroll stay.
    void Reparent(BrowserTab tab, Panel to)
    {
        if (tab.View is not { } view || to.Children.Contains(view)) return;
        (to == PageHost ? (Panel)PanelPages : PageHost).Children.Remove(view);
        Fill(view);
        to.Children.Insert(0, view);
    }

    void MoveToPanel(BrowserTab tab)
    {
        if (panelTabs.Contains(tab))
        {
            ShowPanelTab(tab);
            return;
        }
        if (!tabs.Contains(tab)) return;
        // The panel holds the player, the visualizer and a popped-out video, not whole pages.
        if (tab != floating)
        {
            ShowNotice(new("Only a popped-out video goes in the panel. Pop the video out, then drag it here.", NoticeKind.Info));
            return;
        }
        TakeOut(tab);
        Reparent(tab, PanelPages);
        panelTabs.Add(tab);
        panelTab = tab;
        panelOpen = true;
        report.Key("panel", "added " + UrlPolicy.DisplayHost(tab.Url));
        LayoutPages();
        LayoutPanel();
        ScheduleSave();
    }

    void ShowPanelTab(BrowserTab tab)
    {
        panelTab = tab;
        panelOpen = true;
        LayoutPanel();
        ScheduleSave();
    }

    void BackToTabs(BrowserTab tab)
    {
        if (!panelTabs.Remove(tab)) return;
        if (panelTab == tab) panelTab = panelTabs.LastOrDefault();
        tab.IsActive = false;
        Reparent(tab, PageHost);
        tabs.Insert(active is null ? tabs.Count : tabs.IndexOf(active) + 1, tab);
        Activate(tab);
        LayoutPanel();
        ScheduleSave();
    }

    void RemoveFromPanel(BrowserTab tab)
    {
        if (!panelTabs.Remove(tab)) return;
        if (panelTab == tab) panelTab = panelTabs.LastOrDefault();
        if (!tab.IsHome) closedTabs.Add(new SessionTab(tab.Url, tab.Title));
        if (mediaTab == tab) mediaTab = AllTabs.FirstOrDefault(t => t.Media is { Playing: true });
        DisposeTab(tab);
        LayoutPanel();
        ScheduleSave();
    }

    // Keys and gestures from a page in the panel: back, forward and reload act on that page; Ctrl+W closes nothing.
    bool PanelCommand(CoreWebView2 sender, string command)
    {
        if (panelTabs.FirstOrDefault(t => t.Core == sender) is null) return false;
        switch (command)
        {
            case "back": sender.GoBack(); break;
            case "forward": sender.GoForward(); break;
            case "reload": sender.Reload(); break;
            case "closeTab": break;
            default: RunCommand(command, "panel"); break;
        }
        return true;
    }

    // While the player or the visualizer is dragged: the panel's side (or its edge, while the panel is closed) lights up.
    void ShowDropZone(bool on)
    {
        var open = panelOpen && !fullScreen;
        PanelDropZone.HorizontalAlignment = theme.Panel.Edge == DockEdge.Left ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        PanelDropZone.Width = open ? Math.Max(160, PanelHost.ActualWidth) : 160;
        PanelDropZone.Margin = open ? default : new Thickness(10);
        PanelDropZone.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }

    // Whether a widget dragged to this pointer position would dock in the panel.
    bool OverDock(PointerRoutedEventArgs e)
    {
        var left = theme.Panel.Edge == DockEdge.Left;
        if (panelOpen && !fullScreen)
        {
            var p = e.GetCurrentPoint(PanelHost).Position;
            return p.X >= 0 && p.X <= PanelHost.ActualWidth && p.Y >= 0 && p.Y <= PanelHost.ActualHeight;
        }
        var x = e.GetCurrentPoint(ContentArea).Position.X;
        return left ? x <= 160 : x >= ContentArea.ActualWidth - 160;
    }

    // Puts a widget in the panel's rows (at the bottom) and shows it there, whatever its own setting says.
    void DockWidget(string widget)
    {
        if (widget == "visualizer") visualizerToggle = true;
        else playerToggle = true;
        panelOpen = true;
        var ids = Current.Panel.Sections;
        Queue(Current with { Panel = Current.Panel with { Sections = ids.Has(widget) ? ids : new Ids(ids.Items.Append(widget).ToArray()) } });
        LayoutPanel();
    }

    // The visualizer's bottom edge, dragged: its height in the panel and on the page.
    void VizGrip_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (dragging is not null) return;
        vizStart = VisualizerWidget.ActualHeight;
        StartDrag("vizheight", PanelVizGrip, e);
    }

    void VizHeightDragTo(double dy)
    {
        vizHeight = Math.Clamp(vizStart + dy, 40, 400);
        VisualizerWidget.Height = vizHeight.Value;
    }

    void EndVizHeightDrag()
    {
        if (vizHeight is not { } h) return;
        vizHeight = null;
        Queue(Current with { Media = Current.Media with { Visualizer = Current.Media.Visualizer with { Height = Math.Round(h) } } });
    }

    void PanelGrip_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (dragging is not null) return;
        dragPanelWidth = PanelHost.ActualWidth;
        StartDrag("panel", PanelGrip, e);
    }

    void PanelDragTo(Windows.Foundation.Point p)
    {
        var w = theme.Panel.Edge == DockEdge.Left ? p.X : PanelHost.ActualWidth - p.X;
        dragPanelWidth = Math.Clamp(w, 200, Math.Max(200, Math.Min(1200, ContentArea.ActualWidth * 0.75)));
        SizePanel();
    }

    void EndPanelDrag()
    {
        if (dragPanelWidth is not { } w) return;
        dragPanelWidth = null;
        Queue(Current with { Panel = Current.Panel with { Width = Math.Round(w) } });
    }

    void PanelToggle_Click(object sender, RoutedEventArgs e) => TogglePanel();
    void PanelBack_Click(object sender, RoutedEventArgs e) => panelTab?.Core?.GoBack();
    void PanelReload_Click(object sender, RoutedEventArgs e) => panelTab?.Core?.Reload();

    void PanelSite_Click(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is BrowserTab tab) ShowPanelTab(tab);
    }

    void PanelSite_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: BrowserTab tab } element) return;
        e.Handled = true;
        var menu = new MenuFlyout();
        AddPanelSiteItems(menu, tab);
        menu.ShowAt(element, new FlyoutShowOptions { Position = e.GetPosition(element) });
    }

    static MenuFlyoutItem Item(string text, string glyph, Action action)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        item.Click += (_, _) => action();
        return item;
    }

    void AddPanelSiteItems(MenuFlyout menu, BrowserTab tab)
    {
        menu.Items.Add(Item("Move back to the tabs", "", () => BackToTabs(tab)));
        menu.Items.Add(Item("Copy address", "", () => CopyText(tab.Url)));
        if (tab.Core is { } c) menu.Items.Add(Item(c.IsMuted ? "Unmute" : "Mute", "", () => c.IsMuted = !c.IsMuted));
        menu.Items.Add(Item("Remove from the panel", "", () => RemoveFromPanel(tab)));
    }

    void PanelMenu_Opening(object? sender, object e)
    {
        var menu = PanelMenu;
        menu.Items.Clear();
        if (panelTab is { } shown)
        {
            AddPanelSiteItems(menu, shown);
            menu.Items.Add(new MenuFlyoutSeparator());
        }
        var spec = Current.Panel;
        menu.Items.Add(Item(spec.Edge == DockEdge.Left ? "Move the panel to the right" : "Move the panel to the left", "",
            () => Queue(Current with { Panel = Current.Panel with { Edge = Current.Panel.Edge == DockEdge.Left ? DockEdge.Right : DockEdge.Left } })));
        foreach (var (id, name) in new[] { ("visualizer", "visualizer"), ("player", "player") })
            menu.Items.Add(Item(spec.Sections.Has(id) ? $"Let the {name} float" : $"Dock the {name} here", id == "visualizer" ? "" : "",
                () => Queue(Current with { Panel = Current.Panel with { Sections = ToggleId(Current.Panel.Sections, id) } })));
        menu.Items.Add(Item("Panel settings", "", () => ShowCustomize("layout")));
    }

    static Ids ToggleId(Ids ids, string id) => new(ids.Has(id) ? ids.Items.Where(x => x != id).ToArray() : ids.Items.Append(id).ToArray());
}
