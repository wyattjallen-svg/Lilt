using Lilt.App.Browser;
using Lilt.Core;
using Lilt.Core.Navigation;
using Lilt.Core.Theming;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Windows.Media.Control;

namespace Lilt.App;

// Now playing: each tab's page helper reports its media (title, artist, artwork, position) and Lilt shows the tab that
// last started playing in a floating player. Picture in picture uses Windows' floating video window when WebView2 offers
// it, otherwise the tab itself pops out into a small frame over the page you're on ("floating").
public sealed partial class MainWindow
{
    BrowserTab? mediaTab, floating;
    bool hasMedia;
    // The player's and visualizer's show/hide buttons override media.player.show / media.visualizer.show until the next
    // thing plays (player) or for this session (visualizer).
    bool? playerToggle, visualizerToggle;
    string? artShown;
    DispatcherQueueTimer mediaClock = null!;
    static GlobalSystemMediaTransportControlsSessionManager? smtc;

    void InitMedia()
    {
        engine.MediaChanged += OnMediaChanged;
        engine.VisualizerData += OnVisualizerData;
        mediaClock = DispatcherQueue.CreateTimer();
        mediaClock.Interval = TimeSpan.FromSeconds(1);
        mediaClock.Tick += (_, _) => UpdateProgress();
        MediaWidget.SizeChanged += (_, _) => PlaceWidgets();
    }

    void OnMediaChanged(CoreWebView2 core, MediaInfo? info)
    {
        if (tabs.FirstOrDefault(t => t.Core == core) is not { } tab) return;
        var was = tab.Media;
        tab.Media = info;
        tab.MediaAt = Environment.TickCount64;
        if (info is { Playing: true } && (mediaTab != tab || was?.Title != info.Title || was?.Playing != true))
        {
            if (mediaTab != tab || was?.Title != info.Title) playerToggle = playerToggle == true ? true : null;
            mediaTab = tab;
        }
        else if (mediaTab == tab && info is null) mediaTab = tabs.FirstOrDefault(t => t.Media is { Playing: true });
        if (floating == tab && info is null) DockFloating(activate: false);
        UpdateMedia();
    }

    bool OnScreen(BrowserTab tab) => tab == active || tab == floating || (active is not null && Partner(active) == tab);

    bool PlayerWanted()
    {
        if (playerToggle is { } forced) return forced;
        var info = mediaTab?.Media;
        return theme.Media.Player.Show switch
        {
            WidgetShow.Never => false,
            WidgetShow.Always => info is not null,
            // While the playing tab is out of sight: you can see (and control) the page itself otherwise.
            _ => info is not null && !OnScreen(mediaTab!),
        };
    }

