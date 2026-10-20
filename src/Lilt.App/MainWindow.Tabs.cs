using System.Collections.ObjectModel;
using Lilt.App.Browser;
using Lilt.Core;
using Lilt.Core.Navigation;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Web.WebView2.Core;

namespace Lilt.App;

// Tabs: one WebView2 per tab, created the first time the tab is shown. Background tabs are suspended after a while.
public sealed partial class MainWindow
{
    const int SuspendAfterMs = 5 * 60 * 1000;
    readonly ObservableCollection<BrowserTab> tabs = [];
    readonly List<SessionTab> closedTabs = [];
    DispatcherQueueTimer saveTimer = null!, suspendTimer = null!;
    BrowserTab? active;
    // Tabs only load once startup has set up extensions, so the ad blocker covers the first page too.
    bool started;
    string? startProblem;

    void InitTabs()
    {
        TabList.ItemsSource = tabs;
        var start = AddressResolver.Resolve(options.Url);
        startProblem = start.Kind == AddressKind.Rejected ? start.Reason : null;

        BrowserTab? pick = null;
        if (!options.InPrivate && Session.Load(paths.Session) is { } session)
            for (var i = 0; i < session.Tabs.Count; i++)
            {
                if (!UrlPolicy.IsNavigable(session.Tabs[i].Url)) continue;
                var tab = new BrowserTab(session.Tabs[i].Url, session.Tabs[i].Title);
                tabs.Add(tab);
                if (i <= session.Active || pick is null) pick = tab;
            }
        if (start.Kind is AddressKind.Navigate or AddressKind.Search || tabs.Count == 0)
        {
            pick = new BrowserTab(start.Uri?.AbsoluteUri ?? BrowserEngine.NewTabUri.AbsoluteUri);
            tabs.Add(pick);
        }
        active = pick;
        TabList.SelectedItem = active;
        tabs.CollectionChanged += (_, _) => ScheduleSave();

        saveTimer = DispatcherQueue.CreateTimer();
        saveTimer.Interval = TimeSpan.FromSeconds(1);
        saveTimer.IsRepeating = false;
        saveTimer.Tick += (_, _) => SaveSession();
        suspendTimer = DispatcherQueue.CreateTimer();
        suspendTimer.Interval = TimeSpan.FromMinutes(1);
        suspendTimer.Tick += (_, _) => SuspendIdleTabs();
        suspendTimer.Start();
    }

    Task<CoreWebView2> LoadAsync(BrowserTab tab, bool navigate) => tab.Loading ??= CreateViewAsync(tab, navigate);

    async Task<CoreWebView2> CreateViewAsync(BrowserTab tab, bool navigate)
    {
        var view = new WebView2 { Visibility = tab == active ? Visibility.Visible : Visibility.Collapsed };
        // Lets a transparent page (the glass home tab) show the window material instead of the control's own theme background.
        view.Resources["BrushForThemeBackgroundColor"] = new SolidColorBrush(Colors.Transparent);
        tab.View = view;
        SetPageBackground(tab);
        PageHost.Children.Add(view);
        var c = await engine.AttachAsync(view);
        tab.Core = c;

        c.NavigationStarting += (_, e) =>
        {
            tab.IsLoadingPage = true;
            SetPageBackground(tab, e.Uri);
            if (tab == active) UpdateNavigation();
        };
        c.NavigationCompleted += (_, e) =>
        {
            tab.IsLoadingPage = false;
            if (tab == active)
            {
                UpdateNavigation();
                UpdateTitle();
            }
            if (!firstNavigation) return;
            firstNavigation = false;
            report.FirstNavigation(c.Source, e.IsSuccess, e.WebErrorStatus.ToString());
        };
        c.SourceChanged += (_, _) =>
        {
            tab.Url = c.Source;
            if (tab == active) UpdateNavigation();
            ScheduleSave();
        };
        c.HistoryChanged += (_, _) =>
        {
            if (tab == active) UpdateNavigation();
        };
        c.DocumentTitleChanged += (_, _) =>
        {
            tab.Title = c.DocumentTitle;
            if (tab == active) UpdateTitle();
            ScheduleSave();
        };
        c.FaviconChanged += (_, _) => LoadFavicon(tab, c);
        c.ContainsFullScreenElementChanged += (_, _) =>
        {
            if (tab == active) SetFullScreen(c.ContainsFullScreenElement);
        };
        c.WindowCloseRequested += (_, _) => CloseTab(tab);
        c.ProcessFailed += (_, e) => OnProcessFailed(tab, e);

        if (navigate) c.Navigate(tab.Url);
        return c;
    }

    async void Load(BrowserTab tab)
    {
        try
        {
            await LoadAsync(tab, navigate: true);
        }
        catch (Exception e)
        {
            LiltPaths.LogError(e);
            if (tabs.Contains(tab)) ShowNotice(new($"{tab.DisplayTitle} couldn't open: {e.Message}", NoticeKind.Error));
        }
    }

