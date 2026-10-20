using System.Collections.ObjectModel;
using Lilt.App.Browser;
using Lilt.Core;
using Lilt.Core.Navigation;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Web.WebView2.Core;

namespace Lilt.App;

// Tabs: one WebView2 per tab, created the first time the tab is shown. Tabs you aren't looking at get a low
// memory target, and are suspended after power.suspendMinutes (see MainWindow.Power.cs).
public sealed partial class MainWindow
{
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
        var start = AddressResolver.Resolve(options.Url, SearchEngines.For(themes.Current.Pages.Search).Template);
        startProblem = start.Kind == AddressKind.Rejected ? start.Reason : null;

        BrowserTab? pick = null, partner = null;
        if (!options.InPrivate && Session.Load(paths.Session) is { } session)
            for (var i = 0; i < session.Tabs.Count; i++)
            {
                var saved = session.Tabs[i];
                if (!UrlPolicy.IsNavigable(saved.Url)) continue;
                var tab = new BrowserTab(saved.Url, saved.Title, saved.Pinned);
                tabs.Add(tab);
                if (i <= session.Active || pick is null) pick = tab;
                if (i == session.Split) partner = tab;
            }
        if (start.Kind is AddressKind.Navigate or AddressKind.Search || tabs.Count == 0)
        {
            pick = new BrowserTab(start.Uri?.AbsoluteUri ?? BrowserEngine.NewTabUri.AbsoluteUri);
            tabs.Add(pick);
            partner = null;
        }
        if (partner is not null && partner != pick) split = (pick!, partner);
        active = pick;
        active!.IsActive = true;
        TabList.SelectedItem = active;
        tabs.CollectionChanged += (_, _) => ScheduleSave();

