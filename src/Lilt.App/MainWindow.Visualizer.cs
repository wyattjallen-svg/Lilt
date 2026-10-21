using Lilt.App.Shell;
using Lilt.Core;
using Lilt.Core.Theming;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.Web.WebView2.Core;

namespace Lilt.App;

// The visualizer: bars (or a wave) drawn from what Lilt is playing. Sound comes from Windows' process loopback of
// Lilt's own audio (every site, Windows 11) or from the playing tab's media element (media.source). It only runs while
// it's on screen and something is audible; nothing runs otherwise.
public sealed partial class MainWindow
{
    LoopbackCapture? loopback;
    Spectrum? spectrum;
    bool loopbackFailed;
    CoreWebView2? captureCore;
    float[] levels = [];
    long lastLevels, quietSince, loopbackSince, heardAt;
    DispatcherQueueTimer? vizTimer;
    string vizShape = "";
    int vizFrames;
    double vizPeak;

    bool IsMusicSite(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && theme.Media.MusicSites.Items.Any(h => u.Host == h || u.Host.EndsWith("." + h, StringComparison.Ordinal));

    bool VisualizerWanted()
    {
        if (visualizerToggle is { } forced) return forced;
        var playing = AllTabs.Where(t => t.IsAudible && !t.IsMuted);
        return theme.Media.Visualizer.Show switch
        {
            WidgetShow.Never => false,
            WidgetShow.Always => playing.Any(),
            _ => playing.Any(t => IsMusicSite(t.Url)),
        };
    }

    void ToggleVisualizer()
    {
        visualizerToggle = VisualizerWidget.Visibility != Visibility.Visible;
        UpdateVisualizer();
    }

    void UpdateVisualizer()
    {
        if (closing) return;
        var show = VisualizerWanted() && !pagesHidden && !windowHidden;
        var was = VisualizerWidget.Visibility == Visibility.Visible;
        VisualizerWidget.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show)
        {
            StopVisualizer();
            return;
        }
        if (!was) PlaceWidgets();
        BuildVisualizer();
        StartSound();
        quietSince = 0;
        if (vizTimer is null)
        {
            vizTimer = DispatcherQueue.CreateTimer();
            vizTimer.Interval = TimeSpan.FromMilliseconds(33);
            vizTimer.Tick += (_, _) => DrawVisualizer();
        }
        vizTimer.Interval = TimeSpan.FromMilliseconds(1000.0 / Math.Clamp(theme.Media.Fps, 10, 60));
        vizTimer.Start();
    }

    // Loopback first (media.source auto or system); the playing tab's media element if that's unavailable or chosen.
    async void StartSound()
    {
        var m = theme.Media;
        if (m.Source != VisualizerSource.Page && !loopbackFailed)
        {
            if (loopback is not null && spectrum?.Bars.Count == m.Bars) return;
            loopback?.Dispose();
            var s = new Spectrum(m.Bars, LoopbackCapture.SampleRate);
            spectrum = s;
            try
            {
                loopback = LoopbackCapture.Start(Environment.ProcessId, pcm =>
                {
                    lock (s) s.AddPcm16(pcm, 2);
                });
                loopbackSince = Environment.TickCount64;
                report.Key("visualizer", "system sound");
                return;
            }
            catch (Exception e)
            {
                loopbackFailed = true;
                loopback = null;
                spectrum = null;
                report.Key("visualizer", "loopback unavailable: " + e.Message);
            }
        }
        if (m.Source == VisualizerSource.System) return;
        var tab = mediaTab is { IsAudible: true } t ? t : AllTabs.FirstOrDefault(x => x.IsAudible) ?? mediaTab;
        if (tab?.Core is not { } c || (c == captureCore && levels.Length == m.Bars)) return;
        if (captureCore is { } old) _ = engine.EvaluateAsync(old, "__lilt.capture(false)");
        captureCore = c;
        levels = new float[m.Bars];
        var result = await engine.EvaluateAsync(c, $"__lilt.capture(true, {m.Bars})", userGesture: true);
        report.Key("visualizer", "page sound: " + result);
    }

    void OnVisualizerData(CoreWebView2 sender, byte[] data)
    {
        if (sender != captureCore || data.Length != levels.Length) return;
        for (var i = 0; i < data.Length; i++) levels[i] = Math.Max(data[i] / 255f, levels[i] * 0.82f);
        lastLevels = Environment.TickCount64;
    }