    void UpdateMedia()
    {
        var info = mediaTab?.Media;
        hasMedia = info is not null;
        ArrangeToolbar(theme.Toolbar.Items);
        var show = PlayerWanted() && !pagesHidden;
        MediaWidget.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show)
        {
            mediaClock.Stop();
            return;
        }
        var tab = mediaTab;
        MediaTitle.Text = info is null ? "Nothing playing" : info.Title.Length > 0 ? info.Title : tab!.DisplayTitle;
        MediaArtist.Text = info is null || tab is null ? "" : info.Artist.Length > 0 ? info.Artist : UrlPolicy.DisplayHost(tab.Url);
        MediaPlayIcon.Glyph = info is { Playing: true } ? "" : "";
        MediaMuteIcon.Glyph = tab?.Core?.IsMuted == true ? "" : "";
        MediaControls.Opacity = info is null ? 0.4 : 1;
        MediaControls.IsHitTestVisible = info is not null;
        MediaPip.Visibility = info is { Video: true } && theme.Media.Pip.Show != WidgetShow.Never ? Visibility.Visible : Visibility.Collapsed;
        if (info?.Artwork != artShown || MediaArt.Source is null)
        {
            artShown = info?.Artwork;
            // Artwork comes from the site that's playing (its own image server); otherwise the tab's icon.
            MediaArt.Source = artShown is not null ? new BitmapImage(new Uri(artShown)) { DecodePixelWidth = 104 } : tab?.Icon;
        }
        UpdateProgress();
        if (info is { Playing: true, Duration: > 0 }) mediaClock.Start();
        else mediaClock.Stop();
    }

    void UpdateProgress()
    {
        if (mediaTab is not { Media: { } info } tab || info.Duration <= 0)
        {
            MediaProgress.Visibility = Visibility.Collapsed;
            MediaTime.Text = mediaTab?.Media is { Playing: true } ? "Live" : "";
            return;
        }
        var pos = Math.Min(info.Duration, info.Position + (info.Playing ? (Environment.TickCount64 - tab.MediaAt) / 1000.0 * info.Rate : 0));
        MediaProgress.Visibility = Visibility.Visible;
        MediaProgress.Value = pos / info.Duration;
        MediaTime.Text = $"{MediaInfo.Clock(pos)} / {MediaInfo.Clock(info.Duration)}";
    }

    void ToggleMediaWidget()
    {
        playerToggle = MediaWidget.Visibility != Visibility.Visible;
        UpdateMedia();
    }

    Task<string?> MediaCommandAsync(string js) =>
        mediaTab?.Core is { } c ? engine.EvaluateAsync(c, js, userGesture: true) : Task.FromResult<string?>(null);

    void MediaButton_Click(object sender, RoutedEventArgs e) => ToggleMediaWidget();
    void MediaPlay_Click(object sender, RoutedEventArgs e) => _ = MediaCommandAsync("__lilt.media('toggle')");
    void MediaNext_Click(object sender, RoutedEventArgs e) => _ = SkipAsync("next");
    void MediaPrev_Click(object sender, RoutedEventArgs e) => _ = SkipAsync("prev");
    void MediaVisualizer_Click(object sender, RoutedEventArgs e) => ToggleVisualizer();
    void MediaPip_Click(object sender, RoutedEventArgs e) => _ = PipAsync(mediaTab);

    void MediaClose_Click(object sender, RoutedEventArgs e)
    {
        playerToggle = false;
        UpdateMedia();
    }

    void MediaMute_Click(object sender, RoutedEventArgs e)
    {
        if (mediaTab?.Core is { } c) c.IsMuted = !c.IsMuted;
    }

    void MediaGoTo_Click(object sender, RoutedEventArgs e)
    {
        if (mediaTab is { } tab) Activate(tab);
    }

    void MediaProgress_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;
        if (mediaTab?.Media is not { Duration: > 0 } info || MediaProgress.ActualWidth <= 0) return;
        var to = e.GetCurrentPoint(MediaProgress).Position.X / MediaProgress.ActualWidth * info.Duration;
        _ = MediaCommandAsync($"__lilt.media('seekto', {to.ToString(System.Globalization.CultureInfo.InvariantCulture)})");
    }

    // The site's own next/previous buttons first (YouTube, Spotify, SoundCloud…), then Windows' media controls,
    // which reach the page's media session handlers.
    async Task SkipAsync(string which)
    {
        if (await MediaCommandAsync($"__lilt.media('{which}')") == "ok") return;
        try
        {
            smtc ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            foreach (var session in smtc.GetSessions())
            {
                if (!IsLiltSession(session.SourceAppUserModelId)) continue;
                var done = which == "next" ? await session.TrySkipNextAsync() : await session.TrySkipPreviousAsync();
                report.Key("media", $"{which} via Windows: {done}");
                return;
            }
        }
        catch (Exception e)
        {
            LiltPaths.LogError(e);
        }
    }

    static bool IsLiltSession(string id) =>
        id.Contains("lilt", StringComparison.OrdinalIgnoreCase) || id.Contains("msedgewebview2", StringComparison.OrdinalIgnoreCase);

    // ---- Picture in picture ----

    async Task PipAsync(BrowserTab? tab = null)
    {
        // Alt+P again puts a popped-out video back.
        if (tab is null && floating is not null)
        {
            DockFloating(activate: false);
            return;
        }
        tab ??= mediaTab is { Media.Video: true } m && !OnScreen(m) ? m : active;
        if (tab?.Core is not { } c) return;
        if (tab == floating)
        {
            DockFloating(activate: false);
            return;
        }
        var mode = theme.Media.Pip.Show;
        if (mode == WidgetShow.Never) return;
        if (mode == WidgetShow.Auto)
        {
            var result = await engine.EvaluateAsync(c, "__lilt.pip()", userGesture: true);
            report.Key("pip", result ?? "no helper");
            if (result is "open" or "closed") return;
        }
        await FloatAsync(tab);
    }

    // The tab leaves the page area and shows in the floating frame; its video fills its page (element full screen).
    async Task FloatAsync(BrowserTab tab)
    {
        if (tab.Core is not { } c || await engine.EvaluateAsync(c, "__lilt.hasVideo()") is not "true")
        {
            ShowNotice(new("There's no video on this page to pop out.", NoticeKind.Info));
            return;
        }
        if (floating is not null) DockFloating(activate: false);
        if (InSplit(tab)) split = null;
        floating = tab;
        if (tab == active)
        {
            var next = tabs.Where(t => t != tab).MaxBy(t => t.LastActive);
            if (next is null) AddTab(BrowserEngine.NewTabUri.AbsoluteUri);
            else Activate(next);
        }
        PipTitle.Text = tab.DisplayTitle;
        LayoutPages();
        var result = await engine.EvaluateAsync(c, "__lilt.float(true)", userGesture: true);
        report.Key("pip", "float " + result);
        if (result != "on" && floating == tab) DockFloating(activate: false);
    }

    void DockFloating(bool activate)
    {
        if (floating is not { } tab) return;
        floating = null;
        if (tab.Core is { } c) _ = engine.EvaluateAsync(c, "__lilt.float(false)");
        if (activate) Activate(tab);
        else LayoutPages();
    }

    void PipDock_Click(object sender, RoutedEventArgs e) => DockFloating(activate: true);
    void PipBar_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => DockFloating(activate: true);

    void PipClose_Click(object sender, RoutedEventArgs e)
    {
        if (floating?.Core is { } c) _ = engine.EvaluateAsync(c, "__lilt.media('pause')");
        DockFloating(activate: false);
    }

    // ---- Probe (--run probe): what this PC's engine supports, for CI and bug reports ----

    async Task ProbeAsync()
    {
        report.Key("probe", "page " + (core is { } c ? await engine.EvaluateAsync(c, "__lilt.probe()") : "no tab"));
        try
        {
            smtc ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            foreach (var session in smtc.GetSessions())
            {
                var props = await session.TryGetMediaPropertiesAsync();
                report.Key("probe", $"windows media session {session.SourceAppUserModelId}: '{props.Title}' {session.GetPlaybackInfo().PlaybackStatus}");
            }
            report.Key("probe", $"windows media sessions: {smtc.GetSessions().Count}");
        }
        catch (Exception e)
        {
            report.Key("probe", "windows media sessions failed: " + e.Message);
        }
        try
        {
            var packets = 0;
            using (Shell.LoopbackCapture.Start(Environment.ProcessId, _ => Interlocked.Increment(ref packets))) await Task.Delay(1500);
            report.Key("probe", $"loopback ok, {packets} packets");
        }
        catch (Exception e)
        {
            report.Key("probe", "loopback failed: " + e.Message);
        }
    }
}
