using Lilt.Core.Layout;

namespace Lilt.Core.Theming;

public enum ThemeBase { Dark, Light }

public enum Backdrop { Mica, MicaAlt, Acrylic, AcrylicThin, None }

// Tint values are optional; when absent Windows picks them from the light/dark base.
public sealed record BackdropSpec(Backdrop Material, Rgba? Tint = null, double? TintOpacity = null, double? LuminosityOpacity = null);

public enum Density { Compact, Standard, Comfortable }

// What websites see as prefers-color-scheme. Theme follows the theme's base.
public enum PageColorScheme { Theme, Auto, Light, Dark }

public sealed record ThemeColors(
    Rgba Background,
    Rgba Surface,
    Rgba SurfaceRaised,
    Rgba Border,
    Rgba Text,
    Rgba TextMuted,
    Rgba Accent,
    Rgba OnAccent,
    Rgba Hover,
    Rgba Pressed,
    Rgba Page);

public sealed record Typography(string Family, double Size, int Weight);

public sealed record Shape(double Radius, double ControlRadius, double BorderWidth);

public sealed record ToolbarSpec(double Height);

public sealed record SidebarSpec(DockEdge Edge, double Width, double Height, bool AutoHide, double RevealEdge, int HideDelay);

public sealed record MotionSpec(bool Animations, int Duration);

public sealed record PagesSpec(PageColorScheme ColorScheme);

// The home tab. Glass makes its background transparent so the window material shows through.
public sealed record HomeSpec(bool Clock, bool Search, bool Tiles, bool Glass);

public sealed record Theme(
    int SchemaVersion,
    string Name,
    ThemeBase Base,
    BackdropSpec Backdrop,
    ThemeColors Colors,
    Typography Typography,
    Shape Shape,
    Density Density,
    ToolbarSpec Toolbar,
    SidebarSpec Sidebar,
    MotionSpec Motion,
    PagesSpec Pages,
    HomeSpec Home)
{
    public static Theme Default { get; } = new(
        1,
        "Dusk",
        ThemeBase.Dark,
        new BackdropSpec(Theming.Backdrop.Mica),
        new ThemeColors(
            Background: Rgba.Parse("#141318"),
            Surface: Rgba.Parse("#1D1B23B3"),
            SurfaceRaised: Rgba.Parse("#2A2733CC"),
            Border: Rgba.Parse("#FFFFFF14"),
            Text: Rgba.Parse("#ECE9F1"),
            TextMuted: Rgba.Parse("#A19CAB"),
            Accent: Rgba.Parse("#C4A7FF"),
            OnAccent: Rgba.Parse("#1A1325"),
            Hover: Rgba.Parse("#FFFFFF0F"),
            Pressed: Rgba.Parse("#FFFFFF1A"),
            Page: Rgba.Parse("#141318")),
        new Typography("Segoe UI Variable Text", 13, 400),
        new Shape(Radius: 8, ControlRadius: 6, BorderWidth: 1),
        Density.Standard,
        new ToolbarSpec(40),
        new SidebarSpec(DockEdge.Left, Width: 248, Height: 48, AutoHide: false, RevealEdge: 6, HideDelay: 350),
        new MotionSpec(Animations: true, Duration: 160),
        new PagesSpec(PageColorScheme.Theme),
        new HomeSpec(Clock: true, Search: true, Tiles: true, Glass: true));
}
