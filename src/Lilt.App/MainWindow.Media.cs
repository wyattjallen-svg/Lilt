using System.Runtime.InteropServices;
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
// last started playing in a floating player. A page whose media element isn't in the document (Spotify) reports only its
// media session; the play state, position and controls then come from Windows' media controls, which WebView2 feeds
// from that same session. Picture in picture uses Windows' floating video window when WebView2 offers
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
        mediaClock.Tick += (_, _) =>
        {
            UpdateProgress();
            // A session-only page sends nothing when its track changes: ask it, and Windows, every few seconds while it plays.
            if (mediaTab is { Media.Session: true } tab && ++sessionTicks % 3 == 0)
            {
                if (tab.Core is { } c) _ = engine.EvaluateAsync(c, "__lilt.report()");
                _ = SyncSessionAsync();
            }
        };
        MediaWidget.SizeChanged += (_, _) => PlaceWidgets();
    }

    void OnMediaChanged(CoreWebView2 core, MediaInfo? info)
    {
        if (AllTabs.FirstOrDefault(t => t.Core == core) is not { } tab) return;
        var was = tab.Media;
        if (info is { Session: true })
        {
            // The page only knows the session; keep what Windows said about the same track until it's asked again.
            var same = was is { Session: true } && was.Title == info.Title;
            if (same)
            {
                var elapsed = was!.Playing ? (Environment.TickCount64 - tab.MediaAt) / 1000.0 * was.Rate : 0;
                info = info with { Position = was.Duration > 0 ? Math.Min(was.Position + elapsed, was.Duration) : 0, Duration = was.Duration, Rate = was.Rate };
            }
            if (!info.KnownState) info = info with { Playing = same ? was!.Playing || tab.IsAudible : tab.IsAudible };
        }
        tab.Media = info;
        tab.MediaAt = Environment.TickCount64;
        if (info is { Playing: true } && (mediaTab != tab || was?.Title != info.Title || was?.Playing != true))
        {
            if (mediaTab != tab || was?.Title != info.Title) playerToggle = playerToggle == true ? true : null;
            mediaTab = tab;
        }
        else if (mediaTab == tab && info is null) mediaTab = AllTabs.FirstOrDefault(t => t.Media is { Playing: true });
        if (floating == tab && info is null) DockFloating(activate: false);
        UpdateMedia();
        if (tab == mediaTab && info is { Session: true }) _ = SyncSessionAsync();
    }

    int sessionTicks;
    bool syncingSession;

    // Sound starting or stopping is often the only sign from a session-only page (its element's events never reach the
    // document), so the page is asked again and the play state follows the sound until Windows says more.
    void OnAudibleChanged(BrowserTab tab)
    {
        if (tab.Core is { } c) _ = engine.EvaluateAsync(c, "__lilt.report()");
        if (tab.Media is { Session: true, KnownState: false } m)
        {
            tab.Media = m with { Playing = tab.IsAudible };
            tab.MediaAt = Environment.TickCount64;
            if (tab.IsAudible) mediaTab = tab;
            UpdateMedia();
        }
        if (tab == mediaTab && tab.Media is { Session: true }) _ = SyncSessionAsync();
    }

    // Windows' media controls for what plays in Lilt. WebView2's sessions all come from msedgewebview2.exe (other apps'
    // included), so the title picks the right one; with no title to go by, a lone WebView2 session is taken.
    async Task<GlobalSystemMediaTransportControlsSession?> LiltSessionAsync(string? title)
    {
        smtc ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        GlobalSystemMediaTransportControlsSession? only = null;
        var count = 0;
        foreach (var session in smtc.GetSessions())
        {
            if (!IsLiltSession(session.SourceAppUserModelId)) continue;
            if (title is { Length: > 0 } && (await session.TryGetMediaPropertiesAsync()).Title == title) return session;
            only = session;
            count++;
        }
        return count == 1 ? only : null;
    }

    async Task SyncSessionAsync()
    {
        if (syncingSession || mediaTab is not { Media: { Session: true } info } tab) return;
        syncingSession = true;
        try
        {
            if (await LiltSessionAsync(info.Title) is not { } s) return;
            var p = s.GetPlaybackInfo();
            var t = s.GetTimelineProperties();
            var playing = p.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            var rate = p.PlaybackRate is double r && r > 0 ? r : 1;
            var duration = Math.Max(0, (t.EndTime - t.StartTime).TotalSeconds);
            var position = Math.Max(0, (t.Position - t.StartTime).TotalSeconds);
            // The position is as of Windows' last update; carry it to now while playing.
            if (playing && t.LastUpdatedTime > DateTimeOffset.Now.AddDays(-1)) position += Math.Max(0, (DateTimeOffset.Now - t.LastUpdatedTime).TotalSeconds) * rate;
            if (mediaTab != tab || tab.Media is not { Session: true } now || now.Title != info.Title) return;
            tab.Media = now with { Playing = playing, Position = duration > 0 ? Math.Min(position, duration) : 0, Duration = duration, Rate = rate };
            tab.MediaAt = Environment.TickCount64;
            UpdateMedia();
        }
        catch (Exception e) when (e is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            LiltPaths.Log.Write(Lilt.Core.Diagnostics.LogLevel.Warning, "media", "Windows' media controls didn't answer", e.Message);
        }
        finally
        {
            syncingSession = false;
        }
    }

    // Play, pause, seek or skip through Windows' media controls, then read the session again.
    async Task<bool> WithSessionAsync(string what, Func<GlobalSystemMediaTransportControlsSession, Task<bool>> act)
    {
        try
        {
            if (await LiltSessionAsync(mediaTab?.Media?.Title) is not { } s) return false;
            var done = await act(s);
            report.Key("media", $"{what} via Windows: {done}");
            await Task.Delay(250);
            await SyncSessionAsync();
            return done;
        }
        catch (Exception e) when (e is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            LiltPaths.Log.Write(Lilt.Core.Diagnostics.LogLevel.Warning, "media", $"Couldn't {what} through Windows' media controls", e.Message);
            return false;
        }
    }

    // The page's own element when it has one; its media session through Windows otherwise.
    async Task PlayPauseAsync()
    {
        if (mediaTab?.Media is not { Session: true } && await MediaCommandAsync("__lilt.media('toggle')") == "ok") return;
        await WithSessionAsync("play or pause", async s => await s.TryTogglePlayPauseAsync());
    }

    bool OnScreen(BrowserTab tab) => tab == active || tab == floating || (active is not null && Partner(active) == tab) || PanelShows(tab);

    bool PlayerWanted()
    {
        if (playerToggle is { } forced) return forced;
        var info = mediaTab?.Media;
        return theme.Media.Player.Show switch
        {
            WidgetShow.Never => false,
            WidgetShow.Always => info is not null,
            // While the playing tab is out of sight: you can see (and control) the page itself otherwise. Docked in the
            // panel, it's part of the layout you chose, so it shows whenever something plays.
            _ => info is not null && (!OnScreen(mediaTab!) || Docked("player")),
        };
    }

    void UpdateMedia()
    {
        var info = mediaTab?.Media;
        hasMedia = info is not null;
        ArrangeToolbar(theme.Toolbar.Items);
        var show = PlayerWanted() && !pagesHidden;
        var shown = MediaWidget.Visibility == Visibility.Visible;
        MediaWidget.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (shown != show) SizePanelRows();
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
            // Decoded big enough for the panel's wide artwork.
            MediaArt.Source = artShown is not null ? new BitmapImage(new Uri(artShown)) { DecodePixelWidth = 480 } : tab?.Icon;
        }
        UpdateProgress();
        if (info is { Playing: true } && (info.Duration > 0 || info.Session)) mediaClock.Start();
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
        var pos = info.Position + (info.Playing ? (Environment.TickCount64 - tab.MediaAt) / 1000.0 * info.Rate : 0);
        // A position past the reported length means that length belonged to another clip: show the time without a bar.
        if (pos > info.Duration + 1)
        {
            MediaProgress.Visibility = Visibility.Collapsed;
            MediaTime.Text = MediaInfo.Clock(pos);
            return;
        }
        pos = Math.Min(pos, info.Duration);
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
    void MediaPlay_Click(object sender, RoutedEventArgs e) => _ = PlayPauseAsync();
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
        if (info.Session) _ = WithSessionAsync("seek", async s => await s.TryChangePlaybackPositionAsync((s.GetTimelineProperties().StartTime + TimeSpan.FromSeconds(to)).Ticks));
        else _ = MediaCommandAsync($"__lilt.media('seekto', {to.ToString(System.Globalization.CultureInfo.InvariantCulture)})");
    }

    // The site's own next/previous buttons first (YouTube, Spotify, SoundCloud…), then Windows' media controls,
    // which reach the page's media session handlers.
    async Task SkipAsync(string which)
    {
        if (await MediaCommandAsync($"__lilt.media('{which}')") == "ok") return;
        await WithSessionAsync(which, async s => which == "next" ? await s.TrySkipNextAsync() : await s.TrySkipPreviousAsync());
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
