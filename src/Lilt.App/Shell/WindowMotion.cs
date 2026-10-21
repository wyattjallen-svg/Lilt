using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace Lilt.App.Shell;

// motion.windowDrag: while you drag the window around, its content lifts a touch (a little smaller, a little
// see-through) and settles back when you let go. Resizing is left alone. Runs on the compositor, so it costs nothing.
sealed class WindowMotion
{
    delegate void WinEventProc(nint hook, uint e, nint window, int obj, int child, uint thread, uint time);
    [DllImport("user32.dll")] static extern nint SetWinEventHook(uint min, uint max, nint module, WinEventProc proc, uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out Point p);
    [DllImport("user32.dll")] static extern bool GetWindowRect(nint window, out Rect r);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(nint window);
    [StructLayout(LayoutKind.Sequential)] struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }

    const uint moveSizeStart = 0x0A, moveSizeEnd = 0x0B;
    readonly WinEventProc proc;
    readonly nint hwnd;
    readonly UIElement root;
    bool enabled;
    int duration = 160;

    public bool Lifted { get; private set; }

    public WindowMotion(nint hwnd, UIElement root)
    {
        this.hwnd = hwnd;
        this.root = root;
        proc = OnEvent;
        SetWinEventHook(moveSizeStart, moveSizeEnd, 0, proc, (uint)Environment.ProcessId, 0, 0);
    }

    public void Apply(bool on, int ms)
    {
        enabled = on;
        duration = Math.Clamp(ms, 80, 600);
        if (!on && Lifted) Lift(false);
    }

    void OnEvent(nint hook, uint e, nint window, int obj, int child, uint thread, uint time)
    {
        if (window != hwnd || obj != 0) return;
        if (e == moveSizeStart && enabled && Moving()) Lift(true);
        else if (e == moveSizeEnd && Lifted) Lift(false);
    }

    // A drag by the title bar starts with the pointer inside the window; a resize starts on its edge.
    bool Moving()
    {
        if (!GetCursorPos(out var p) || !GetWindowRect(hwnd, out var r)) return false;
        var edge = (int)(8 * Math.Max(96u, GetDpiForWindow(hwnd)) / 96);
        return p.X > r.Left + edge && p.X < r.Right - edge && p.Y > r.Top + edge && p.Y < r.Bottom - edge;
    }

    public void Lift(bool up)
    {
        Lifted = up;
        var visual = ElementCompositionPreview.GetElementVisual(root);
        var c = visual.Compositor;
        var size = root.RenderSize;
        visual.CenterPoint = new Vector3((float)size.Width / 2, (float)size.Height / 2, 0);
        var ease = c.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.8f), new Vector2(0.2f, 1f));
        var time = TimeSpan.FromMilliseconds(up ? duration : duration * 1.5);
        var scale = c.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(1, up ? new Vector3(0.985f, 0.985f, 1) : Vector3.One, ease);
        scale.Duration = time;
        var fade = c.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1, up ? 0.9f : 1f, ease);
        fade.Duration = time;
        visual.StartAnimation("Scale", scale);
        visual.StartAnimation("Opacity", fade);
    }
}
