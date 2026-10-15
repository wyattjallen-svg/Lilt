using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace Lilt.App.Shell;

static class WindowSizing
{
    [DllImport("user32.dll")]
    static extern uint GetDpiForWindow(nint hwnd);

    public static double Scale(Window window)
    {
        var dpi = GetDpiForWindow(Win32Interop.GetWindowFromWindowId(window.AppWindow.Id));
        return dpi == 0 ? 1 : dpi / 96.0;
    }

    // Sizes in logical pixels; centred on the window's display unless a position is given.
    public static void Place(Window window, double width, double height, double? left = null, double? top = null)
    {
        var scale = Scale(window);
        var work = DisplayArea.GetFromWindowId(window.AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        var w = (int)Math.Min(width * scale, work.Width * 0.95);
        var h = (int)Math.Min(height * scale, work.Height * 0.95);
        var x = left is { } l ? (int)(l * scale) : work.X + (work.Width - w) / 2;
        var y = top is { } t ? (int)(t * scale) : work.Y + (work.Height - h) / 2;
        window.AppWindow.MoveAndResize(new RectInt32(x, y, w, h));
    }
}
