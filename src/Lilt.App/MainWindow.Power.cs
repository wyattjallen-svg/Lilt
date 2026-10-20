using Lilt.App.Shell;
using Lilt.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.Web.WebView2.Core;
using Windows.System.Power;

namespace Lilt.App;

// Efficiency mode (rules in Lilt.Core.PowerPolicy). Windows already gives minimized and covered windows a low QoS;
// on top of that Lilt lowers its own priority and asks for EcoQoS, sets every page's memory target to Low, hides its
// pages when they can't be seen (behind another window: Hidden; minimized or behind a full-screen app: Paused, which
// also suspends them) and hands its own unused memory back to Windows. Everything returns to normal the moment Lilt is
// in front again. No polling while you use Lilt: the only timer runs while you're in another app.
public sealed partial class MainWindow
{
    DispatcherQueueTimer? powerTimer;
    long awaySince = -1;
    bool energySaver, closing, windowHidden;
    PowerLevel powerLevel = PowerLevel.Full;
    IntPtr hwnd;

    void InitPower()
    {
        hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        powerTimer = DispatcherQueue.CreateTimer();
        powerTimer.Interval = TimeSpan.FromSeconds(10);
        powerTimer.Tick += (_, _) => UpdatePower();
        // Activation changes trigger a check; while Lilt isn't in front (including a window that never got focus) a timer keeps checking.
        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated) powerTimer.Start();
            else powerTimer.Stop();
            DispatcherQueue.TryEnqueue(() => UpdatePower());
        };
        powerTimer.Start();
        Native.OnForegroundChanged(() => UpdatePower());
        try
        {
            energySaver = PowerManager.EnergySaverStatus == EnergySaverStatus.On;
            PowerManager.EnergySaverStatusChanged += (_, _) => DispatcherQueue.TryEnqueue(() =>
            {
                energySaver = PowerManager.EnergySaverStatus == EnergySaverStatus.On;
                UpdatePower();
            });
        }
        catch (Exception e)
        {
            LiltPaths.LogError(e);
        }
    }

    void UpdatePower()
    {
        if (powerTimer is null || closing) return;
        var focused = Native.LiltInFront();
        if (focused) awaySince = -1;
        else if (awaySince < 0) awaySince = Environment.TickCount64;
        var minimized = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized };
        var away = awaySince < 0 ? 0 : (Environment.TickCount64 - awaySince) / 1000.0;
        var audible = tabs.Any(t => t.IsAudible);
        var covered = !focused && !minimized && Native.IsCovered(hwnd);
        var occluded = !focused && !minimized && !covered && Native.IsOccluded(hwnd);
        // Whatever the level, nothing Lilt draws for itself (the visualizer, the player's clock) runs while it can't be seen.
        var hidden = minimized || covered || occluded;
        if (hidden != windowHidden)
        {
            windowHidden = hidden;
            UpdateVisualizer();
        }
        var level = PowerPolicy.Decide(theme.Power, focused, minimized, away, audible, covered && theme.Power.HideWhenCovered, energySaver, occluded);
        if (level == powerLevel)
        {
            // Suspending can fail while a just-hidden view still counts as visible; each paused tick tries again.
            if (level == PowerLevel.Paused) SuspendIdleTabs(force: true);
            return;
        }
        powerLevel = level;
        report.Key("power", level.ToString().ToLowerInvariant());
        var efficient = level != PowerLevel.Full;

        var pids = new List<int> { Environment.ProcessId };
        if (engine.Environment is { } env)
            foreach (var p in env.GetProcessInfos())
                if (p.Kind is CoreWebView2ProcessKind.Browser or CoreWebView2ProcessKind.Gpu or CoreWebView2ProcessKind.Utility) pids.Add(p.ProcessId);
        var changed = pids.Count(pid => Native.SetEfficient(pid, efficient));

        foreach (var tab in tabs)
        {
            if (tab.Core is not { } c) continue;
            try
            {
                c.MemoryUsageTargetLevel = efficient || !OnScreen(tab) ? CoreWebView2MemoryUsageTargetLevel.Low : CoreWebView2MemoryUsageTargetLevel.Normal;
            }
            catch (Exception) { } // a view that's closing
        }

        // Hidden pages stop rendering and get Chromium's background throttling; paused ones are then suspended too.
        var hide = level is PowerLevel.Hidden or PowerLevel.Paused;
        if (hide != pagesHidden)
        {
            pagesHidden = hide;
            LayoutPages();
            UpdateVisualizer();
        }
        if (hide)
        {
            Native.TrimMemory();
            if (level == PowerLevel.Paused)
            {
                SuspendIdleTabs(force: true);
                // The view reports itself hidden a moment later; suspend again then.
                _ = Task.Delay(2000).ContinueWith(_ => DispatcherQueue.TryEnqueue(() =>
                {
                    if (powerLevel == PowerLevel.Paused) SuspendIdleTabs(force: true);
                }));
            }
        }
        powerTimer.Interval = TimeSpan.FromSeconds(level == PowerLevel.Paused ? 30 : 10);
        PowerStatus.Text = level switch
        {
            PowerLevel.Full => "Running normally.",
            PowerLevel.Reduced => $"Efficiency mode on {changed} of {pids.Count} processes.",
            PowerLevel.Hidden => $"Behind another window: pages hidden; efficiency mode on {changed} of {pids.Count} processes.",
            _ => $"Paused: pages hidden and suspended; efficiency mode on {changed} of {pids.Count} processes.",
        };
    }
}
