using System.ComponentModel;
using System.Diagnostics;
using Lilt.App.Browser;
using Lilt.App.Shell;
using Lilt.Core;
using Lilt.Core.Bookmarks;
using Lilt.Core.Layout;
using Lilt.Core.Navigation;
using Lilt.Core.Theming;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.Web.WebView2.Core;
using Windows.Graphics;
using VirtualKey = Windows.System.VirtualKey;
using VirtualKeyModifiers = Windows.System.VirtualKeyModifiers;
using Windows.UI.Text;
using Windows.UI.ViewManagement;

namespace Lilt.App;

public sealed partial class MainWindow : Window
{
    readonly LaunchOptions options;
    readonly LiltPaths paths;
    readonly ThemeService themes;
    readonly BrowserEngine engine;
    readonly SidebarState sidebar;
    readonly DispatcherQueueTimer hideTimer;
    readonly StartupReport report;
    readonly UISettings uiSettings = new();
    WindowMotion? windowMotion;
    readonly string appName;
    Theme theme = Theme.Default;
    ShellMetrics metrics;
    string backdropKey = "";
    string backdropNote = "";
    bool sidebarShown, pointerInBand, fullScreen, hasDownloads, firstNavigation = true;
    int slideGeneration;

    CoreWebView2? core => active?.Core;

