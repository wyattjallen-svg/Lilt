using Lilt.Core;
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
    public static readonly string[] All = ["back", "forward", "reload", "home", "address", "bookmark", "media", "split", "downloads", "extensions", "palette", "customize", "sidebar", "menu"];
    public static readonly string[] Defaults = ["back", "forward", "reload", "home", "address", "bookmark", "media", "downloads", "extensions", "customize", "sidebar", "menu"];
}

public sealed record SidebarSpec(DockEdge Edge, double Width, double Height, bool AutoHide, double RevealEdge, int HideDelay, Ids Sections)
{
    public static readonly string[] All = ["tabs", "bookmarks", "layout"];
}

public sealed record MotionSpec(bool Animations, int Duration);

// Peek: Shift+click a link to preview it in a panel over the page.
public sealed record PagesSpec(PageColorScheme ColorScheme, bool Peek);

// The home tab. Glass makes its background transparent so the window material shows through.
// Background: "" for none, "hills" for the built-in scenery, or an image file name in the newtab folder.
public sealed record HomeSpec(Ids Items, bool Glass, string Background)
{
    public static readonly string[] All = ["logo", "clock", "search", "tiles", "hint", "gear"];
}

// How hard Lilt saves power. SuspendMinutes 0 never suspends background tabs. HideWhenCovered pauses pages behind
// full-screen apps; HideWhenOccluded hides (throttles, without suspending) pages fully behind another window.
// Lean trades a little speed for less memory (Chromium's low-end-device mode and one process per site); it needs a restart.
public sealed record PowerSpec(bool Efficiency, int SuspendMinutes, bool HideWhenCovered, bool HideWhenOccluded, bool Lean);

public enum WidgetShow { Auto, Always, Never }

// A floating panel over the page, dragged anywhere. X and Y place its top-left corner as a fraction of the page area,
// so it keeps its place when the window resizes. Height 0 sizes to content.
public sealed record WidgetSpec(WidgetShow Show, double X, double Y, double Width, double Height, double Opacity);

public enum VisualizerStyle { Bars, Mirror, Wave }

// Where the visualizer's audio comes from: System listens to Lilt's own audio output (works with every site, needs
// Windows 11); Page reads the playing tab's media element (no DRM sites); Auto tries System first.
public enum VisualizerSource { Auto, System, Page }

// Media: the "now playing" panel, the visualizer and picture in picture. Auto shows the panel while a tab you can't see
// plays, and the visualizer while a music site in MusicSites plays. Pip places videos popped out inside Lilt; its Show
// picks Windows' own floating window when available (auto), always inside Lilt (always), or off (never). Height 0 is 16:9.
public sealed record MediaSpec(WidgetSpec Player, WidgetSpec Visualizer, WidgetSpec Pip, int Bars, VisualizerStyle Style, VisualizerSource Source,
    bool MediaKeys, bool AutoPictureInPicture, Ids MusicSites)
{
    public static readonly string[] DefaultMusicSites =
    [
        "music.youtube.com", "open.spotify.com", "soundcloud.com", "music.apple.com", "listen.tidal.com", "www.deezer.com",
        "bandcamp.com", "www.pandora.com", "music.amazon.com", "www.mixcloud.com", "tunein.com", "radio.garden",
    ];
}

public enum SplitOrientation { Horizontal, Vertical }

// Split view: two tabs side by side (horizontal) or stacked (vertical). Ratio is the first pane's share.
public sealed record SplitSpec(SplitOrientation Orientation, double Ratio, double Gap);

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
    PowerSpec Power,
    MediaSpec Media,
    SplitSpec Split,
    ShortcutMap Shortcuts)
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
        new ToolbarSpec(40, new Ids(ToolbarSpec.Defaults)),
        new SidebarSpec(DockEdge.Left, Width: 220, Height: 44, AutoHide: false, RevealEdge: 6, HideDelay: 350, new Ids(SidebarSpec.All)),
        new MotionSpec(Animations: true, Duration: 160),
        new PagesSpec(PageColorScheme.Theme, Peek: true),
        new HomeSpec(new Ids("logo", "search", "tiles", "gear"), Glass: true, Background: ""),
        new PowerSpec(Efficiency: true, SuspendMinutes: 5, HideWhenCovered: true, HideWhenOccluded: true, Lean: false),
        new MediaSpec(
            Player: new WidgetSpec(WidgetShow.Auto, X: 0.66, Y: 0.74, Width: 300, Height: 0, Opacity: 1),
            Visualizer: new WidgetSpec(WidgetShow.Auto, X: 0.66, Y: 0.6, Width: 300, Height: 72, Opacity: 0.9),
            Pip: new WidgetSpec(WidgetShow.Auto, X: 0.6, Y: 0.55, Width: 400, Height: 0, Opacity: 1),
            Bars: 32, VisualizerStyle.Bars, VisualizerSource.Auto, MediaKeys: true, AutoPictureInPicture: false, new Ids(MediaSpec.DefaultMusicSites)),
        new SplitSpec(SplitOrientation.Horizontal, Ratio: 0.5, Gap: 6),
        ShortcutMap.Default);
}
