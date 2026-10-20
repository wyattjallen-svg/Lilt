using System.Runtime.InteropServices;

namespace Lilt.App.Shell;

// The few Win32 calls efficiency mode needs: process priority, EcoQoS, and "is a full-screen app covering my screen".
static class Native
{
    const uint ProcessSetInformation = 0x0200, ProcessQueryLimitedInformation = 0x1000;
    const uint NormalPriority = 0x20, BelowNormalPriority = 0x4000;
    const int ProcessPowerThrottling = 4;
    const uint ExecutionSpeed = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    struct PowerThrottlingState
    {
        public uint Version, ControlMask, StateMask;
    }

    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetPriorityClass(IntPtr process, uint priority);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PowerThrottlingState info, int size);
    [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    // Efficient: below-normal priority plus EcoQoS (Windows' "efficiency mode": efficient cores, lowest clocks).
    // Not efficient: normal priority and the QoS decision handed back to Windows.
    public static bool SetEfficient(int pid, bool efficient)
    {
        var self = pid == Environment.ProcessId;
        var handle = self ? GetCurrentProcess() : OpenProcess(ProcessSetInformation | ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return false;
        try
        {
            var state = new PowerThrottlingState { Version = 1, ControlMask = efficient ? ExecutionSpeed : 0, StateMask = efficient ? ExecutionSpeed : 0 };
            var qos = SetProcessInformation(handle, ProcessPowerThrottling, ref state, Marshal.SizeOf<PowerThrottlingState>());
            var priority = SetPriorityClass(handle, efficient ? BelowNormalPriority : NormalPriority);
            return qos && priority;
        }
        finally
        {
            if (!self) CloseHandle(handle);
        }
    }

    // True when the session is locked, or a full-screen app (a game, a presentation, a full-screen video player)
    // is in front on the same monitor as this window.
    public static bool IsCovered(IntPtr window)
    {
        if (SHQueryUserNotificationState(out var state) != 0) return false;
        const int notPresent = 1, busy = 2, direct3dFullScreen = 3, presentation = 4;
        if (state == notPresent) return true;
        if (state is not (busy or direct3dFullScreen or presentation)) return false;
        var front = GetForegroundWindow();
        const uint nearest = 2;
        return front != window && front != IntPtr.Zero && MonitorFromWindow(front, nearest) == MonitorFromWindow(window, nearest);
    }
}
