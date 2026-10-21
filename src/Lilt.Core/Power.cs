using Lilt.Core.Theming;

namespace Lilt.Core;

// Full: you're using Lilt. Reduced: you've been in another app for a bit, so Lilt drops to efficiency mode
// (low priority, EcoQoS, low memory target). Hidden: Lilt is fully behind another window, so its page is hidden too
// (it stops drawing and gets Chromium's background throttling, but keeps running). Paused: Lilt can't be seen at all
// (minimized, a full-screen app covers its screen, or the PC is locked), so pages are hidden and suspended.
// Anything playing sound keeps Lilt at Full.
public enum PowerLevel { Full, Reduced, Hidden, Paused }

public static class PowerPolicy
{
    public const double AwayDelaySeconds = 10;

    public static PowerLevel Decide(PowerSpec spec, bool focused, bool minimized, double awaySeconds, bool audible, bool covered, bool energySaver, bool occluded = false)
    {
        if (!spec.Efficiency || focused || audible) return PowerLevel.Full;
        if (awaySeconds < (energySaver ? 0 : AwayDelaySeconds)) return PowerLevel.Full;
        if (minimized || (covered && spec.HideWhenCovered)) return PowerLevel.Paused;
        return occluded && spec.HideWhenOccluded ? PowerLevel.Hidden : PowerLevel.Reduced;
    }

    // Background tabs are suspended after SuspendMinutes without being shown; Battery saver shortens that to one minute.
    public static bool ShouldSuspend(PowerSpec spec, double idleMinutes, bool energySaver) =>
        spec.SuspendMinutes > 0 && idleMinutes >= (energySaver ? Math.Min(1, spec.SuspendMinutes) : spec.SuspendMinutes);

    // Sleeping closes a background tab's page entirely: after SleepMinutes unseen, or after a few minutes when Windows is
    // short of memory (memoryLoad: percent of physical memory in use).
    public const int LowMemoryLoad = 85;

    public static bool ShouldSleep(PowerSpec spec, double idleMinutes, int memoryLoad) =>
        (spec.SleepMinutes > 0 && idleMinutes >= spec.SleepMinutes) || (spec.SleepWhenLowMemory && memoryLoad >= LowMemoryLoad && idleMinutes >= 5);

    // True when rectangle a (left, top, right, bottom) fully contains b, with a few pixels of slack for window shadows.
    public static bool Covers((int L, int T, int R, int B) a, (int L, int T, int R, int B) b, int slack = 8) =>
        a.L <= b.L + slack && a.T <= b.T + slack && a.R >= b.R - slack && a.B >= b.B - slack && b.R > b.L && b.B > b.T;
}
