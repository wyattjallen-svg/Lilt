using Lilt.Core.Theming;

namespace Lilt.Core;

// Full: you're using Lilt. Reduced: you've been in another app for a bit, so Lilt drops to efficiency mode
// (low priority, EcoQoS, low memory target). Paused: Lilt can't be seen at all (minimized, or a full-screen
// app covers its screen), so pages are hidden and suspended too. Anything playing sound keeps Lilt at Full.
public enum PowerLevel { Full, Reduced, Paused }

public static class PowerPolicy
{
    public const double AwayDelaySeconds = 10;

    public static PowerLevel Decide(PowerSpec spec, bool focused, bool minimized, double awaySeconds, bool audible, bool covered, bool energySaver)
    {
        if (!spec.Efficiency || focused || audible) return PowerLevel.Full;
        if (awaySeconds < (energySaver ? 0 : AwayDelaySeconds)) return PowerLevel.Full;
        return minimized || (covered && spec.HideWhenCovered) ? PowerLevel.Paused : PowerLevel.Reduced;
    }

    // Background tabs are suspended after SuspendMinutes without being shown; Battery saver shortens that to one minute.
    public static bool ShouldSuspend(PowerSpec spec, double idleMinutes, bool energySaver) =>
        spec.SuspendMinutes > 0 && idleMinutes >= (energySaver ? Math.Min(1, spec.SuspendMinutes) : spec.SuspendMinutes);
}