    void StopVisualizer()
    {
        vizTimer?.Stop();
        loopback?.Dispose();
        loopback = null;
        spectrum = null;
        if (captureCore is { } c) _ = engine.EvaluateAsync(c, "__lilt.capture(false)");
        captureCore = null;
    }

    // One shape per bar (or one line for the wave), rebuilt only when the style, bar count or color changes.
    void BuildVisualizer()
    {
        var m = theme.Media;
        var brush = ThemeResources.Get("LiltAccentBrush");
        var key = $"{m.Style}|{m.Bars}|{theme.Colors.Accent}";
        if (key == vizShape) return;
        vizShape = key;
        VisualizerCanvas.Children.Clear();
        if (m.Style == VisualizerStyle.Wave)
            VisualizerCanvas.Children.Add(new Polyline { Stroke = brush, StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round });
        else
            for (var i = 0; i < m.Bars; i++) VisualizerCanvas.Children.Add(new Rectangle { Fill = brush, RadiusX = 1.5, RadiusY = 1.5 });
    }

    void DrawVisualizer()
    {
        var m = theme.Media;
        var n = m.Bars;
        var now = Environment.TickCount64;
        Span<float> bars = stackalloc float[n];
        if (spectrum is { } s && s.Bars.Count == n)
        {
            lock (s) s.Update();
            for (var i = 0; i < n; i++) bars[i] = s.Bars[i];
        }
        else if (levels.Length == n)
        {
            // Page data stops when the page does; let the bars fall.
            if (now - lastLevels > 150)
                for (var i = 0; i < n; i++) levels[i] *= 0.82f;
            levels.AsSpan().CopyTo(bars);
        }
        double w = VisualizerCanvas.ActualWidth, h = VisualizerCanvas.ActualHeight, peak = 0;
        if (w <= 0 || h <= 0) return;
        foreach (var b in bars) peak = Math.Max(peak, b);
        if (peak > 0.02) heardAt = now;
        // Loopback that has heard nothing at all while a tab plays (no audio device, or sound Windows can't loop back):
        // listen to the playing tab instead. Once it has heard something it's working, and quiet passages are just quiet.
        if (loopback is not null && m.Source == VisualizerSource.Auto && now - loopbackSince > 2500 && heardAt == 0
            && AllTabs.Any(t => t.Media is { Playing: true } && !t.IsMuted))
        {
            loopbackFailed = true;
            loopback.Dispose();
            loopback = null;
            spectrum = null;
            report.Key("visualizer", "loopback silent, using the page");
            StartSound();
        }
        vizFrames++;
        vizPeak = Math.Max(vizPeak * 0.99, peak);

        if (VisualizerCanvas.Children.Count == 1 && VisualizerCanvas.Children[0] is Polyline line)
        {
            var points = new PointCollection();
            for (var i = 0; i < n; i++) points.Add(new Windows.Foundation.Point(i * w / Math.Max(1, n - 1), h - 1 - bars[i] * (h - 2)));
            line.Points = points;
        }
        else if (VisualizerCanvas.Children.Count == n && n > 1)
        {
            var slot = w / n;
            var gap = Math.Min(3, slot * 0.25);
            for (var i = 0; i < n; i++)
            {
                var r = (Rectangle)VisualizerCanvas.Children[i];
                var bh = Math.Max(2, bars[i] * h);
                r.Width = Math.Max(1, slot - gap);
                r.Height = bh;
                Canvas.SetLeft(r, i * slot + gap / 2);
                Canvas.SetTop(r, m.Style == VisualizerStyle.Mirror ? (h - bh) / 2 : h - bh);
            }
        }

        // Nothing audible and the bars have settled: stop drawing until sound starts again (UpdateVisualizer restarts it).
        if (peak > 0.02 || AllTabs.Any(t => t.IsAudible)) quietSince = 0;
        else if (quietSince == 0) quietSince = now;
        else if (now - quietSince > 2000) vizTimer?.Stop();
    }

    void Visualizer_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var next = (VisualizerStyle)(((int)Current.Media.Style + 1) % 3);
        Queue(Current with { Media = Current.Media with { Style = next } });
    }
}
