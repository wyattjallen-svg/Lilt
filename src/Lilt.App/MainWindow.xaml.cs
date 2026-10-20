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
    readonly string appName;
    Theme theme = Theme.Default;
    ShellMetrics metrics;
    string backdropKey = "";
    string backdropNote = "";
    bool sidebarShown, pointerInBand, fullScreen, firstNavigation = true;
    int slideGeneration;

    CoreWebView2? core => active?.Core;

    public MainWindow(LaunchOptions options)
    {
        this.options = options;
        report = new StartupReport(options.StartupReportPath);
        paths = LiltPaths.Resolve(options.UserDataDir);
        var queue = DispatcherQueue.GetForCurrentThread();
        engine = new BrowserEngine(new EngineOptions(paths.WebView2, paths.PrepareNewTab(), paths.PrepareUserContent(), paths.Extensions,
            options.InPrivate, options.UserAgent, options.BrowserArgs, !options.NoExtensions), queue);

        themes = new ThemeService(paths.PrepareTheme(options.ThemePath), queue);
        var load = themes.Load();
        var typography = themes.Current.Typography;
        Application.Current.Resources["ContentControlThemeFontFamily"] = new FontFamily(typography.Family);
        Application.Current.Resources["ControlContentThemeFontSize"] = typography.Size;
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
        InitTabs();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(Toolbar);
        appName = options.InPrivate ? "Lilt (InPrivate)" : "Lilt";
        AppWindow.Title = appName;
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
            if (tabs.Any(t => t.Core == sender)) RunCommand(command);
        };
        engine.OpenTabForPage = OpenTabForPageAsync;
        engine.OpenUrlInTab = url => AddTab(url);
        AdBlockMenuItem.Visibility = ExtensionsMenuItem.Visibility = engine.ExtensionsEnabled ? Visibility.Visible : Visibility.Collapsed;

        ContentArea.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(Content_PointerMoved), true);
        ContentArea.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(Content_PointerPressed), true);
        ContentArea.AddHandler(UIElement.PointerExitedEvent, new PointerEventHandler(Content_PointerExited), true);
        ContentArea.SizeChanged += (_, e) => ContentArea.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };
        foreach (var element in new FrameworkElement[] { Toolbar, NavPanel, AddressHost, ActionsPanel })
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

        // Extensions are set up before the first page loads, so the ad blocker covers it.
        try
        {
            await engine.EnsureBundledExtensionsAsync(first.Profile);
        }
        catch (Exception e)
        {
            LiltPaths.LogError(e);
            ShowNotice(new($"The ad blocker couldn't be set up: {e.Message}", NoticeKind.Warning));
        }
        if (options.InstallExtension is { } install) await InstallAsync(install);
        if (engine.ExtensionsEnabled)
        {
            try
            {
                report.Extensions((await engine.ListExtensionsAsync(first.Profile)).Select(x => $"{x.Name} ({x.Id}, {(x.Enabled ? "on" : "off")})"));
            }
            catch (Exception e)
            {
                LiltPaths.LogError(e);
            }
        }

        first.Navigate(tab.Url);
        if (tab.IsHome && tab == active) AddressBox.Focus(FocusState.Programmatic);
        if (startProblem is not null) ShowNotice(new(startProblem, NoticeKind.Warning));
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
        var loading = tab?.IsLoadingPage == true;
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

    static string Display(Uri? uri) =>
        uri is null || UrlPolicy.IsBlank(uri) || uri.Host == BrowserEngine.NewTabHost ? "" : uri.AbsoluteUri;

    void Go(string text)
    {
        var result = AddressResolver.Resolve(text);
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

    void AddressBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            Go(AddressBox.Text);
        }
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            AddressBox.Text = Display(Uri.TryCreate(active?.Url, UriKind.Absolute, out var u) ? u : null);
            active?.View?.Focus(FocusState.Programmatic);
        }
    }

    // A lambda, not a method group: CsWinRT can't marshal a delegate whose target is a projected control.
    void AddressBox_GotFocus(object sender, RoutedEventArgs e) => DispatcherQueue.TryEnqueue(() => AddressBox.SelectAll());

    void Back_Click(object sender, RoutedEventArgs e) => core?.GoBack();
    void Forward_Click(object sender, RoutedEventArgs e) => core?.GoForward();
    void Home_Click(object sender, RoutedEventArgs e) => Navigate(BrowserEngine.NewTabUri.AbsoluteUri);

    void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (active?.IsLoadingPage == true) core?.Stop();
        else core?.Reload();
    }

    // Shortcuts work both from Lilt's chrome (keyboard accelerators) and from inside pages (the engine's key shim).
    void Accelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        var ctrl = sender.Modifiers.HasFlag(VirtualKeyModifiers.Control);
        var shift = sender.Modifiers.HasFlag(VirtualKeyModifiers.Shift);
        var alt = sender.Modifiers.HasFlag(VirtualKeyModifiers.Menu);
        var command = sender.Key switch
        {
            VirtualKey.L or VirtualKey.F6 => "address",
            VirtualKey.D when alt => "address",
            VirtualKey.D => "bookmark",
            VirtualKey.B => "sidebar",
            VirtualKey.Left => "back",
            VirtualKey.Right => "forward",
            VirtualKey.F5 or VirtualKey.R => "reload",
            VirtualKey.T when shift => "reopenTab",
            VirtualKey.T => "newTab",
            VirtualKey.W => "closeTab",
            VirtualKey.Tab when shift => "prevTab",
            VirtualKey.Tab => "nextTab",
            >= VirtualKey.Number1 and <= VirtualKey.Number9 when ctrl => "tab" + (sender.Key - VirtualKey.Number0),
            _ => null,
        };
        if (command is null) return;
        e.Handled = true;
        RunCommand(command, keyboard: true);
    }

    void RunCommand(string command) => RunCommand(command, keyboard: true);

    void RunCommand(string command, bool keyboard)
    {
        if (fullScreen && command is not ("closeTab" or "back" or "forward" or "reload")) return;
        switch (command)
        {
            case "address":
                AddressBox.Focus(FocusState.Keyboard);
                AddressBox.SelectAll();
                break;
            case "sidebar": ToggleSidebar(keyboard); break;
            case "bookmark": ToggleBookmark(); break;
            case "back": core?.GoBack(); break;
            case "forward": core?.GoForward(); break;
            case "reload": core?.Reload(); break;
            case "newTab": NewTab(); break;
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
        SaveSession();
        engine.CloseWindows();
        themes.Dispose();
        engine.UserContent.Dispose();
        foreach (var tab in tabs)
        {
            engine.Detach(tab.Core);
            tab.View?.Close();
        }
    }

    void Print_Click(object sender, RoutedEventArgs e) => core?.ShowPrintUI();
    void DevTools_Click(object sender, RoutedEventArgs e) => core?.OpenDevToolsWindow();
    void CustomizeMenu_Click(object sender, RoutedEventArgs e) => CustomizeFlyout.ShowAt(CustomizeButton);

    void DownloadsMenu_Click(object sender, RoutedEventArgs e)
    {
        DownloadsButton.Visibility = Visibility.Visible;
        DownloadsFlyout.ShowAt(DownloadsButton);
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
        foreach (var tab in tabs) SetPageBackground(tab);
        engine.SetPageColorScheme(t.Pages.ColorScheme, t.Base);

        var m = metrics;
        var radius = new CornerRadius(t.Shape.ControlRadius);
        Toolbar.Height = m.ToolbarHeight;
        Toolbar.Padding = new Thickness(m.Unit, 0, 0, 0);
        Toolbar.BorderThickness = new Thickness(0, 0, 0, t.Shape.BorderWidth);
        NavPanel.Spacing = ActionsPanel.Spacing = Math.Max(2, m.Unit / 2);
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
        TabsTitle.FontFamily = family;
        TabsTitle.FontSize = Math.Max(9, t.Typography.Size - 2);
        AppWindow.TitleBar.PreferredHeightOption = m.TallCaption ? TitleBarHeightOption.Tall : TitleBarHeightOption.Standard;

        // The file is the source of truth for layout, but only fields that changed in it override what you set in the sidebar.
        if (previous is null || previous.Sidebar.Edge != t.Sidebar.Edge) sidebar.Edge = t.Sidebar.Edge;
        if (previous is not null && previous.Sidebar.AutoHide != t.Sidebar.AutoHide) sidebar.SetAutoHide(t.Sidebar.AutoHide);
        LayoutSidebar();

        ThemeName.Text = $"{t.Name} · {(t.Base == ThemeBase.Light ? "light" : "dark")}";
        WriteHomeData();
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
        BackButton, ForwardButton, ReloadButton, HomeButton, StarButton, DownloadsButton, CustomizeButton, SidebarButton, MenuButton,
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
        tab.View.DefaultBackgroundColor = home && HomeGlass ? Colors.Transparent : ThemeResources.ToColor(theme.Colors.Page.Opaque);
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

        // Vertical: tabs, bookmarks, then the layout buttons stacked. Horizontal: a tab strip with the layout buttons at the end.
        var u = metrics.Unit;
        SidebarGrid.RowDefinitions.Clear();
        SidebarGrid.ColumnDefinitions.Clear();
        SidebarGrid.Padding = vertical ? new Thickness(u * 1.5) : new Thickness(u, 0, u, 0);
        SidebarGrid.RowSpacing = SidebarGrid.ColumnSpacing = u;
        FrameworkElement[] parts = [TabsHeader, TabList, BookmarksPanel, SidebarFooter];
        var star = new GridLength(1, GridUnitType.Star);
        var sizes = vertical
            ? new[] { GridLength.Auto, star, GridLength.Auto, GridLength.Auto }
            : new[] { GridLength.Auto, star, new GridLength(0), GridLength.Auto };
        for (var i = 0; i < parts.Length; i++)
        {
            if (vertical) SidebarGrid.RowDefinitions.Add(new RowDefinition { Height = sizes[i] });
            else SidebarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = sizes[i] });
            Grid.SetRow(parts[i], vertical ? i : 0);
            Grid.SetColumn(parts[i], vertical ? 0 : i);
        }
        BookmarksPanel.Visibility = vertical ? Visibility.Visible : Visibility.Collapsed;
        TabsTitle.Visibility = vertical ? Visibility.Visible : Visibility.Collapsed;
        TabsHeader.VerticalAlignment = SidebarFooter.VerticalAlignment = vertical ? VerticalAlignment.Stretch : VerticalAlignment.Center;
        SetTabStripOrientation(vertical);

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
        foreach (var element in new FrameworkElement[] { NavPanel, AddressHost, ActionsPanel })
        {
            if (element.ActualWidth <= 0 || element.Visibility != Visibility.Visible) continue;
            var b = element.TransformToVisual(null).TransformBounds(new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));
            rects.Add(new RectInt32((int)Math.Round(b.X * scale), (int)Math.Round(b.Y * scale), (int)Math.Round(b.Width * scale), (int)Math.Round(b.Height * scale)));
        }
        InputNonClientPointerSource.GetForWindowId(AppWindow.Id).SetRegionRects(NonClientRegionKind.Passthrough, rects.ToArray());
    }

    // ---- Notices ----

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
    }

    // ---- Diagnostics ----

    void Measure_Click(object sender, RoutedEventArgs e) =>
        MemoryText.Text = $"{MemorySnapshot.Format(MemorySnapshot.Take(engine.Environment))} · {DateTime.Now:t}";
}
