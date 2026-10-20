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

// An ordered list of element ids: position in the list is position on screen, and leaving an id out hides it.
public sealed record Ids(IReadOnlyList<string> Items)
{
    public Ids(params string[] items) : this((IReadOnlyList<string>)items) { }
    public bool Has(string id) => Items.Contains(id);
    public int IndexOf(string id) => Items.ToList().IndexOf(id);
    public bool Equals(Ids? other) => other is not null && Items.SequenceEqual(other.Items);
    public override int GetHashCode() => string.Join('|', Items).GetHashCode();
    public override string ToString() => string.Join(", ", Items);
}

// Every toolbar element; "address" stretches, the rest sit to its left or right in list order.
public sealed record ToolbarSpec(double Height, Ids Items)
{
    public static readonly string[] All = ["back", "forward", "reload", "home", "address", "bookmark", "downloads", "extensions", "customize", "sidebar", "menu"];
}

public sealed record SidebarSpec(DockEdge Edge, double Width, double Height, bool AutoHide, double RevealEdge, int HideDelay, Ids Sections)
{
    public static readonly string[] All = ["tabs", "bookmarks", "layout"];
}

public sealed record MotionSpec(bool Animations, int Duration);

public sealed record PagesSpec(PageColorScheme ColorScheme);

// The home tab. Glass makes its background transparent so the window material shows through.
// Background: "" for none, "hills" for the built-in scenery, or an image file name in the newtab folder.
public sealed record HomeSpec(Ids Items, bool Glass, string Background)
{
    public static readonly string[] All = ["logo", "clock", "search", "tiles", "hint", "gear"];
}

// How hard Lilt saves power while you're in another app. SuspendMinutes 0 never suspends background tabs.
public sealed record PowerSpec(bool Efficiency, int SuspendMinutes, bool HideWhenCovered);

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
    HomeSpec Home,
    PowerSpec Power)
{
    // "Lilt Minimal": deep charcoal, slate surfaces, warm white text, sage accent.
    public static Theme Default { get; } = new(
        1,
        "Minimal",
        ThemeBase.Dark,
        new BackdropSpec(Theming.Backdrop.Mica),
        new ThemeColors(
            Background: Rgba.Parse("#171A1E"),
            Surface: Rgba.Parse("#171A1EE6"),
            SurfaceRaised: Rgba.Parse("#2A2F36"),
            Border: Rgba.Parse("#FFFFFF12"),
            Text: Rgba.Parse("#F3F4F1"),
            TextMuted: Rgba.Parse("#9AA3AD"),
            Accent: Rgba.Parse("#638B7B"),
            OnAccent: Rgba.Parse("#F3F4F1"),
            Hover: Rgba.Parse("#FFFFFF0D"),
            Pressed: Rgba.Parse("#FFFFFF17"),
            Page: Rgba.Parse("#171A1E")),
        new Typography("Segoe UI Variable Text", 13, 400),
        new Shape(Radius: 10, ControlRadius: 8, BorderWidth: 1),
        Density.Standard,
        new ToolbarSpec(40, new Ids(ToolbarSpec.All)),
        new SidebarSpec(DockEdge.Left, Width: 220, Height: 44, AutoHide: false, RevealEdge: 6, HideDelay: 350, new Ids(SidebarSpec.All)),
        new MotionSpec(Animations: true, Duration: 160),
        new PagesSpec(PageColorScheme.Theme),
        new HomeSpec(new Ids("logo", "search", "tiles", "gear"), Glass: true, Background: ""),
        new PowerSpec(Efficiency: true, SuspendMinutes: 5, HideWhenCovered: true));
}