        saveTimer = DispatcherQueue.CreateTimer();
        saveTimer.Interval = TimeSpan.FromSeconds(1);
        saveTimer.IsRepeating = false;
        saveTimer.Tick += (_, _) => SaveSession();
        suspendTimer = DispatcherQueue.CreateTimer();
        suspendTimer.Interval = TimeSpan.FromMinutes(1);
        suspendTimer.Tick += (_, _) => SuspendIdleTabs(force: false);
        suspendTimer.Start();
    }

    Task<CoreWebView2> LoadAsync(BrowserTab tab, bool navigate) => tab.Loading ??= CreateViewAsync(tab, navigate);

    async Task<CoreWebView2> CreateViewAsync(BrowserTab tab, bool navigate)
    {
        var view = new WebView2 { Visibility = Visibility.Collapsed };
        // Lets a transparent page (the glass home tab) show the window material instead of the control's own theme background.
        view.Resources["BrushForThemeBackgroundColor"] = new SolidColorBrush(Colors.Transparent);
        tab.View = view;
        SetPageBackground(tab);
        PageHost.Children.Insert(0, view);
        LayoutPages();
        // In split view, clicking into the other pane makes its tab the one the toolbar acts on.
        view.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) =>
        {
            if (tab != active && OnScreen(tab) && tab != floating) Activate(tab);
        }), true);
        var c = await engine.AttachAsync(view);
        tab.Core = c;
        if (!OnScreen(tab)) c.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;

        c.NavigationStarting += (_, e) =>
        {
            tab.IsLoading = true;
            tab.HasError = false;
            SetPageBackground(tab, e.Uri);
            if (tab != active) return;
            HideSuggestions();
            UpdateNavigation();
        };
        c.NavigationCompleted += (_, e) =>
        {
            tab.IsLoading = false;
            // Stopped or replaced navigations aren't errors worth a red tab.
            tab.HasError = !e.IsSuccess && e.WebErrorStatus is not (CoreWebView2WebErrorStatus.OperationCanceled or CoreWebView2WebErrorStatus.ConnectionAborted);
            if (tab == active)
            {
                UpdateNavigation();
                UpdateTitle();
            }
            if (e.IsSuccess) RecordVisit(c.Source, c.DocumentTitle);
            if (!firstNavigation) return;
            firstNavigation = false;
            report.FirstNavigation(c.Source, e.IsSuccess, e.WebErrorStatus.ToString());
        };
        c.SourceChanged += (_, e) =>
        {
            tab.Url = c.Source;
            if (!e.IsNewDocument && tab.IsHome) HomeCommand(c);
            // In-page navigations (most single-page sites) count as visits too.
            else if (!e.IsNewDocument) RecordVisit(c.Source, c.DocumentTitle);
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
            RetitleVisit(c.Source, c.DocumentTitle);
            ScheduleSave();
        };
        c.FaviconChanged += (_, _) => LoadFavicon(tab, c);
        c.IsDocumentPlayingAudioChanged += (_, _) =>
        {
            tab.IsAudible = c.IsDocumentPlayingAudio;
            UpdatePower();
            UpdateVisualizer();
        };
        c.IsMutedChanged += (_, _) =>
        {
            tab.IsMuted = c.IsMuted;
            if (tab == mediaTab) UpdateMedia();
            UpdateVisualizer();
        };
        // A popped-out video fills its own small page, not the window.
        c.ContainsFullScreenElementChanged += (_, _) =>
        {
            if (tab == active && tab != floating) SetFullScreen(c.ContainsFullScreenElement);
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

    // Favicons show in the tab and are kept for home tiles of bookmarked sites (newtab\icons\<host>.png), so tiles never fetch from the web.
    async void LoadFavicon(BrowserTab tab, CoreWebView2 c)
    {
        try
        {
            if (string.IsNullOrEmpty(c.FaviconUri))
            {
                tab.Icon = null;
                return;
            }
            byte[] png;
            using (var stream = await c.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png))
            using (var copy = new MemoryStream())
            {
                await stream.AsStreamForRead().CopyToAsync(copy);
                png = copy.ToArray();
            }
            var image = new BitmapImage { DecodePixelWidth = 32 };
            using (var ms = new MemoryStream(png)) await image.SetSourceAsync(ms.AsRandomAccessStream());
            tab.Icon = image;
            SaveTileIcon(c.Source, png);
        }
        catch (Exception)
        {
            tab.Icon = null;
        }
    }

    void Activate(BrowserTab tab)
    {
        if (!tabs.Contains(tab)) return;
        if (tab == floating)
        {
            floating = null;
            if (tab.Core is { } fc) _ = engine.EvaluateAsync(fc, "__lilt.float(false)");
        }
        var old = active;
        if (old is not null && old != tab) old.IsActive = false;
        active = tab;
        tab.IsActive = true;
        tab.LastActive = Environment.TickCount64;
        if (Partner(tab) is { } partner) partner.LastActive = tab.LastActive;
        if (TabList.SelectedItem != tab) TabList.SelectedItem = tab;
        if (tab.View is null && tab.Loading is null && started) Load(tab);
        if (Partner(tab) is { View: null, Loading: null } p && started) Load(p);
        LayoutPages();
        foreach (var t in tabs)
            if (t.Core is { } tc) tc.MemoryUsageTargetLevel = OnScreen(t) ? CoreWebView2MemoryUsageTargetLevel.Normal : CoreWebView2MemoryUsageTargetLevel.Low;
        // media.autoPictureInPicture: a video playing in the tab you leave pops out.
        if (old is { Media: { Playing: true, Video: true } } && old != tab && !OnScreen(old) && theme.Media.AutoPictureInPicture) _ = PipAsync(old);
        SetFullScreen(tab.Core?.ContainsFullScreenElement == true);
        // Whatever was being typed belonged to the other tab.
        HideSuggestions();
        if (AddressBox.FocusState != FocusState.Unfocused) SetAddress(CurrentFull);
        UpdateNavigation();
        UpdateTitle();
        ScheduleSave();
    }

    // activate: false opens it behind the current tab (Ctrl+click, middle-click), loading right away.
    BrowserTab AddTab(string url, bool atEnd = false, bool activate = true)
    {
        var tab = new BrowserTab(url);
        tabs.Insert(atEnd || active is null ? tabs.Count : NextTabIndex(activate), tab);
        if (activate) Activate(tab);
        else
        {
            lastBehind = tab;
            if (started) Load(tab);
        }
        return tab;
    }

    // New tabs go right after the current one; several opened behind it in a row stay in the order they were opened.
    BrowserTab? lastBehind, lastBehindFrom;

    int NextTabIndex(bool activate)
    {
        var at = lastBehindFrom == active && lastBehind is { } last && tabs.Contains(last) ? tabs.IndexOf(last) + 1 : tabs.IndexOf(active!) + 1;
        if (!activate) lastBehindFrom = active;
        return Math.Max(at, PinnedCount());
    }

    int PinnedCount() => tabs.TakeWhile(t => t.IsPinned).Count();

    // A page asked for a new window (target=_blank, window.open): the tab's view must exist but not navigate yet.
    // Links opened with Ctrl+click or middle-click open behind the current tab.
    async Task<CoreWebView2> OpenTabForPageAsync(bool behind)
    {
        var tab = new BrowserTab("about:blank");
        var at = active is null ? tabs.Count : NextTabIndex(!behind);
        tabs.Insert(at, tab);
        if (behind) lastBehind = tab;
        var load = LoadAsync(tab, navigate: false);
        if (!behind) Activate(tab);
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
            closedTabs.Add(new SessionTab(tab.Url, tab.Title, tab.IsPinned));
            if (closedTabs.Count > 25) closedTabs.RemoveAt(0);
        }
        // Closing the last tab leaves a fresh home tab rather than closing the window.
        if (tabs.Count == 1) AddTab(BrowserEngine.NewTabUri.AbsoluteUri);
        else if (tab == active) Activate(Partner(tab) ?? tabs[i + 1 < tabs.Count ? i + 1 : i - 1]);
        if (InSplit(tab)) split = null;
        if (floating == tab) floating = null;
        if (mediaTab == tab) mediaTab = tabs.FirstOrDefault(t => t != tab && t.Media is { Playing: true });
        tabs.Remove(tab);
        LayoutPages();
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
        var tab = AddTab(last.Url);
        tab.Title = last.Title;
        if (last.Pinned) SetPinned(tab, true);
    }

    void CycleTab(int step)
    {
        if (active is null || tabs.Count < 2) return;
        Activate(tabs[(tabs.IndexOf(active) + step + tabs.Count) % tabs.Count]);
    }

    // Pinned tabs show only their icon and stay at the top of the list.
    void SetPinned(BrowserTab tab, bool pinned)
    {
        if (tab.IsPinned == pinned) return;
        var from = tabs.IndexOf(tab);
        // Either way the tab lands right after the other pinned tabs.
        var to = tabs.Count(t => t != tab && t.IsPinned);
        tab.IsPinned = pinned;
        if (from != to) tabs.Move(from, to);
        if (TabList.ContainerFromItem(tab) is ListViewItem item) SizeTab(item, tab);
        TabList.SelectedItem = active;
        ScheduleSave();
    }

    // Suspended tabs keep their page and scroll position but use no CPU; they resume when shown.
    async void SuspendIdleTabs(bool force)
    {
        var now = Environment.TickCount64;
        var spec = theme.Power;
        foreach (var tab in tabs.ToArray())
        {
            if (closing || tab.Core is not { } c || tab.IsAudible) continue;
            var hidden = !OnScreen(tab) || force;
            if (!hidden || (!force && !PowerPolicy.ShouldSuspend(spec, (now - tab.LastActive) / 60000.0, energySaver))) continue;
            try
            {
                if (!c.IsSuspended && await c.TrySuspendAsync()) report.Key("power", $"suspended {UrlPolicy.DisplayHost(tab.Url)}");
            }
            catch (Exception) { } // best effort: visible or closing views refuse
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
            var partner = active is null ? null : Partner(active);
            new Session(list.Select(t => new SessionTab(t.Url, t.Title, t.IsPinned)).ToList(), Math.Max(0, active is null ? 0 : list.IndexOf(active)),
                partner is null ? -1 : list.IndexOf(partner)).Save(paths.Session);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            LiltPaths.LogError(e);
        }
    }

    // A tab strip along the top or bottom; a list down the side.
    void SetTabStripOrientation(bool vertical)
    {
        if (tabStripSet && vertical == verticalTabs)
        {
            foreach (var tab in tabs)
                if (TabList.ContainerFromItem(tab) is ListViewItem existing) SizeTab(existing, tab);
            return;
        }
        tabStripSet = true;
        TabList.ItemsPanel = (ItemsPanelTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(vertical
            ? "<ItemsPanelTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><ItemsStackPanel Orientation='Vertical'/></ItemsPanelTemplate>"
            : "<ItemsPanelTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><ItemsStackPanel Orientation='Horizontal'/></ItemsPanelTemplate>");
        ScrollViewer.SetHorizontalScrollMode(TabList, vertical ? ScrollMode.Disabled : ScrollMode.Enabled);
        ScrollViewer.SetHorizontalScrollBarVisibility(TabList, vertical ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Hidden);
        ScrollViewer.SetVerticalScrollMode(TabList, vertical ? ScrollMode.Enabled : ScrollMode.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(TabList, vertical ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled);
        TabList.VerticalAlignment = vertical ? VerticalAlignment.Stretch : VerticalAlignment.Center;
        verticalTabs = vertical;
        TabList.ContainerContentChanging -= SizeTabItem;
        TabList.ContainerContentChanging += SizeTabItem;
        foreach (var tab in tabs)
            if (TabList.ContainerFromItem(tab) is ListViewItem item) SizeTab(item, tab);
    }

    bool verticalTabs = true, tabStripSet;

    void SizeTabItem(ListViewBase sender, ContainerContentChangingEventArgs e)
    {
        if (e.Item is BrowserTab tab) SizeTab((ListViewItem)e.ItemContainer, tab);
    }

    // Pinned tabs in a strip are icon-sized; other strip tabs are a fixed width. Row height follows the spacing setting.
    void SizeTab(ListViewItem item, BrowserTab tab)
    {
        item.Width = verticalTabs ? double.NaN : tab.IsPinned ? 48 : 200;
        item.MinHeight = metrics.ControlHeight + 4;
        item.Padding = new Thickness(metrics.Unit / 2, 1, metrics.Unit / 2, 1);
    }

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

    void Tab_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: BrowserTab tab } element) return;
        e.Handled = true;
        var menu = new MenuFlyout();
        void Add(string text, string glyph, Action action)
        {
            var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        Add(tab.IsPinned ? "Unpin" : "Pin", tab.IsPinned ? "" : "", () => SetPinned(tab, !tab.IsPinned));
        if (tab.Core is { } c) Add(c.IsMuted ? "Unmute" : "Mute", c.IsMuted ? "" : "", () => c.IsMuted = !c.IsMuted);
        Add("Reload", "", () =>
        {
            if (tab.Core is { } core) core.Reload();
            else Activate(tab);
        });
        Add("Duplicate", "", () => AddTab(tab.Url));
        Add("Copy address", "\uE8C8", () => CopyText(tab.Url));
        if (InSplit(tab))
        {
            Add("Swap split panes", "\uE8AB", SwapSplit);
            Add("Close split view", "\uE8A9", () =>
            {
                split = null;
                LayoutPages();
                ScheduleSave();
            });
        }
        else if (tab != active) Add("Open in split view", "\uE8A9", () => SplitWith(tab));
        if (tab.Media is { Video: true }) Add(tab == floating ? "Put the video back" : "Pop out the video", "\uE8A7", () => _ = PipAsync(tab));
        if (bookmarks.Contains(tab.Url)) Add("Remove bookmark", "", () =>
        {
            bookmarks.Remove(tab.Url);
            SaveBookmarks();
        });
        else if (Uri.TryCreate(tab.Url, UriKind.Absolute, out var u) && UrlPolicy.IsWeb(u) && !tab.IsHome) Add("Bookmark", "", () =>
        {
            bookmarks.Add(tab.Title, tab.Url);
            SaveBookmarks();
        });
        menu.Items.Add(new MenuFlyoutSeparator());
        Add("Close", "", () => CloseTab(tab));
        if (tabs.Count > 1) Add("Close other tabs", "", () =>
        {
            foreach (var other in tabs.Where(t => t != tab && !t.IsPinned).ToArray()) CloseTab(other);
            Activate(tab);
        });
        menu.ShowAt(element, new FlyoutShowOptions { Position = e.GetPosition(element) });
    }

    void TabAudio_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: BrowserTab { Core: { } c } }) c.IsMuted = !c.IsMuted;
    }

    void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: BrowserTab tab }) CloseTab(tab);
    }

    void NewTab_Click(object sender, RoutedEventArgs e) => NewTab();
    void ReopenTab_Click(object sender, RoutedEventArgs e) => ReopenTab();
}