    static async void LoadFavicon(BrowserTab tab, CoreWebView2 c)
    {
        try
        {
            if (string.IsNullOrEmpty(c.FaviconUri))
            {
                tab.Icon = null;
                return;
            }
            using var stream = await c.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png);
            var image = new BitmapImage { DecodePixelWidth = 32 };
            await image.SetSourceAsync(stream);
            tab.Icon = image;
        }
        catch (Exception)
        {
            tab.Icon = null;
        }
    }

    void Activate(BrowserTab tab)
    {
        if (!tabs.Contains(tab)) return;
        if (active != tab && active?.View is { } old) old.Visibility = Visibility.Collapsed;
        active = tab;
        tab.LastActive = Environment.TickCount64;
        if (TabList.SelectedItem != tab) TabList.SelectedItem = tab;
        if (tab.View is { } view) view.Visibility = Visibility.Visible;
        else if (tab.Loading is null && started) Load(tab);
        SetFullScreen(tab.Core?.ContainsFullScreenElement == true);
        UpdateNavigation();
        UpdateTitle();
        ScheduleSave();
    }

    BrowserTab AddTab(string url, bool atEnd = false)
    {
        var tab = new BrowserTab(url);
        tabs.Insert(atEnd || active is null ? tabs.Count : tabs.IndexOf(active) + 1, tab);
        Activate(tab);
        return tab;
    }

    // A page asked for a new window (target=_blank, window.open): the tab's view must exist but not navigate yet.
    async Task<CoreWebView2> OpenTabForPageAsync()
    {
        var tab = new BrowserTab("about:blank");
        tabs.Insert(active is null ? tabs.Count : tabs.IndexOf(active) + 1, tab);
        var load = LoadAsync(tab, navigate: false);
        Activate(tab);
        return await load;
    }

    void NewTab()
    {
        AddTab(BrowserEngine.NewTabUri.AbsoluteUri, atEnd: true);
        AddressBox.Focus(FocusState.Programmatic);
    }

    void CloseTab(BrowserTab tab)
    {
        var i = tabs.IndexOf(tab);
        if (i < 0) return;
        if (!tab.IsHome)
        {
            closedTabs.Add(new SessionTab(tab.Url, tab.Title));
            if (closedTabs.Count > 25) closedTabs.RemoveAt(0);
        }
        // Closing the last tab leaves a fresh home tab rather than closing the window.
        if (tabs.Count == 1) AddTab(BrowserEngine.NewTabUri.AbsoluteUri);
        else if (tab == active) Activate(tabs[i + 1 < tabs.Count ? i + 1 : i - 1]);
        tabs.Remove(tab);
        engine.Detach(tab.Core);
        if (tab.View is { } view)
        {
            PageHost.Children.Remove(view);
            view.Close();
        }
        tab.View = null;
        tab.Core = null;
    }

    void ReopenTab()
    {
        if (closedTabs.Count == 0) return;
        var last = closedTabs[^1];
        closedTabs.RemoveAt(closedTabs.Count - 1);
        AddTab(last.Url).Title = last.Title;
    }

    void CycleTab(int step)
    {
        if (active is null || tabs.Count < 2) return;
        Activate(tabs[(tabs.IndexOf(active) + step + tabs.Count) % tabs.Count]);
    }

    // Suspended tabs keep their page and scroll position but use no CPU; they resume when shown.
    async void SuspendIdleTabs()
    {
        var now = Environment.TickCount64;
        foreach (var tab in tabs.ToArray())
        {
            if (tab == active || tab.Core is not { IsSuspended: false, IsDocumentPlayingAudio: false } c || now - tab.LastActive < SuspendAfterMs) continue;
            try
            {
                await c.TrySuspendAsync();
            }
            catch (Exception) { }
        }
    }

    void ScheduleSave()
    {
        if (options.InPrivate || saveTimer is null) return;
        saveTimer.Stop();
        saveTimer.Start();
    }

    void SaveSession()
    {
        if (options.InPrivate || tabs.Count == 0) return;
        try
        {
            var list = tabs.Where(t => UrlPolicy.IsNavigable(t.Url)).ToList();
            new Session(list.Select(t => new SessionTab(t.Url, t.Title)).ToList(), Math.Max(0, active is null ? 0 : list.IndexOf(active))).Save(paths.Session);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            LiltPaths.LogError(e);
        }
    }

    // A tab strip along the top or bottom; a list down the side.
    void SetTabStripOrientation(bool vertical)
    {
        TabList.ItemsPanel = (ItemsPanelTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(vertical
            ? "<ItemsPanelTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><ItemsStackPanel Orientation='Vertical'/></ItemsPanelTemplate>"
            : "<ItemsPanelTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><ItemsStackPanel Orientation='Horizontal'/></ItemsPanelTemplate>");
        ScrollViewer.SetHorizontalScrollMode(TabList, vertical ? ScrollMode.Disabled : ScrollMode.Enabled);
        ScrollViewer.SetHorizontalScrollBarVisibility(TabList, vertical ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Hidden);
        ScrollViewer.SetVerticalScrollMode(TabList, vertical ? ScrollMode.Enabled : ScrollMode.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(TabList, vertical ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled);
        TabList.VerticalAlignment = vertical ? VerticalAlignment.Stretch : VerticalAlignment.Center;
        tabItemWidth = vertical ? double.NaN : 200;
        TabList.ContainerContentChanging -= SizeTabItem;
        TabList.ContainerContentChanging += SizeTabItem;
        foreach (var tab in tabs)
            if (TabList.ContainerFromItem(tab) is ListViewItem item) item.Width = tabItemWidth;
    }

    double tabItemWidth = double.NaN;

    void SizeTabItem(ListViewBase sender, ContainerContentChangingEventArgs e) => e.ItemContainer.Width = tabItemWidth;

    void TabList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TabList.SelectedItem is BrowserTab tab) Activate(tab);
        // Drag-reordering removes and re-inserts the item, which drops the selection; put it back.
        else if (active is not null) DispatcherQueue.TryEnqueue(() => TabList.SelectedItem = active);
    }

    void Tab_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: BrowserTab tab } element) return;
        if (e.GetCurrentPoint(element).Properties.PointerUpdateKind != PointerUpdateKind.MiddleButtonPressed) return;
        e.Handled = true;
        CloseTab(tab);
    }

    void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: BrowserTab tab }) CloseTab(tab);
    }

    void NewTab_Click(object sender, RoutedEventArgs e) => NewTab();
    void ReopenTab_Click(object sender, RoutedEventArgs e) => ReopenTab();
}
