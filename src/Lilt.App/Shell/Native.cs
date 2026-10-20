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
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr window, char[] name, int max);

    [StructLayout(LayoutKind.Sequential)]
    struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MonitorInfo
    {
        public int Size;
        public Rect Monitor, Work;
        public uint Flags;
    }

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
    // is in front on the same monitor as this window. Same signals Chromium uses: the shell's notification state, plus
    // a frameless foreground window that exactly covers its monitor (borderless games often only show up this way).
    public static bool IsCovered(IntPtr window)
    {
        const int notPresent = 1, busy = 2, direct3dFullScreen = 3, presentation = 4;
        var state = SHQueryUserNotificationState(out var s) == 0 ? s : 0;
        if (state == notPresent) return true;
        var front = GetForegroundWindow();
        const uint nearest = 2;
        if (front == IntPtr.Zero || front == window) return false;
        var monitor = MonitorFromWindow(front, nearest);
        if (monitor != MonitorFromWindow(window, nearest)) return false;
        if (state is busy or direct3dFullScreen or presentation) return true;

        var name = new char[64];
        var cls = new string(name, 0, GetClassName(front, name, name.Length));
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd") return false;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetWindowRect(front, out var r) || !GetMonitorInfo(monitor, ref info)) return false;
        var m = info.Monitor;
        var clipped = (Math.Max(r.Left, m.Left), Math.Max(r.Top, m.Top), Math.Min(r.Right, m.Right), Math.Min(r.Bottom, m.Bottom));
        if (clipped != (m.Left, m.Top, m.Right, m.Bottom)) return false;
        const long dialogFrame = 0x00400000, thickFrame = 0x00040000, windowEdge = 0x00000100, toolWindow = 0x00000080;
        var style = (long)GetWindowLongPtr(front, -16);
        var exStyle = (long)GetWindowLongPtr(front, -20);
        return (style & (dialogFrame | thickFrame)) == 0 && (exStyle & (windowEdge | toolWindow)) == 0;
    }
}
