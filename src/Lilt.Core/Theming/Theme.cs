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
// AddressWidth: the address bar's widest width. SiteColor: how strongly the toolbar takes on the site's own color (0 = never).
public sealed record ToolbarSpec(double Height, Ids Items, double AddressWidth = 900, double SiteColor = 0.45)
{
    public static readonly string[] All = ["back", "forward", "reload", "home", "address", "bookmark", "media", "split", "panel", "downloads", "extensions", "palette", "customize", "sidebar", "menu"];
    public static readonly string[] Defaults = ["back", "forward", "reload", "home", "address", "bookmark", "media", "panel", "downloads", "extensions", "customize", "sidebar", "menu"];
}

// TabWidth: a tab's width in a tab strip (sidebar along the top or bottom). CloseButtons: an × on every tab.
// SiteColor: how strongly the sidebar takes on the site's own color (0 = never).
public sealed record SidebarSpec(DockEdge Edge, double Width, double Height, bool AutoHide, double RevealEdge, int HideDelay, Ids Sections,
    double TabWidth = 200, bool CloseButtons = true, double SiteColor = 0.2)
{
    public static readonly string[] All = ["tabs", "bookmarks", "layout"];
}

// WindowDrag: the window's content lifts slightly while you drag the window around.
public sealed record MotionSpec(bool Animations, int Duration, bool WindowDrag = true);

// How hard the engine blocks trackers (WebView2's tracking prevention levels).
public enum TrackingLevel { Off, Basic, Balanced, Strict }

public enum Scrollbars { Overlay, Classic }

// Peek: Shift+click a link to preview it in a panel over the page.
// Search: the address bar's search engine (a name like "duckduckgo" or "google", or an https address with %s).
// History: remember visited pages for address-bar suggestions (never in InPrivate windows).
// Glass: websites become see-through so the window material shows behind them (GlassOpacity is how much of the
// page's own color stays; GlassOff lists sites left alone). ForceDark darkens sites that have no dark look of their own;
// Scrollbars and ForceDark apply after a restart. Gestures: hold the right button and draw (← back, → forward,
// ↑↓ reload, ↓→ close tab, ↓ new tab). Restore: start with the tabs from last time (off: a fresh home tab each start).
// SmoothScrolling: animated scrolling in pages (off: instant, a little lighter; after a restart).
public sealed record PagesSpec(PageColorScheme ColorScheme, bool Peek, string Search = "duckduckgo", bool History = true,
    bool Glass = false, double GlassOpacity = 0.72, Ids? GlassOff = null, bool ForceDark = false, TrackingLevel Tracking = TrackingLevel.Balanced,
    bool Gestures = true, Scrollbars Scrollbars = Scrollbars.Overlay, bool Restore = true, bool SmoothScrolling = true)
{
    public Ids GlassOffSites => GlassOff ?? new Ids();
    public bool Equals(PagesSpec? o) => o is not null && ColorScheme == o.ColorScheme && Peek == o.Peek && Search == o.Search && History == o.History
        && Glass == o.Glass && GlassOpacity == o.GlassOpacity && GlassOffSites.Equals(o.GlassOffSites) && ForceDark == o.ForceDark
        && Tracking == o.Tracking && Gestures == o.Gestures && Scrollbars == o.Scrollbars && Restore == o.Restore && SmoothScrolling == o.SmoothScrolling;
    public override int GetHashCode() => HashCode.Combine(ColorScheme, Search, Glass, GlassOpacity);
}

// The home tab. Glass makes its background transparent so the window material shows through.
// Background: "" for none, "hills" for the built-in scenery, or an image file name in the newtab folder.
// Background: "" none, "hills", "aurora" or "waves" (drawn from the theme's colors), or an image file in the newtab folder.
// Dim darkens an image so the home tab stays readable (0 = off).
public sealed record HomeSpec(Ids Items, bool Glass, string Background, double Dim = 0)
{
    public static readonly string[] Scenes = ["hills", "aurora", "waves"];
    public static readonly string[] All = ["logo", "clock", "search", "tiles", "hint", "gear"];
}

// How hard Lilt saves power. SuspendMinutes 0 never suspends background tabs. HideWhenCovered pauses pages behind
// full-screen apps; HideWhenOccluded hides (throttles, without suspending) pages fully behind another window.
// Lean trades a little speed for less memory (Chromium's low-end-device mode and one process per site); it needs a restart.
// SleepMinutes: tabs you haven't looked at for that long are put to sleep, their page closed so it uses no memory at all
// (the tab stays and reloads when you open it; 0 = never; pinned tabs and tabs playing sound never sleep).
// SleepWhenLowMemory: when Windows runs short of memory, tabs unused for a few minutes sleep early.
// SolidWhenAway: while efficiency mode has Lilt in the background, the window material turns solid (Acrylic otherwise
// keeps blurring whatever moves behind the window).
public sealed record PowerSpec(bool Efficiency, int SuspendMinutes, bool HideWhenCovered, bool HideWhenOccluded, bool Lean,
    int SleepMinutes = 60, bool SleepWhenLowMemory = true, bool SolidWhenAway = false);

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
// Fps: how often the visualizer redraws (lower saves power).
public sealed record MediaSpec(WidgetSpec Player, WidgetSpec Visualizer, WidgetSpec Pip, int Bars, VisualizerStyle Style, VisualizerSource Source,
    bool MediaKeys, bool AutoPictureInPicture, Ids MusicSites, int Fps = 30)
{
    public static readonly string[] DefaultMusicSites =
    [
        "music.youtube.com", "open.spotify.com", "soundcloud.com", "music.apple.com", "listen.tidal.com", "www.deezer.com",
        "bandcamp.com", "www.pandora.com", "music.amazon.com", "www.mixcloud.com", "tunein.com", "radio.garden",
    ];
}

// The panel beside the page: tabs you keep there (music, chat, a reference), and the visualizer and player docked
// under them if you like. Edge: left or right. Sections, top to bottom: "web", "visualizer", "player"; leave one out to
// keep it floating (the visualizer and player) or hidden (web).
public sealed record PanelSpec(DockEdge Edge, double Width, Ids Sections)
{
    public static readonly string[] All = ["web", "visualizer", "player"];
}

// What sites keep on your PC, trimmed without signing you out. CacheLimit: MB of cached files the engine may keep
// (0 = its own limit; applies after a restart); sites' offline copies are emptied once they pass it too.
// ForgetAfterDays: sites you haven't opened in that many days lose what they stored and their readable cookies (0 = never);
// their sign-in cookies (the ones pages can't read) stay unless SignOut is on. Sites in Keep, bookmarked sites and sites
// open in a tab are never touched.
public sealed record StorageSpec(int CacheLimit, int ForgetAfterDays, Ids Keep, bool SignOut = false);

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
    ShortcutMap Shortcuts,
    PanelSpec Panel,
    StorageSpec Storage)
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
        ShortcutMap.Default,
        new PanelSpec(DockEdge.Right, Width: 380, new Ids(PanelSpec.All)),
        new StorageSpec(CacheLimit: 300, ForgetAfterDays: 90, new Ids()));
}
