using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Lilt.App.Browser;
using Lilt.App.Shell;
using Lilt.Core;
using Lilt.Core.Layout;
using Lilt.Core.Navigation;
using Lilt.Core.Theming;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
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
using Windows.System;
using Windows.UI.Text;
using Windows.UI.ViewManagement;

namespace Lilt.App;

public sealed partial class MainWindow : Window
{
    readonly LiltPaths paths;
    readonly ThemeService themes;
    readonly BrowserEngine engine;
    readonly SidebarState sidebar;
    readonly DispatcherQueueTimer hideTimer;
    readonly StartupReport report;
    readonly UISettings uiSettings = new();
    readonly WebView2 view = new();
    readonly Uri startUri;
    readonly string? startProblem;
    readonly string appName;
    CoreWebView2? core;
    Theme theme = Theme.Default;
    ShellMetrics metrics;
    Backdrop? activeBackdrop;
    string backdropNote = "";
    bool sidebarShown, pointerInBand, fullScreen, loading, firstNavigation = true;
    int slideGeneration;

    public MainWindow(LaunchOptions options)
    {
        report = new StartupReport(options.StartupReportPath);
        paths = LiltPaths.Resolve(options.UserDataDir);
        var queue = DispatcherQueue.GetForCurrentThread();
        engine = new BrowserEngine(new EngineOptions(paths.WebView2, paths.PrepareNewTab(), paths.PrepareUserContent(),
            options.InPrivate, options.UserAgent, options.BrowserArgs), queue);

        themes = new ThemeService(paths.PrepareTheme(options.ThemePath), queue);
        var load = themes.Load();
        var typography = themes.Current.Typography;
        Application.Current.Resources["ContentControlThemeFontFamily"] = new FontFamily(typography.Family);
        Application.Current.Resources["ControlContentThemeFontSize"] = typography.Size;

        InitializeComponent();

        var start = AddressResolver.Resolve(options.Url);
        startUri = start.Kind is AddressKind.Navigate or AddressKind.Search ? start.Uri! : BrowserEngine.NewTabUri;
        startProblem = start.Kind == AddressKind.Rejected ? start.Reason : null;

        sidebar = new SidebarState(themes.Current.Sidebar.Edge, themes.Current.Sidebar.AutoHide);
        hideTimer = DispatcherQueue.CreateTimer();
        hideTimer.IsRepeating = false;
        hideTimer.Tick += (_, _) =>
        {
            sidebar.HideDelayElapsed();
            SyncSidebar();
        };

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(Toolbar);
        appName = options.InPrivate ? "Lilt (InPrivate)" : "Lilt";
        AppWindow.Title = appName;
        WindowSizing.Place(this, 1280, 820);

        PageHost.Children.Add(view);
        ApplyTheme(themes.Current, previous: null);
        ShowThemeLoad(load, startup: true);
        themes.Reloaded += OnThemeReloaded;
        themes.Watch();

        engine.Noticed += ShowNotice;
        engine.DownloadStarted += OnDownloadStarted;

        ContentArea.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(Content_PointerMoved), true);
        ContentArea.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(Content_PointerPressed), true);
        ContentArea.AddHandler(UIElement.PointerExitedEvent, new PointerEventHandler(Content_PointerExited), true);
        ContentArea.SizeChanged += (_, e) => ContentArea.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };
        foreach (var element in new FrameworkElement[] { Toolbar, NavPanel, AddressHost, SidebarButton })
            element.SizeChanged += (_, _) => UpdateDragRegions();
        Root.Loaded += (_, _) =>
        {
            Root.XamlRoot.Changed += (_, _) => UpdateDragRegions();
            UpdateDragRegions();
        };

        Activated += (_, e) =>
        {
            if (e.WindowActivationState != WindowActivationState.Deactivated) report.WindowShown();
        };
        Closed += OnClosed;

        _ = StartAsync();
    }

    // ---- Engine and navigation ----

    async Task StartAsync()
    {
        try
        {
            await engine.WhenEnvironmentReady;
            report.EnvironmentReady();
            core = await engine.AttachAsync(view);
        }
        catch (Exception e)
        {
            LiltPaths.LogError(e);
            ShowNotice(new($"The web engine couldn't start: {e.Message} Lilt needs the Microsoft Edge WebView2 Runtime, which ships with Windows 11.", NoticeKind.Error));
            return;
        }
        report.EngineReady();

        core.NavigationStarting += (_, _) =>
        {
            loading = true;
            UpdateNavigation();
        };
        core.NavigationCompleted += OnNavigationCompleted;
        core.SourceChanged += (_, _) => UpdateNavigation();
        core.HistoryChanged += (_, _) => UpdateNavigation();
        core.DocumentTitleChanged += (_, _) => UpdateTitle();
        core.ContainsFullScreenElementChanged += (_, _) => SetFullScreen(core.ContainsFullScreenElement);
        core.ProcessFailed += OnProcessFailed;

        core.Navigate(startUri.AbsoluteUri);
        if (startProblem is not null) ShowNotice(new(startProblem, NoticeKind.Warning));
        if (startUri == BrowserEngine.NewTabUri) AddressBox.Focus(FocusState.Programmatic);
    }

    void OnNavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        loading = false;
        UpdateNavigation();
        UpdateTitle();
        if (!firstNavigation) return;
        firstNavigation = false;
        report.FirstNavigation(sender.Source);
    }

    void OnProcessFailed(CoreWebView2 sender, CoreWebView2ProcessFailedEventArgs e)
    {
        switch (e.ProcessFailedKind)
        {
            case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                ShowNotice(new("The web engine stopped.", NoticeKind.Error, "Restart Lilt", Restart));
                break;
            case CoreWebView2ProcessFailedKind.RenderProcessUnresponsive:
                ShowNotice(new("This page isn't responding.", NoticeKind.Warning, "Reload", () => core?.Reload()));
                break;
            case CoreWebView2ProcessFailedKind.RenderProcessExited:
            case CoreWebView2ProcessFailedKind.FrameRenderProcessExited:
                ShowNotice(new("This page crashed.", NoticeKind.Error, "Reload", () => core?.Reload()));
                break;
        }
    }

    void UpdateNavigation()
    {
        if (core is null) return;
        BackButton.IsEnabled = core.CanGoBack;
        ForwardButton.IsEnabled = core.CanGoForward;
        ReloadIcon.Glyph = loading ? "" : "";
        ToolTipService.SetToolTip(ReloadButton, loading ? "Stop" : "Reload (F5)");
        AutomationProperties.SetName(ReloadButton, loading ? "Stop" : "Reload");
        var uri = Uri.TryCreate(core.Source, UriKind.Absolute, out var u) ? u : null;
        if (AddressBox.FocusState == FocusState.Unfocused) AddressBox.Text = Display(uri);
        InsecureChip.Visibility = uri is not null && UrlPolicy.IsInsecure(uri) ? Visibility.Visible : Visibility.Collapsed;
    }

    void UpdateTitle()
    {
        var title = core?.DocumentTitle;
        AppWindow.Title = string.IsNullOrWhiteSpace(title) || core?.Source?.StartsWith(BrowserEngine.NewTabUri.AbsoluteUri, StringComparison.Ordinal) == true
            ? appName
            : $"{title} – {appName}";
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
                core?.Navigate(result.Uri!.AbsoluteUri);
                view.Focus(FocusState.Programmatic);
                break;
        }
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
            AddressBox.Text = Display(Uri.TryCreate(core?.Source, UriKind.Absolute, out var u) ? u : null);
            view.Focus(FocusState.Programmatic);
        }
    }

    void AddressBox_GotFocus(object sender, RoutedEventArgs e) => DispatcherQueue.TryEnqueue(AddressBox.SelectAll);

    void Back_Click(object sender, RoutedEventArgs e) => core?.GoBack();
    void Forward_Click(object sender, RoutedEventArgs e) => core?.GoForward();

    void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (loading) core?.Stop();
        else core?.Reload();
    }

    void FocusAddress_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        e.Handled = true;
        if (fullScreen) return;
        AddressBox.Focus(FocusState.Keyboard);
        AddressBox.SelectAll();
    }

    void Back_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        e.Handled = true;
        core?.GoBack();
    }

    void Forward_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        e.Handled = true;
        core?.GoForward();
    }

    void Reload_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        e.Handled = true;
        core?.Reload();
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
        var start = new ProcessStartInfo(Environment.ProcessPath!);
        foreach (var arg in Environment.GetCommandLineArgs().Skip(1)) start.ArgumentList.Add(arg);
        Process.Start(start);
        Close();
    }

    void OnClosed(object sender, WindowEventArgs e)
    {
        engine.CloseWindows();
        themes.Dispose();
        engine.UserContent.Dispose();
        engine.Detach(core);
        view.Close();
    }

    // ---- Theme ----

    void ApplyTheme(Theme t, Theme? previous)
    {
        theme = t;
        metrics = ShellMetrics.From(t);
        var highContrast = new AccessibilitySettings().HighContrast;
        var colors = highContrast ? ThemeResources.HighContrast() : t.Colors;
        ThemeResources.Apply(Application.Current.Resources, colors);
        Root.RequestedTheme = highContrast ? ElementTheme.Default : t.Base == ThemeBase.Light ? ElementTheme.Light : ElementTheme.Dark;
        ApplyBackdrop(highContrast ? Backdrop.None : t.Backdrop);
        ApplyCaptionColors(colors);
        view.DefaultBackgroundColor = ThemeResources.ToColor(colors.Page.Opaque);
        engine.SetPageColorScheme(t.Pages.ColorScheme, t.Base);

        var m = metrics;
        var radius = new CornerRadius(t.Shape.ControlRadius);
        Toolbar.Height = m.ToolbarHeight;
        Toolbar.Padding = new Thickness(m.Unit, 0, 0, 0);
        Toolbar.BorderThickness = new Thickness(0, 0, 0, t.Shape.BorderWidth);
        NavPanel.Spacing = Math.Max(2, m.Unit / 2);
        AddressHost.Margin = new Thickness(m.Unit, 0, m.Unit, 0);
        foreach (var b in ChromeButtons()) StyleButton(b);

        var family = new FontFamily(t.Typography.Family);
        var weight = new FontWeight((ushort)t.Typography.Weight);
        AddressBox.MinHeight = 0;
        AddressBox.Height = m.ControlHeight;
        AddressBox.CornerRadius = radius;
        var pad = Math.Max(0, Math.Floor((m.ControlHeight - t.Typography.Size * 1.33) / 2) - 1);
        AddressBox.Padding = new Thickness(m.Unit * 1.5, pad, m.Unit, pad);
        AddressBox.FontFamily = family;
        AddressBox.FontSize = t.Typography.Size;
        AddressBox.FontWeight = weight;
        foreach (var tb in new[] { ThemeName, ThemeStatus, MemoryText, NoDownloads, InsecureText })
        {
            tb.FontFamily = family;
            tb.FontSize = t.Typography.Size;
            tb.FontWeight = weight;
        }
        foreach (var tb in new[] { LayoutHeader, ThemeHeader, DownloadsHeader, MemoryHeader })
        {
            tb.FontFamily = family;
            tb.FontSize = Math.Max(9, t.Typography.Size - 2);
        }
        AppWindow.TitleBar.PreferredHeightOption = m.TallCaption ? TitleBarHeightOption.Tall : TitleBarHeightOption.Standard;

        // The file is the source of truth for layout, but only fields that changed in it override what you set in the sidebar.
        if (previous is null || previous.Sidebar.Edge != t.Sidebar.Edge) sidebar.Edge = t.Sidebar.Edge;
        if (previous is not null && previous.Sidebar.AutoHide != t.Sidebar.AutoHide) sidebar.SetAutoHide(t.Sidebar.AutoHide);
        LayoutSidebar();

        ThemeName.Text = $"{t.Name} · {(t.Base == ThemeBase.Light ? "light" : "dark")}";
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
        BackButton, ForwardButton, ReloadButton, SidebarButton,
        EdgeLeft, EdgeRight, EdgeTop, EdgeBottom, PinToggle,
        ReloadThemeButton, EditThemeButton, DataFolderButton, UserContentButton, MeasureButton,
    ];

    void ApplyBackdrop(Backdrop requested)
    {
        var effective = requested switch
        {
            Backdrop.Mica or Backdrop.MicaAlt when MicaController.IsSupported() => requested,
            Backdrop.Acrylic when DesktopAcrylicController.IsSupported() => requested,
            _ => Backdrop.None,
        };
        if (effective != activeBackdrop)
        {
            SystemBackdrop = effective switch
            {
                Backdrop.Mica => new MicaBackdrop { Kind = MicaKind.Base },
                Backdrop.MicaAlt => new MicaBackdrop { Kind = MicaKind.BaseAlt },
                Backdrop.Acrylic => new DesktopAcrylicBackdrop(),
                _ => null,
            };
            activeBackdrop = effective;
        }
        Root.Background = effective == Backdrop.None ? ThemeResources.Get("LiltBackgroundBrush") : new SolidColorBrush(Colors.Transparent);
        backdropNote = effective == requested ? "" : $" · {requested} unavailable, using a solid background";
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

    void OnThemeReloaded(ThemeLoadResult result)
    {
        if (result.Failed)
        {
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

        var u = metrics.Unit;
        SidebarStack.Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal;
        SidebarStack.Spacing = vertical ? u : u * 2;
        SidebarStack.Padding = vertical ? new Thickness(u * 2) : new Thickness(u * 2, 0, u * 2, 0);
        SidebarStack.VerticalAlignment = vertical ? VerticalAlignment.Top : VerticalAlignment.Center;
        foreach (var header in new[] { LayoutHeader, ThemeHeader, DownloadsHeader, MemoryHeader })
            header.Visibility = vertical ? Visibility.Visible : Visibility.Collapsed;
        DownloadList.Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal;
        foreach (var text in new[] { ThemeStatus, MemoryText })
        {
            text.TextWrapping = vertical ? TextWrapping.Wrap : TextWrapping.NoWrap;
            text.MaxWidth = vertical ? double.PositiveInfinity : 340;
        }
        SidebarScroll.HorizontalScrollMode = vertical ? ScrollMode.Disabled : ScrollMode.Auto;
        SidebarScroll.HorizontalScrollBarVisibility = vertical ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        SidebarScroll.VerticalScrollMode = vertical ? ScrollMode.Auto : ScrollMode.Disabled;
        SidebarScroll.VerticalScrollBarVisibility = vertical ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;

        foreach (var (button, e) in EdgeButtons()) button.IsChecked = e == edge;
        PinToggle.IsChecked = !sidebar.AutoHide;
        ShowSidebar(sidebar.Open, animate: false);
    }

    (ToggleButton Button, DockEdge Edge)[] EdgeButtons() =>
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
        if (IsFocusWithin(Sidebar)) view.Focus(FocusState.Programmatic);
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
        view.Focus(FocusState.Programmatic);
    }

    void SidebarButton_Click(object sender, RoutedEventArgs e) => ToggleSidebar(keyboard: false);

    void ToggleSidebar_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        e.Handled = true;
        ToggleSidebar(keyboard: true);
    }

    void ToggleSidebar(bool keyboard)
    {
        if (fullScreen) return;
        sidebar.Toggle();
        pointerInBand = false;
        SyncSidebar();
        if (!sidebar.Open) return;
        if (keyboard) EdgeButtons().First(b => b.Edge == sidebar.Edge).Button.Focus(FocusState.Keyboard);
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
        foreach (var element in new FrameworkElement[] { NavPanel, AddressHost, SidebarButton })
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

    // ---- Downloads ----

    void OnDownloadStarted(CoreWebView2DownloadOperation op)
    {
        var body = (Style)Application.Current.Resources["BodyText"];
        var name = new TextBlock { Style = body, Text = Path.GetFileName(op.ResultFilePath), FontSize = theme.Typography.Size };
        var status = new TextBlock { Style = body, Foreground = ThemeResources.Get("LiltTextMutedBrush"), FontSize = Math.Max(9, theme.Typography.Size - 1) };
        var icon = new FontIcon { Glyph = "" };
        var action = new Button { Style = (Style)Application.Current.Resources["ChromeButton"], Content = icon, VerticalAlignment = VerticalAlignment.Center };
        StyleButton(action);
        var row = new Grid { ColumnSpacing = 6, Width = sidebar.Edge.IsVertical() ? double.NaN : 240 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(name);
        text.Children.Add(status);
        row.Children.Add(text);
        Grid.SetColumn(action, 1);
        row.Children.Add(action);
        DownloadList.Children.Insert(0, row);
        NoDownloads.Visibility = Visibility.Collapsed;

        long lastUpdate = 0;
        void Update()
        {
            status.Text = DownloadStatus(op);
            var (glyph, tip) = op.State switch
            {
                CoreWebView2DownloadState.InProgress => ("", "Cancel download"),
                CoreWebView2DownloadState.Completed => ("", "Show in folder"),
                _ when op.CanResume => ("", "Resume download"),
                _ => ("", "Remove from list"),
            };
            icon.Glyph = glyph;
            ToolTipService.SetToolTip(action, tip);
            AutomationProperties.SetName(action, tip);
        }

        action.Click += (_, _) =>
        {
            switch (op.State)
            {
                case CoreWebView2DownloadState.InProgress:
                    op.Cancel();
                    break;
                case CoreWebView2DownloadState.Completed:
                    ShowInFolder(op.ResultFilePath);
                    break;
                case CoreWebView2DownloadState.Interrupted when op.CanResume:
                    op.Resume();
                    break;
                default:
                    DownloadList.Children.Remove(row);
                    if (DownloadList.Children.Count == 0) NoDownloads.Visibility = Visibility.Visible;
                    break;
            }
        };
        op.BytesReceivedChanged += (_, _) =>
        {
            var now = Environment.TickCount64;
            if (now - lastUpdate < 250) return;
            lastUpdate = now;
            Update();
        };
        op.StateChanged += (_, _) =>
        {
            Update();
            if (op.State == CoreWebView2DownloadState.Completed)
                ShowNotice(new($"Downloaded {name.Text}", NoticeKind.Success, "Show in folder", () => ShowInFolder(op.ResultFilePath)));
            else if (op.State == CoreWebView2DownloadState.Interrupted)
                ShowNotice(new($"{name.Text} didn't finish downloading: {Humanize(op.InterruptReason.ToString())}.", NoticeKind.Warning));
        };
        Update();
        ShowNotice(new($"Downloading {name.Text} to {Path.GetDirectoryName(op.ResultFilePath)}", NoticeKind.Info));
    }

    static string DownloadStatus(CoreWebView2DownloadOperation op)
    {
        double received = op.BytesReceived;
        double total = op.TotalBytesToReceive is { } t ? t : 0;
        return op.State switch
        {
            CoreWebView2DownloadState.Completed => $"Done · {Size(received)}",
            CoreWebView2DownloadState.Interrupted => $"Stopped · {Humanize(op.InterruptReason.ToString())}",
            _ => total > 0 ? $"{received / total:P0} · {Size(received)} of {Size(total)}" : Size(received),
        };
    }

    static string Size(double bytes) => bytes switch
    {
        >= 1 << 30 => $"{bytes / (1 << 30):N1} GB",
        >= 1 << 20 => $"{bytes / (1 << 20):N1} MB",
        >= 1 << 10 => $"{bytes / (1 << 10):N0} KB",
        _ => $"{bytes:N0} B",
    };

    static string Humanize(string pascal) => Regex.Replace(pascal, "(?<=[a-z])(?=[A-Z])", " ").ToLowerInvariant();

    static void ShowInFolder(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\""));
        }
        catch (Win32Exception e)
        {
            LiltPaths.LogError(e);
        }
    }

    // ---- Diagnostics ----

    void Measure_Click(object sender, RoutedEventArgs e) =>
        MemoryText.Text = $"{MemorySnapshot.Format(MemorySnapshot.Take(engine.Environment))} · {DateTime.Now:t}";
}