    public MainWindow(LaunchOptions options)
    {
        this.options = options;
        report = new StartupReport(options.StartupReportPath);
        paths = LiltPaths.Resolve(options.UserDataDir);
        var queue = DispatcherQueue.GetForCurrentThread();
        themes = new ThemeService(paths.PrepareTheme(options.ThemePath), queue);
        var load = themes.Load();
        // Media keys and lean mode are browser switches, fixed for the engine's lifetime.
        engine = new BrowserEngine(new EngineOptions(paths.WebView2, paths.PrepareNewTab(), paths.PrepareUserContent(), paths.Extensions,
            options.InPrivate, options.UserAgent, options.BrowserArgs, !options.NoExtensions, themes.Current.Media.MediaKeys, themes.Current.Power.Lean,
            themes.Current.Pages.ForceDark, themes.Current.Pages.Scrollbars == Scrollbars.Classic), queue);
        var typography = themes.Current.Typography;
        Application.Current.Resources["ContentControlThemeFontFamily"] = new FontFamily(typography.Family);
        Application.Current.Resources["ControlContentThemeFontSize"] = typography.Size;
        firstRun = !options.InPrivate && !File.Exists(paths.Session) && !File.Exists(paths.Bookmarks);
        bookmarks = new BookmarkStore(paths.Bookmarks);
        bookmarks.Load();

        InitializeComponent();

        sidebar = new SidebarState(themes.Current.Sidebar.Edge, themes.Current.Sidebar.AutoHide);
        hideTimer = DispatcherQueue.CreateTimer();
        hideTimer.IsRepeating = false;
        hideTimer.Tick += (_, _) =>
        {
            sidebar.HideDelayElapsed();
            SyncSidebar();
        };
        InitMedia();
        InitHistory();
        InitTabs();
        InitPower();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(Toolbar);
        appName = options.InPrivate ? "Lilt (InPrivate)" : "Lilt";
        AppWindow.Title = appName;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "lilt.ico"));
        VersionItem.Text = $"Lilt {typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}";
        WindowSizing.Place(this, 1280, 820);

        ApplyTheme(themes.Current, previous: null);
        ShowThemeLoad(load, startup: true);
        themes.Reloaded += OnThemeReloaded;
        themes.Watch();
        BuildAccentSwatches();
        ShowBookmarks();
        FlushHomeData();

        engine.Noticed += ShowNotice;
        engine.DownloadStarted += OnDownloadStarted;
        engine.KeyCommand += (sender, command) =>
        {
            if (sender == peekCore) RunCommand(command is "escape" or "closeTab" ? "closePeek" : command, "peek");
            else if (tabs.Any(t => t.Core == sender)) RunCommand(command, "page");
        };
        engine.PeekRequested += (sender, url) =>
        {
            if (sender == peekCore) sender.Navigate(url);
            else if (tabs.Any(t => t.Core == sender)) ShowPeek(url);
        };
        engine.Trace += message => report.Key("trace", message);
        engine.Gesture += OnGesture;
        engine.AskPermission = AskPermissionAsync;
        engine.OpenTabForPage = OpenTabForPageAsync;
        engine.OpenUrlInTab = url => AddTab(url);
        AdBlockMenuItem.Visibility = ExtensionsMenuItem.Visibility = engine.ExtensionsEnabled ? Visibility.Visible : Visibility.Collapsed;

        ContentArea.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(Content_PointerMoved), true);
        ContentArea.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(Content_PointerPressed), true);
        ContentArea.AddHandler(UIElement.PointerExitedEvent, new PointerEventHandler(Content_PointerExited), true);
        ContentArea.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) =>
        {
            if (dragging is null) return;
            report.Key("drag", "released");
            EndDrag();
        }), true);
        // Capture can go (a tooltip, another window); the drag goes on as long as moves come with the button down.
        ContentArea.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, _) =>
        {
            if (dragging is not null) report.Key("drag", "capture lost");
        }), true);
        ContentArea.SizeChanged += (_, e) => ContentArea.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };
        foreach (var element in new FrameworkElement[] { Toolbar, LeftPanel, AddressHost, RightPanel })
            element.SizeChanged += (_, _) => UpdateDragRegions();
        Root.Loaded += (_, _) =>
        {
            Root.XamlRoot.Changed += (_, _) => UpdateDragRegions();
            UpdateDragRegions();
            LayoutSidebar();
        };

        Activated += (_, e) =>
        {
            if (e.WindowActivationState != WindowActivationState.Deactivated) report.WindowShown();
        };
        Closed += OnClosed;

        _ = StartAsync();
    }

    // ---- Startup ----

    async Task StartAsync()
    {
        var tab = active!;
        CoreWebView2 first;
        try
        {
            await engine.WhenEnvironmentReady;
            report.EnvironmentReady();
            first = await LoadAsync(tab, navigate: false);
        }
        catch (Exception e)
        {
            LiltPaths.LogError(e);
            ShowNotice(new($"The web engine couldn't start: {e.Message} Lilt needs the Microsoft Edge WebView2 Runtime, which ships with Windows 11.", NoticeKind.Error));
            return;
        }
        report.EngineReady();

        // The ad blocker is checked before the first page loads, so it covers it. Installing it the first time takes
        // a few seconds, so after a short wait the page goes ahead and the blocker joins when ready.
        var adBlock = SetUpAdBlockAsync(first.Profile);
        var waited = await Task.WhenAny(adBlock, Task.Delay(1500)) == adBlock;
        if (options.InstallExtension is { } install) await InstallAsync(install);
        if (waited) await ReportExtensionsAsync(first.Profile);
        else _ = LateAdBlockAsync();

        async Task LateAdBlockAsync()
        {
            await adBlock;
            await ReportExtensionsAsync(first.Profile);
            if (tabs.Any(t => !t.IsHome && t.Core is not null))
                ShowNotice(new("uBlock Origin Lite is set up. Reload open pages to block their ads.", NoticeKind.Success, "Reload", () => core?.Reload()));
        }

        first.Navigate(tab.Url);
        started = true;
        if (active is { View: null, Loading: null } other) Load(other);
        if (active is not null && Partner(active) is { View: null, Loading: null } partner) Load(partner);
        if (tab.IsHome && tab == active) AddressBox.Focus(FocusState.Programmatic);
        if (startProblem is not null) ShowNotice(new(startProblem, NoticeKind.Warning));
        if (firstRun && startProblem is null)
            ShowNotice(new("Welcome to Lilt. Bring your bookmarks over from another browser, and press Ctrl+K whenever you're looking for something.",
                NoticeKind.Info, "Import bookmarks", () => ShowFlyout(ImportFlyout, BookmarkImportButton, FlyoutHost)));
        if (options.Run is { } script) await RunScriptAsync(script);
    }

    bool firstRun;

    // --run: commands separated by ";". Besides any command name: wait <ms>, open <address>, peek <url>,
    // eval <script in Lilt's page world>, probe (what this PC's engine supports) and state (what's on screen), for tests.
    async Task RunScriptAsync(string script)
    {
        foreach (var step in script.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var space = step.IndexOf(' ');
            var (verb, arg) = space > 0 ? (step[..space], step[(space + 1)..].Trim()) : (step, "");
            report.Key("run", step);
            try
            {
                switch (verb)
                {
                    case "wait": await Task.Delay(Math.Clamp(int.TryParse(arg, out var ms) ? ms : 500, 0, 120000)); break;
                    case "open": AddTab(AddressResolver.Resolve(arg).Uri?.AbsoluteUri ?? BrowserEngine.NewTabUri.AbsoluteUri); break;
                    case "peek": ShowPeek(arg); break;
                    case "eval": report.Key("eval", (core is { } c ? await engine.EvaluateAsync(c, arg, userGesture: true) : null) ?? "null"); break;
                    case "probe": await ProbeAsync(); break;
                    case "state": report.Key("state", State()); break;
                    // Types into the address bar, as if by hand (completion and suggestions included).
                    case "type":
                        AddressBox.Focus(FocusState.Keyboard);
                        await Task.Delay(300);
                        foreach (var ch in arg)
                        {
                            // A key press replaces the selection (an inline completion) with the character.
                            AddressBox.Text = AddressBox.Text[..AddressBox.SelectionStart] + ch;
                            AddressBox.Select(AddressBox.Text.Length, 0);
                            await Task.Delay(60);
                        }
                        await Task.Delay(300);
                        break;
                    case "import": Import(arg, Path.GetFileName(arg)); break;
                    case "go": Go(arg); break;
                    case "dismiss": NoticeBar.IsOpen = false; break;
                    case "workspace" when arg.Length > 0: SwitchWorkspace(Workspace.Clean(arg)); break;
                    case "move": if (active is not null) MoveToWorkspace(active, Workspace.Clean(arg)); break;
                    case "lift": windowMotion?.Lift(arg != "off"); break;
                    case "mark": break;
                    default: RunCommand(verb, "run"); break;
                }
            }
            catch (Exception e)
            {
                report.Key("run", $"failed: {e.Message}");
            }
        }
        report.Key("run", "done");
    }

    string State() => System.Text.Json.JsonSerializer.Serialize(new
    {
        tabs = tabs.Count,
        workspace = Workspace.Display(workspace),
        workspaces = workspaces.Count,
        lifted = windowMotion?.Lifted == true,
        manager = ManagerShown ? managerItems.Count : -1,
        permission = PermissionBar.IsOpen ? PermissionBar.Message : null,
        allTabs = AllTabs.Count(),
        active = active is null ? -1 : tabs.IndexOf(active),
        split = active is not null && Partner(active) is { } p ? tabs.IndexOf(p) : -1,
        floating = floating is null ? -1 : tabs.IndexOf(floating),
        peek = PeekOpen,
        palette = PaletteFlyout.IsOpen,
        player = MediaWidget.Visibility == Visibility.Visible,
        visualizer = VisualizerWidget.Visibility == Visibility.Visible,
        visualizerSource = loopback is not null ? "system" : captureCore is not null ? "page" : "none",
        visualizerFrames = vizFrames,
        visualizerPeak = Math.Round(vizPeak, 3),
        media = mediaTab?.Media is { } m ? new { m.Title, m.Artist, m.Playing, m.Video, m.Duration, m.PictureInPicture } : null,
        power = powerLevel.ToString(),
        shortcuts = Root.KeyboardAccelerators.Count,
        address = AddressBox.Text,
        url = active?.Url,
        suggestions = SuggestPanel.Visibility == Visibility.Visible ? suggestRows.Select(r => r.Title).ToArray() : [],
        bookmarks = bookmarks.Items.Count,
        history = history.Count,
        notice = NoticeBar.IsOpen ? NoticeBar.Message : null,
        noticeAction = NoticeBar.IsOpen && NoticeBar.ActionButton is Button { Content: string label } ? label : null,
        // Screen pixels of the page area's corner and of a point on the pop-out frame's bar, for tests that click and drag.
        page = PageHost.XamlRoot is { } pr ? Native.ClientToScreen(hwnd, PageHost.TransformToVisual(null).TransformPoint(default), pr.RasterizationScale) : null,
        pipBar = PipFrame.Visibility == Visibility.Visible && PipFrame.XamlRoot is { } xr
            ? Native.ClientToScreen(hwnd, PipFrame.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(40, 15)), xr.RasterizationScale)
            : null,
    });

    async Task SetUpAdBlockAsync(CoreWebView2Profile profile)
    {
        try
        {
            await engine.EnsureBundledExtensionsAsync(profile);
        }
        catch (Exception e)
        {
            LiltPaths.LogError(e);
            ShowNotice(new($"The ad blocker couldn't be set up: {e.Message}", NoticeKind.Warning));
        }
    }

    async Task ReportExtensionsAsync(CoreWebView2Profile profile)
    {
        if (!engine.ExtensionsEnabled) return;
        try
        {
            report.Extensions((await engine.ListExtensionsAsync(profile)).Select(x => $"{x.Name} ({x.Id}, {(x.Enabled ? "on" : "off")})"));
        }
        catch (Exception e)
        {
            LiltPaths.LogError(e);
        }
    }

    void OnProcessFailed(BrowserTab tab, CoreWebView2ProcessFailedEventArgs e)
    {
        switch (e.ProcessFailedKind)
        {
            case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                ShowNotice(new("The web engine stopped.", NoticeKind.Error, "Restart Lilt", Restart));
                break;
            case CoreWebView2ProcessFailedKind.RenderProcessUnresponsive:
                ShowNotice(new($"{tab.DisplayTitle} isn't responding.", NoticeKind.Warning, "Reload", () => tab.Core?.Reload()));
                break;
            case CoreWebView2ProcessFailedKind.RenderProcessExited:
            case CoreWebView2ProcessFailedKind.FrameRenderProcessExited:
                ShowNotice(new($"{tab.DisplayTitle} crashed.", NoticeKind.Error, "Reload", () => tab.Core?.Reload()));
                break;
        }
    }

    // ---- Toolbar and navigation ----

    void UpdateNavigation()
    {
        var tab = active;
        BackButton.IsEnabled = tab?.Core?.CanGoBack == true;
        ForwardButton.IsEnabled = tab?.Core?.CanGoForward == true;
        var loading = tab?.IsLoading == true;
        ReloadIcon.Glyph = loading ? "" : "";
        ToolTipService.SetToolTip(ReloadButton, loading ? "Stop" : "Reload (F5)");
        AutomationProperties.SetName(ReloadButton, loading ? "Stop" : "Reload");
        var uri = Uri.TryCreate(tab?.Url, UriKind.Absolute, out var u) ? u : null;
        if (AddressBox.FocusState == FocusState.Unfocused) AddressBox.Text = Display(uri);
        InsecureChip.Visibility = uri is not null && UrlPolicy.IsInsecure(uri) ? Visibility.Visible : Visibility.Collapsed;
        UpdateStar();
    }

    void UpdateTitle()
    {
        var tab = active;
        AppWindow.Title = tab is null || tab.IsHome || string.IsNullOrWhiteSpace(tab.Title) ? appName : $"{tab.Title} – {appName}";
    }

    void Go(string text)
    {
        var result = AddressResolver.Resolve(text, Engine.Template);
        switch (result.Kind)
        {
            case AddressKind.Empty:
                return;
            case AddressKind.Rejected:
                ShowNotice(new(result.Reason!, NoticeKind.Warning));
                return;
            default:
                Navigate(result.Uri!.AbsoluteUri);
                break;
        }
    }

    void Navigate(string url)
    {
        if (active is null) return;
        active.Url = url;
        if (active.Core is { } c) c.Navigate(url);
        else _ = LoadAsync(active, navigate: true);
        active.View?.Focus(FocusState.Programmatic);
    }

    void Back_Click(object sender, RoutedEventArgs e) => core?.GoBack();
    void Forward_Click(object sender, RoutedEventArgs e) => core?.GoForward();
    void Home_Click(object sender, RoutedEventArgs e) => Navigate(BrowserEngine.NewTabUri.AbsoluteUri);

    void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (active?.IsLoading == true) core?.Stop();
        else core?.Reload();
    }

    // Shortcuts come from the theme's "shortcuts" block. They work both from Lilt's chrome (keyboard accelerators,
    // rebuilt when the block changes) and from inside pages (page.js, through the engine).
    ShortcutMap? appliedKeys;

    void ApplyShortcuts(ShortcutMap map)
    {
        if (map.Equals(appliedKeys)) return;
        appliedKeys = map;
        engine.SetShortcuts(map);
        Root.KeyboardAccelerators.Clear();
        void Add(string command, bool ctrl, bool shift, bool alt, int key)
        {
            var accelerator = new KeyboardAccelerator
            {
                Key = (VirtualKey)key,
                Modifiers = (ctrl ? VirtualKeyModifiers.Control : 0) | (shift ? VirtualKeyModifiers.Shift : 0) | (alt ? VirtualKeyModifiers.Menu : 0),
            };
            accelerator.Invoked += (_, e) =>
            {
                e.Handled = true;
                RunCommand(command, "xaml");
            };
            Root.KeyboardAccelerators.Add(accelerator);
        }
        foreach (var (command, gestures) in map.Map)
            foreach (var g in gestures)
                if (g.VirtualKey > 0) Add(command, g.Ctrl, g.Shift, g.Alt, g.VirtualKey);
        for (var n = 1; n <= 9; n++) Add("tab" + n, true, false, false, '0' + n);

        foreach (var (button, label, command) in new (FrameworkElement, string, string)[]
        {
            (BackButton, "Back", "back"), (ForwardButton, "Forward", "forward"), (HomeButton, "Home", "home"), (StarButton, "Bookmark this page", "bookmark"),
            (MediaButton, "Now playing", "media"), (SplitButton, "Split view", "split"), (PaletteButton, "Command palette", "palette"),
            (CustomizeButton, "Customize", "customize"), (SidebarButton, "Sidebar", "sidebar"), (MenuButton, "Menu", "menu"),
            (MediaPip, "Picture in picture", "pip"), (MediaVisualizer, "Visualizer", "visualizer"), (NewTabButton, "New tab", "newTab"),
        })
            ToolTipService.SetToolTip(button, Shortcut(command) is { Length: > 0 } keys ? $"{label} ({keys})" : label);
        foreach (var (item, command) in new (MenuFlyoutItem, string)[]
        {
            (MenuNewTab, "newTab"), (MenuReopen, "reopenTab"), (MenuSplit, "split"), (MenuPalette, "palette"), (MenuBookmark, "bookmark"), (MenuCustomize, "customize"),
            (MenuManager, "bookmarks"), (MenuScreenshot, "screenshot"),
        })
            item.KeyboardAcceleratorTextOverride = Shortcut(command);
    }

    string Shortcut(string command) => (appliedKeys ?? theme.Shortcuts).For(command) is [var first, ..] ? first.ToString() : "";

    void RunCommand(string command, string source)
    {
        report.Key(source, command);
        RunCommand(command, keyboard: source != "run");
    }

    void RunCommand(string command, bool keyboard)
    {
        if (fullScreen && command is not ("closeTab" or "back" or "forward" or "reload" or "pip" or "closePeek")) return;
        switch (command)
        {
            case "home": Navigate(BrowserEngine.NewTabUri.AbsoluteUri); break;
            case "palette": ShowPalette(); break;
            case "split": ToggleSplit(); break;
            case "media": ToggleMediaWidget(); break;
            case "visualizer": ToggleVisualizer(); break;
            case "pip": _ = PipAsync(); break;
            case "closePeek": ClosePeek(); break;
            case "address":
                AddressBox.Focus(FocusState.Keyboard);
                AddressBox.SelectAll();
                break;
            case "sidebar": ToggleSidebar(keyboard); break;
            case "menu": ShowFlyout(MainMenu, MenuButton, MenuHost); break;
            case "customize": ShowCustomize(); break;
            case "bookmark": ToggleBookmark(); break;
            case "back": core?.GoBack(); break;
            case "forward": core?.GoForward(); break;
            case "reload": core?.Reload(); break;
            case "newTab": NewTab(); break;
            case "bookmarks": ShowManager(); break;
            case "screenshot": Screenshot(); break;
            case "workspace": NextWorkspace(); break;
            case "reopenTab": ReopenTab(); break;
            case "closeTab": if (active is not null) CloseTab(active); break;
            case "nextTab": CycleTab(1); break;
            case "prevTab": CycleTab(-1); break;
            case { Length: 4 } when command.StartsWith("tab", StringComparison.Ordinal) && char.IsAsciiDigit(command[3]):
                var n = command[3] - '0';
                if (tabs.Count > 0) Activate(n == 9 ? tabs[^1] : tabs[Math.Min(n, tabs.Count) - 1]);
                break;
        }
    }

    void SetFullScreen(bool on)
    {
        if (fullScreen == on) return;
        fullScreen = on;
        AppWindow.SetPresenter(on ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Overlapped);
        Toolbar.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        LayoutSidebar();
        if (!on) UpdateDragRegions();
    }

    void Restart()
    {
        SaveSession();
        var start = new ProcessStartInfo(Environment.ProcessPath!);
        foreach (var arg in Environment.GetCommandLineArgs().Skip(1)) start.ArgumentList.Add(arg);
        Process.Start(start);
        Close();
    }

    void OnClosed(object sender, WindowEventArgs e)
    {
        closing = true;
        StopVisualizer();
        ClosePeek();
        SaveHistory();
        powerTimer?.Stop();
        suspendTimer.Stop();
        SaveSession();
        engine.CloseWindows();
        themes.Dispose();
        engine.UserContent.Dispose();
        foreach (var tab in AllTabs)
        {
            engine.Detach(tab.Core);
            tab.View?.Close();
        }
    }

    void Screenshot_Click(object sender, RoutedEventArgs e) => Screenshot();
    void Print_Click(object sender, RoutedEventArgs e) => core?.ShowPrintUI(CoreWebView2PrintDialogKind.Browser);
    void DevTools_Click(object sender, RoutedEventArgs e) => core?.OpenDevToolsWindow();
    void CustomizeMenu_Click(object sender, RoutedEventArgs e) => ShowCustomize();
    void CustomizeButton_Click(object sender, RoutedEventArgs e) => ShowCustomize();
    void MenuButton_Click(object sender, RoutedEventArgs e) => ShowFlyout(MainMenu, MenuButton, MenuHost);

    void ShowCustomize(string? page = null)
    {
        if (page is not null) foreach (var item in CustomizeTabs.Items) item.IsSelected = (string)item.Tag == page;
        ShowFlyout(CustomizeFlyout, CustomizeButton, CustomizeHost);
    }

    // Toolbar buttons can be moved or removed, so flyouts open at their button when it's showing, otherwise at the toolbar's edge.
    static void ShowFlyout(FlyoutBase flyout, FrameworkElement button, FrameworkElement fallback) =>
        flyout.ShowAt(button.Parent is not null && button.Visibility == Visibility.Visible ? button : fallback);

    void DownloadsMenu_Click(object sender, RoutedEventArgs e) => ShowFlyout(DownloadsFlyout, DownloadsButton, FlyoutHost);

    // toolbar.items decides order and visibility: ids before "address" go left of it, the rest right.
    // The media button shows only while a tab has media, like the downloads button only once something downloaded.
    Ids? arrangedItems;
    bool arrangedDownloads, arrangedMedia;

    void ArrangeToolbar(Ids items)
    {
        // Theme changes arrive many times a second while a slider moves; only rebuild when the arrangement changed.
        if (items.Equals(arrangedItems) && arrangedDownloads == hasDownloads && arrangedMedia == hasMedia) return;
        arrangedItems = items;
        arrangedDownloads = hasDownloads;
        arrangedMedia = hasMedia;
        var map = new Dictionary<string, FrameworkElement>
        {
            ["back"] = BackButton, ["forward"] = ForwardButton, ["reload"] = ReloadButton, ["home"] = HomeButton,
            ["bookmark"] = StarButton, ["media"] = MediaButton, ["split"] = SplitButton, ["downloads"] = DownloadsButton,
            ["extensions"] = ExtensionsButton, ["palette"] = PaletteButton, ["customize"] = CustomizeButton, ["sidebar"] = SidebarButton, ["menu"] = MenuButton,
        };
        LeftPanel.Children.Clear();
        RightPanel.Children.Clear();
        var split = items.IndexOf("address");
        if (split < 0) split = items.Items.Count;
        for (var i = 0; i < items.Items.Count; i++)
            if (map.TryGetValue(items.Items[i], out var element)) (i < split ? LeftPanel : RightPanel).Children.Add(element);
        foreach (var (id, element) in map)
        {
            var shown = items.Has(id) && (id != "downloads" || hasDownloads) && (id != "media" || hasMedia) && (id != "extensions" || engine.ExtensionsEnabled);
            element.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        }
        AddressHost.Visibility = items.Has("address") ? Visibility.Visible : Visibility.Collapsed;
        UpdateDragRegions();
    }

    void MainMenu_Opening(object sender, object e) => FillImportMenu(ImportMenu.Items);

    // ---- Theme ----

    void ApplyTheme(Theme t, Theme? previous)
    {
        theme = t;
        metrics = ShellMetrics.From(t);
        var highContrast = new AccessibilitySettings().HighContrast;
        var colors = highContrast ? ThemeResources.HighContrast() : t.Colors;
        ThemeResources.Apply(Application.Current.Resources, colors);
        Root.RequestedTheme = highContrast ? ElementTheme.Default : t.Base == ThemeBase.Light ? ElementTheme.Light : ElementTheme.Dark;
        ApplyBackdrop(highContrast ? new BackdropSpec(Backdrop.None) : t.Backdrop, t.Base, colors.Background);
        ApplyCaptionColors(colors);
        // See-through websites need a window material to show.
        engine.Configure(t.Pages.Peek, t.Pages.Gestures, t.Pages.Glass && SystemBackdrop is not null ? t.Pages.GlassOpacity : 0, t.Pages.GlassOffSites.Items);
        engine.SetTracking(t.Pages.Tracking);
        foreach (var tab in AllTabs) SetPageBackground(tab);
        engine.SetPageColorScheme(t.Pages.ColorScheme, t.Base);
        ApplyShortcuts(t.Shortcuts);
        if (previous is not null && (previous.Power.Lean != t.Power.Lean || previous.Media.MediaKeys != t.Media.MediaKeys
            || previous.Pages.ForceDark != t.Pages.ForceDark || previous.Pages.Scrollbars != t.Pages.Scrollbars))
            ShowNotice(new("Restart Lilt to apply this change.", NoticeKind.Info, "Restart now", Restart));
        if (BrowserTab.CloseButtons != t.Sidebar.CloseButtons)
        {
            BrowserTab.CloseButtons = t.Sidebar.CloseButtons;
            foreach (var tab in AllTabs) tab.RefreshCloseButton();
        }
        AddressHost.MaxWidth = t.Toolbar.AddressWidth;
        windowMotion?.Apply(t.Motion.WindowDrag && AnimationsOn, t.Motion.Duration);

        var m = metrics;
        var radius = new CornerRadius(t.Shape.ControlRadius);
        Toolbar.Height = m.ToolbarHeight;
        Toolbar.Padding = new Thickness(m.Unit, 0, 0, 0);
        Toolbar.BorderThickness = new Thickness(0, 0, 0, t.Shape.BorderWidth);
        LeftPanel.Spacing = RightPanel.Spacing = Math.Max(2, m.Unit / 2);
        ArrangeToolbar(t.Toolbar.Items);
        AddressHost.Margin = new Thickness(m.Unit, 0, m.Unit, 0);
        foreach (var b in ChromeButtons()) StyleButton(b);

        var family = new FontFamily(t.Typography.Family);
        var weight = new FontWeight((ushort)t.Typography.Weight);
        AddressBox.MinHeight = 0;
        AddressBox.Height = m.ControlHeight;
        AddressBox.CornerRadius = radius;
        var pad = Math.Max(0, Math.Floor((m.ControlHeight - t.Typography.Size * 1.33) / 2) - 1);
        AddressBox.Padding = new Thickness(m.Unit * 1.5, pad, m.Unit, pad);
        AddressBox.FontFamily = BookmarkSearch.FontFamily = family;
        AddressBox.FontSize = BookmarkSearch.FontSize = t.Typography.Size;
        AddressBox.FontWeight = weight;
        BookmarkSearch.CornerRadius = radius;
        TabList.FontFamily = BookmarkList.FontFamily = family;
        TabList.FontSize = BookmarkList.FontSize = t.Typography.Size;
        foreach (var tb in new[] { ThemeName, ThemeStatus, MemoryText, NoDownloads, InsecureText })
        {
            tb.FontFamily = family;
            tb.FontSize = t.Typography.Size;
            tb.FontWeight = weight;
        }
        // Floating panels follow the theme's shape too.
        foreach (var panel in new[] { MediaWidget, VisualizerWidget, PeekPanel })
        {
            panel.CornerRadius = new CornerRadius(t.Shape.Radius + 2);
            panel.BorderThickness = new Thickness(t.Shape.BorderWidth);
        }
        MediaTitle.FontFamily = MediaArtist.FontFamily = MediaTime.FontFamily = PipTitle.FontFamily = PeekAddress.FontFamily = family;
        TabsTitle.FontFamily = family;
        TabsTitle.FontSize = Math.Max(9, t.Typography.Size - 2);
        AppWindow.TitleBar.PreferredHeightOption = m.TallCaption ? TitleBarHeightOption.Tall : TitleBarHeightOption.Standard;

        // The file is the source of truth for layout, but only fields that changed in it override what you set in the sidebar.
        if (previous is null || previous.Sidebar.Edge != t.Sidebar.Edge) sidebar.Edge = t.Sidebar.Edge;
        if (previous is not null && previous.Sidebar.AutoHide != t.Sidebar.AutoHide) sidebar.SetAutoHide(t.Sidebar.AutoHide);
        LayoutSidebar();

        ThemeName.Text = $"{t.Name} · {(t.Base == ThemeBase.Light ? "light" : "dark")}";
        WriteHomeData();
        LayoutPages();
        UpdateVisualizer();
        UpdatePower();
        UpdateDragRegions();
    }

    void StyleButton(Control b)
    {
        b.Width = b.Height = metrics.ControlHeight;
        b.CornerRadius = new CornerRadius(theme.Shape.ControlRadius);
        if (b is ContentControl { Content: FontIcon icon }) icon.FontSize = metrics.IconSize;
    }

    Control[] ChromeButtons() =>
    [
        BackButton, ForwardButton, ReloadButton, HomeButton, StarButton, MediaButton, SplitButton, PaletteButton, DownloadsButton, ExtensionsButton,
        CustomizeButton, SidebarButton, MenuButton,
        NewTabButton, BookmarkImportButton, EdgeLeft, EdgeRight, EdgeTop, EdgeBottom, PinToggle,
        ReloadThemeButton, EditThemeButton, DataFolderButton, UserContentButton, MeasureButton,
    ];

    void ApplyBackdrop(BackdropSpec spec, ThemeBase themeBase, Rgba fallback)
    {
        var supported = GlassBackdrop.IsSupported(spec.Material);
        var key = supported ? $"{spec}|{themeBase}|{fallback}" : "none";
        if (key != backdropKey)
        {
            SystemBackdrop = supported ? new GlassBackdrop(spec, themeBase, fallback) : null;
            backdropKey = key;
        }
        Root.Background = supported ? new SolidColorBrush(Colors.Transparent) : ThemeResources.Get("LiltBackgroundBrush");
        report.Backdrop = (supported ? spec.Material.ToString() : $"solid ({spec.Material} unsupported)") + (uiSettings.AdvancedEffectsEnabled ? "" : ", transparency effects off in Windows");
        backdropNote = supported || spec.Material == Backdrop.None ? "" : $" · {spec.Material} unavailable, using a solid background";
    }

    void ApplyCaptionColors(ThemeColors c)
    {
        var bar = AppWindow.TitleBar;
        bar.ButtonBackgroundColor = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        bar.ButtonForegroundColor = ThemeResources.ToColor(c.Text);
        bar.ButtonInactiveForegroundColor = ThemeResources.ToColor(c.TextMuted);
        bar.ButtonHoverBackgroundColor = ThemeResources.ToColor(c.Hover);
        bar.ButtonHoverForegroundColor = ThemeResources.ToColor(c.Text);
        bar.ButtonPressedBackgroundColor = ThemeResources.ToColor(c.Pressed);
        bar.ButtonPressedForegroundColor = ThemeResources.ToColor(c.Text);
    }

    // The home tab can be see-through (its page is transparent and so is the view behind it); every other page gets the theme's page color.
    void SetPageBackground(BrowserTab tab, string? url = null)
    {
        if (tab.View is null) return;
        var home = Uri.TryCreate(url ?? tab.Url, UriKind.Absolute, out var u) && u.Host == BrowserEngine.NewTabHost;
        tab.View.DefaultBackgroundColor = (home && HomeGlass) || engine.GlassFor(url ?? tab.Url) ? Colors.Transparent : ThemeResources.ToColor(theme.Colors.Page.Opaque);
    }

    void OnThemeReloaded(ThemeLoadResult result)
    {
        if (result.Failed)
        {
            themeLoadFailed = true;
            ThemeStatus.Text = $"Last save had errors · previous theme kept · {DateTime.Now:t}";
            ShowNotice(new($"Theme not applied: {result.Issues[0]}. Keeping the previous theme.", NoticeKind.Error, "Edit", OpenThemeFile));
            return;
        }
        ApplyTheme(result.Theme, theme);
        ShowThemeLoad(result, startup: false);
    }

    void ShowThemeLoad(ThemeLoadResult result, bool startup)
    {
        var n = result.Issues.Count;
        themeLoadFailed = result.Failed;
        ThemeStatus.Text = result.Failed
            ? "Using the built-in theme"
            : n == 0 ? $"Applied {DateTime.Now:t}{backdropNote}" : $"{n} warning{(n == 1 ? "" : "s")} · {DateTime.Now:t}{backdropNote}";
        if (n == 0)
        {
            if (!startup && NoticeBar.Severity == InfoBarSeverity.Warning) NoticeBar.IsOpen = false;
            return;
        }
        var list = string.Join("\n", result.Issues.Take(4).Select(i => "• " + i));
        if (n > 4) list += $"\n…and {n - 4} more";
        ShowNotice(new(result.Failed ? $"Theme couldn't load; using the built-in one.\n{list}" : $"Theme loaded with fixes:\n{list}",
            result.Failed ? NoticeKind.Error : NoticeKind.Warning, "Edit", OpenThemeFile));
    }

    void ReloadTheme_Click(object sender, RoutedEventArgs e) => themes.Reload();
    void EditTheme_Click(object sender, RoutedEventArgs e) => OpenThemeFile();
    void OpenDataFolder_Click(object sender, RoutedEventArgs e) => Open("explorer.exe", paths.Root);
    void OpenUserContent_Click(object sender, RoutedEventArgs e) => Open("explorer.exe", engine.UserContent.Folder);

    void OpenThemeFile()
    {
        if (themes.FilePath is null)
        {
            ShowNotice(new("There's no theme file to edit; Lilt's data folder isn't writable.", NoticeKind.Warning));
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(themes.FilePath) { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            Open("notepad.exe", themes.FilePath);
        }
    }

    static void Open(string program, string argument)
    {
        try
        {
            var start = new ProcessStartInfo(program);
            start.ArgumentList.Add(argument);
            Process.Start(start);
        }
        catch (Win32Exception e)
        {
            LiltPaths.LogError(e);
        }
    }

    // ---- Sidebar ----

    void LayoutSidebar()
    {
        var edge = sidebar.Edge;
        var vertical = edge.IsVertical();
        var spec = theme.Sidebar;
        var bw = theme.Shape.BorderWidth;
        if (sidebar.AutoHide)
        {
            Grid.SetRow(Sidebar, 1);
            Grid.SetColumn(Sidebar, 1);
            Grid.SetRowSpan(Sidebar, 1);
            Grid.SetColumnSpan(Sidebar, 1);
            Sidebar.Margin = new Thickness(metrics.Unit);
            Sidebar.CornerRadius = new CornerRadius(theme.Shape.Radius);
            Sidebar.BorderThickness = new Thickness(bw);
            Sidebar.Background = ThemeResources.Get("LiltOverlayBrush");
        }
        else
        {
            var (row, col, rows, cols) = edge switch
            {
                DockEdge.Left => (0, 0, 3, 1),
                DockEdge.Right => (0, 2, 3, 1),
                DockEdge.Top => (0, 0, 1, 3),
                _ => (2, 0, 1, 3),
            };
            Grid.SetRow(Sidebar, row);
            Grid.SetColumn(Sidebar, col);
            Grid.SetRowSpan(Sidebar, rows);
            Grid.SetColumnSpan(Sidebar, cols);
            Sidebar.Margin = new Thickness(0);
            Sidebar.CornerRadius = new CornerRadius(0);
            Sidebar.BorderThickness = edge switch
            {
                DockEdge.Left => new Thickness(0, 0, bw, 0),
                DockEdge.Right => new Thickness(bw, 0, 0, 0),
                DockEdge.Top => new Thickness(0, 0, 0, bw),
                _ => new Thickness(0, bw, 0, 0),
            };
            Sidebar.Background = ThemeResources.Get("LiltSurfaceBrush");
        }
        Sidebar.Width = vertical ? spec.Width : double.NaN;
        Sidebar.Height = vertical ? double.NaN : spec.Height;
        Sidebar.HorizontalAlignment = edge switch
        {
            DockEdge.Left => HorizontalAlignment.Left,
            DockEdge.Right => HorizontalAlignment.Right,
            _ => HorizontalAlignment.Stretch,
        };
        Sidebar.VerticalAlignment = edge switch
        {
            DockEdge.Top => VerticalAlignment.Top,
            DockEdge.Bottom => VerticalAlignment.Bottom,
            _ => VerticalAlignment.Stretch,
        };

        // sidebar.sections sets the order and which sections show. Vertical: stacked, the tab list (or else the bookmark
        // list) takes the spare height. Horizontal: a strip; bookmarks don't fit a strip and stay hidden there.
        var u = metrics.Unit;
        SidebarGrid.RowDefinitions.Clear();
        SidebarGrid.ColumnDefinitions.Clear();
        SidebarGrid.Padding = vertical ? new Thickness(u * 1.5) : new Thickness(u, 0, u, 0);
        SidebarGrid.RowSpacing = SidebarGrid.ColumnSpacing = u;
        var sections = spec.Sections;
        var showTabs = sections.Has("tabs");
        var showBookmarks = vertical && sections.Has("bookmarks");
        var star = new GridLength(1, GridUnitType.Star);
        var slots = new List<(FrameworkElement Part, GridLength Size)>();
        foreach (var id in sections.Items)
        {
            if (id == "tabs") slots.AddRange([(TabsHeader, GridLength.Auto), (TabList, star)]);
            else if (id == "bookmarks" && showBookmarks) slots.Add((BookmarksPanel, showTabs ? GridLength.Auto : star));
            else if (id == "layout") slots.Add((SidebarFooter, GridLength.Auto));
        }
        for (var i = 0; i < slots.Count; i++)
        {
            if (vertical) SidebarGrid.RowDefinitions.Add(new RowDefinition { Height = slots[i].Size });
            else SidebarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = slots[i].Size });
            Grid.SetRow(slots[i].Part, vertical ? i : 0);
            Grid.SetColumn(slots[i].Part, vertical ? 0 : i);
        }
        TabsHeader.Visibility = TabList.Visibility = showTabs ? Visibility.Visible : Visibility.Collapsed;
        BookmarksPanel.Visibility = showBookmarks ? Visibility.Visible : Visibility.Collapsed;
        SidebarFooter.Visibility = sections.Has("layout") ? Visibility.Visible : Visibility.Collapsed;
        BookmarkList.MaxHeight = showTabs ? 300 : double.PositiveInfinity;
        BookmarksPanel.RowDefinitions[2].Height = showTabs ? GridLength.Auto : new GridLength(1, GridUnitType.Star);

        TabsHeader.VerticalAlignment = SidebarFooter.VerticalAlignment = vertical ? VerticalAlignment.Stretch : VerticalAlignment.Center;
        SetTabStripOrientation(vertical);
        UpdateWorkspaceButton();

        foreach (var (button, e) in EdgeToggles()) button.IsChecked = e == edge;
        PinToggle.IsChecked = !sidebar.AutoHide;
        ShowSidebar(sidebar.Open, animate: false);
    }

    (ToggleButton Button, DockEdge Edge)[] EdgeToggles() =>
        [(EdgeLeft, DockEdge.Left), (EdgeRight, DockEdge.Right), (EdgeTop, DockEdge.Top), (EdgeBottom, DockEdge.Bottom)];

    void ShowSidebar(bool open, bool animate)
    {
        sidebarShown = open;
        var generation = ++slideGeneration;
        if (fullScreen)
        {
            Sidebar.Visibility = Visibility.Collapsed;
            return;
        }
        if (!sidebar.AutoHide)
        {
            Slide(0, 0, animate: false);
            Sidebar.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            return;
        }
        if (open)
        {
            Sidebar.Visibility = Visibility.Visible;
            Slide(0, 0, animate);
            return;
        }
        pointerInBand = false;
        if (IsFocusWithin(Sidebar)) active?.View?.Focus(FocusState.Programmatic);
        var (x, y) = HiddenOffset();
        Slide(x, y, animate, () =>
        {
            if (generation == slideGeneration && !sidebarShown) Sidebar.Visibility = Visibility.Collapsed;
        });
    }

    (double X, double Y) HiddenOffset()
    {
        var away = (sidebar.Edge.IsVertical() ? theme.Sidebar.Width : theme.Sidebar.Height) + metrics.Unit + 4;
        return sidebar.Edge switch
        {
            DockEdge.Left => (-away, 0),
            DockEdge.Right => (away, 0),
            DockEdge.Top => (0, -away),
            _ => (0, away),
        };
    }

    bool AnimationsOn => theme.Motion.Animations && uiSettings.AnimationsEnabled;

    // Always animated through a storyboard (zero length when motion is off) so the held value stays consistent.
    void Slide(double x, double y, bool animate, Action? done = null)
    {
        var duration = TimeSpan.FromMilliseconds(animate && AnimationsOn ? theme.Motion.Duration : 0);
        var storyboard = new Storyboard();
        foreach (var (property, to) in new[] { ("X", x), ("Y", y) })
        {
            var animation = new DoubleAnimation { To = to, Duration = duration, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            Storyboard.SetTarget(animation, SlideTransform);
            Storyboard.SetTargetProperty(animation, property);
            storyboard.Children.Add(animation);
        }
        if (done is not null) storyboard.Completed += (_, _) => done();
        storyboard.Begin();
    }

    void SyncSidebar()
    {
        if (sidebar.HidePending)
        {
            if (!hideTimer.IsRunning)
            {
                hideTimer.Interval = TimeSpan.FromMilliseconds(theme.Sidebar.HideDelay);
                hideTimer.Start();
            }
        }
        else
        {
            hideTimer.Stop();
        }
        if (sidebar.Open != sidebarShown) ShowSidebar(sidebar.Open, animate: sidebar.AutoHide);
    }

    double Band() => (sidebar.Edge.IsVertical() ? theme.Sidebar.Width : theme.Sidebar.Height) + metrics.Unit * 2;

    // Pointer moves over the page arrive here as handled XAML events (WinUI forwards them to WebView2),
    // so the edge reveal needs no hooks, timers or cursor polling.
    void Content_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (dragging is not null)
        {
            DragTo(e);
            return;
        }
        if (!sidebar.AutoHide || fullScreen) return;
        var point = e.GetCurrentPoint(ContentArea);
        var (x, y) = (point.Position.X, point.Position.Y);
        var (w, h) = (ContentArea.ActualWidth, ContentArea.ActualHeight);
        if (!sidebar.Open)
        {
            var p = point.Properties;
            var pressed = p.IsLeftButtonPressed || p.IsRightButtonPressed || p.IsMiddleButtonPressed;
            if (!sidebar.Edge.NearEdge(x, y, w, h, theme.Sidebar.RevealEdge) || !sidebar.PointerAtEdge(pressed)) return;
            pointerInBand = true;
            sidebar.PointerEntered();
            SyncSidebar();
            return;
        }
        var inBand = sidebar.Edge.NearEdge(x, y, w, h, Band());
        if (inBand == pointerInBand) return;
        pointerInBand = inBand;
        if (inBand) sidebar.PointerEntered();
        else sidebar.PointerExited();
        SyncSidebar();
    }

    void Content_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!sidebar.AutoHide || !pointerInBand) return;
        var p = e.GetCurrentPoint(ContentArea).Position;
        if (p.X > 0 && p.Y > 0 && p.X < ContentArea.ActualWidth && p.Y < ContentArea.ActualHeight) return;
        pointerInBand = false;
        sidebar.PointerExited();
        SyncSidebar();
    }

    void Content_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!sidebar.AutoHide || !sidebar.Open) return;
        var p = e.GetCurrentPoint(ContentArea).Position;
        if (sidebar.Edge.NearEdge(p.X, p.Y, ContentArea.ActualWidth, ContentArea.ActualHeight, Band())) return;
        sidebar.Dismiss();
        SyncSidebar();
    }

    void Sidebar_GotFocus(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not Control { FocusState: FocusState.Keyboard }) return;
        sidebar.FocusEntered();
        SyncSidebar();
    }

    void Sidebar_LostFocus(object sender, RoutedEventArgs e)
    {
        sidebar.FocusExited();
        SyncSidebar();
    }

    void Sidebar_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape) return;
        e.Handled = true;
        sidebar.Dismiss();
        SyncSidebar();
        active?.View?.Focus(FocusState.Programmatic);
    }

    void SidebarButton_Click(object sender, RoutedEventArgs e) => ToggleSidebar(keyboard: false);

    void ToggleSidebar(bool keyboard)
    {
        if (fullScreen) return;
        sidebar.Toggle();
        pointerInBand = false;
        SyncSidebar();
        if (!sidebar.Open || !keyboard) return;
        if (active is not null) TabList.Focus(FocusState.Keyboard);
    }

    void Edge_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string tag } && Enum.TryParse<DockEdge>(tag, out var edge)) sidebar.Edge = edge;
        LayoutSidebar();
    }

    void Pin_Click(object sender, RoutedEventArgs e)
    {
        sidebar.SetAutoHide(PinToggle.IsChecked != true);
        pointerInBand = false;
        LayoutSidebar();
    }

    bool IsFocusWithin(UIElement ancestor)
    {
        if (Root.XamlRoot is null) return false;
        var node = FocusManager.GetFocusedElement(Root.XamlRoot) as DependencyObject;
        while (node is not null)
        {
            if (ReferenceEquals(node, ancestor)) return true;
            node = VisualTreeHelper.GetParent(node);
        }
        return false;
    }

    // ---- Title bar ----

    void UpdateDragRegions()
    {
        if (Toolbar.XamlRoot is not { } root || fullScreen) return;
        var scale = root.RasterizationScale;
        CaptionColumn.Width = new GridLength(AppWindow.TitleBar.RightInset / scale);
        var rects = new List<RectInt32>();
        foreach (var element in new FrameworkElement[] { LeftPanel, AddressHost, RightPanel })
        {
            if (element.ActualWidth <= 0 || element.Visibility != Visibility.Visible) continue;
            var b = element.TransformToVisual(null).TransformBounds(new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));
            rects.Add(new RectInt32((int)Math.Round(b.X * scale), (int)Math.Round(b.Y * scale), (int)Math.Round(b.Width * scale), (int)Math.Round(b.Height * scale)));
        }
        InputNonClientPointerSource.GetForWindowId(AppWindow.Id).SetRegionRects(NonClientRegionKind.Passthrough, rects.ToArray());
    }

    // ---- Notices ----

    // Information and confirmations go away by themselves (later if there's a button, and not while the pointer is on
    // them); warnings and errors stay until closed.
    DispatcherQueueTimer? noticeTimer;
    TimeSpan noticeLife;

    void ShowNotice(Notice notice)
    {
        NoticeBar.Severity = notice.Kind switch
        {
            NoticeKind.Success => InfoBarSeverity.Success,
            NoticeKind.Warning => InfoBarSeverity.Warning,
            NoticeKind.Error => InfoBarSeverity.Error,
            _ => InfoBarSeverity.Informational,
        };
        NoticeBar.Message = notice.Message;
        NoticeBar.ActionButton = null;
        if (notice is { ActionLabel: { } label, Action: { } action })
        {
            var button = new Button { Content = label };
            button.Click += (_, _) =>
            {
                NoticeBar.IsOpen = false;
                action();
            };
            NoticeBar.ActionButton = button;
        }
        NoticeBar.IsOpen = true;
        if (noticeTimer is not { } timer)
        {
            noticeTimer = timer = DispatcherQueue.CreateTimer();
            timer.IsRepeating = false;
            timer.Tick += (_, _) => NoticeBar.IsOpen = false;
            NoticeBar.PointerEntered += (_, _) => timer.Stop();
            NoticeBar.PointerExited += (_, _) =>
            {
                if (noticeLife > TimeSpan.Zero && NoticeBar.IsOpen) timer.Start();
            };
        }
        timer.Stop();
        noticeLife = notice.Kind is NoticeKind.Info or NoticeKind.Success ? TimeSpan.FromSeconds(notice.Action is null ? 6 : 15) : TimeSpan.Zero;
        if (noticeLife <= TimeSpan.Zero) return;
        timer.Interval = noticeLife;
        timer.Start();
    }

    // ---- Diagnostics ----

    void Measure_Click(object sender, RoutedEventArgs e) =>
        MemoryText.Text = $"{MemorySnapshot.Format(MemorySnapshot.Take(engine.Environment))} · {DateTime.Now:t}";
}
