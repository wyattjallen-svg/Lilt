using System.Text.Json;
using Lilt.Core.Layout;

namespace Lilt.Core.Theming;

public sealed record ThemeIssue(string Path, string Message, bool IsError = false)
{
    public override string ToString() => Path.Length == 0 ? Message : $"{Path}: {Message}";
}

// ReadError: the file exists but could not be read (often locked mid-save); worth retrying.
public sealed record ThemeLoadResult(Theme Theme, IReadOnlyList<ThemeIssue> Issues, bool Failed, bool ReadError = false);

public static class ThemeParser
{
    public const int CurrentVersion = 1;
    public const long MaxBytes = 1024 * 1024;

    public static IReadOnlyDictionary<string, string[]> KnownKeys { get; } = new Dictionary<string, string[]>
    {
        [""] = ["$schema", "schemaVersion", "name", "base", "backdrop", "colors", "typography", "shape", "density", "toolbar", "sidebar", "motion", "pages", "home", "power", "media", "split", "shortcuts", "panel", "storage"],
        ["backdrop"] = ["material", "tint", "tintOpacity", "luminosityOpacity"],
        ["colors"] = ["background", "surface", "surfaceRaised", "border", "text", "textMuted", "accent", "onAccent", "hover", "pressed", "page"],
        ["typography"] = ["family", "size", "weight"],
        ["shape"] = ["radius", "controlRadius", "borderWidth"],
        ["toolbar"] = ["height", "items", "addressWidth", "siteColor"],
        ["sidebar"] = ["edge", "width", "height", "autoHide", "revealEdge", "hideDelay", "sections", "tabWidth", "closeButtons", "siteColor"],
        ["motion"] = ["animations", "duration", "windowDrag"],
        ["pages"] = ["colorScheme", "peek", "search", "history", "glass", "glassOpacity", "glassOff", "forceDark", "tracking", "gestures", "scrollbars", "restore", "smoothScrolling"],
        // clock/search/tiles are the v0.2 switches, still read when "items" is absent.
        ["home"] = ["items", "glass", "background", "dim", "clock", "search", "tiles"],
        ["power"] = ["efficiency", "suspendMinutes", "hideWhenCovered", "hideWhenOccluded", "lean", "sleepMinutes", "sleepWhenLowMemory", "solidWhenAway"],
        ["media"] = ["player", "visualizer", "pip", "bars", "style", "source", "mediaKeys", "autoPictureInPicture", "musicSites", "fps"],
        ["media.player"] = ["show", "x", "y", "width", "height", "opacity"],
        ["media.visualizer"] = ["show", "x", "y", "width", "height", "opacity"],
        ["media.pip"] = ["show", "x", "y", "width", "height", "opacity"],
        ["split"] = ["orientation", "ratio", "gap"],
        ["panel"] = ["edge", "width", "sections"],
        ["storage"] = ["cacheLimit", "forgetAfterDays", "keep", "signOut"],
        ["shortcuts"] = Commands.Defaults.Keys.ToArray(),
    };

    static readonly JsonDocumentOptions jsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 16,
    };

    public static ThemeLoadResult LoadFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return Fail($"Theme file not found: {path}");
            if (info.Length > MaxBytes) return Fail($"Theme file is larger than {MaxBytes / 1024} KB.");
            return Parse(File.ReadAllText(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Fail($"Could not read theme file: {e.Message}") with { ReadError = true };
        }
    }

    public static ThemeLoadResult Parse(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, jsonOptions);
        }
        catch (JsonException e)
        {
            var where = e.LineNumber is long line ? $" at line {line + 1}, position {e.BytePositionInLine + 1}" : "";
            return Fail($"Invalid JSON{where}.");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Fail("The theme must be a JSON object.");

            var issues = new List<ThemeIssue>();
            var r = new Reader(issues);
            var d = Theme.Default;
            var top = new Obj(root, "");
            r.Unknown(root, "");

            int version;
            if (!root.TryGetProperty("schemaVersion", out _))
            {
                issues.Add(new("schemaVersion", $"missing; assuming {CurrentVersion}"));
                version = CurrentVersion;
            }
            else
            {
                version = r.Int(top, "schemaVersion", 1, int.MaxValue, CurrentVersion);
                if (version > CurrentVersion)
                    issues.Add(new("schemaVersion", $"{version} is newer than this version of Lilt supports ({CurrentVersion}); unknown settings are ignored"));
            }

            var c = r.Section(root, "colors");
            var dc = d.Colors;
            var colors = new ThemeColors(
                r.Color(c, "background", dc.Background),
                r.Color(c, "surface", dc.Surface),
                r.Color(c, "surfaceRaised", dc.SurfaceRaised),
                r.Color(c, "border", dc.Border),
                r.Color(c, "text", dc.Text),
                r.Color(c, "textMuted", dc.TextMuted),
                r.Color(c, "accent", dc.Accent),
                r.Color(c, "onAccent", dc.OnAccent),
                r.Color(c, "hover", dc.Hover),
                r.Color(c, "pressed", dc.Pressed),
                r.Color(c, "page", dc.Page));

            var ty = r.Section(root, "typography");
            var typography = new Typography(
                r.Str(ty, "family", 100, d.Typography.Family),
                r.Num(ty, "size", 9, 24, d.Typography.Size),
                r.Int(ty, "weight", 100, 900, d.Typography.Weight));

            var sh = r.Section(root, "shape");
            var shape = new Shape(
                r.Num(sh, "radius", 0, 24, d.Shape.Radius),
                r.Num(sh, "controlRadius", 0, 24, d.Shape.ControlRadius),
                r.Num(sh, "borderWidth", 0, 4, d.Shape.BorderWidth));

            var tb = r.Section(root, "toolbar");
            var toolbar = new ToolbarSpec(r.Num(tb, "height", 32, 64, d.Toolbar.Height), r.List(tb, "items", ToolbarSpec.All, d.Toolbar.Items),
                r.Num(tb, "addressWidth", 300, 2400, d.Toolbar.AddressWidth), r.Num(tb, "siteColor", 0, 1, d.Toolbar.SiteColor));

            var sb = r.Section(root, "sidebar");
            var ds = d.Sidebar;
            var sidebar = new SidebarSpec(
                r.Enum(sb, "edge", ds.Edge),
                r.Num(sb, "width", 120, 600, ds.Width),
                r.Num(sb, "height", 32, 200, ds.Height),
                r.Bool(sb, "autoHide", ds.AutoHide),
                r.Num(sb, "revealEdge", 1, 32, ds.RevealEdge),
                r.Int(sb, "hideDelay", 0, 5000, ds.HideDelay),
                r.List(sb, "sections", SidebarSpec.All, ds.Sections),
                r.Num(sb, "tabWidth", 100, 400, ds.TabWidth),
                r.Bool(sb, "closeButtons", ds.CloseButtons),
                r.Num(sb, "siteColor", 0, 1, ds.SiteColor));

            var mo = r.Section(root, "motion");
            var motion = new MotionSpec(
                r.Bool(mo, "animations", d.Motion.Animations),
                r.Int(mo, "duration", 0, 1000, d.Motion.Duration),
                r.Bool(mo, "windowDrag", d.Motion.WindowDrag));

            var pg = r.Section(root, "pages");
            var search = r.Str(pg, "search", 300, d.Pages.Search);
            if (!Navigation.SearchEngines.IsValid(search))
            {
                issues.Add(new("pages.search", $"\"{search}\" isn't a search engine Lilt knows ({string.Join(", ", Navigation.SearchEngines.Known.Keys)}) or an https address with %s; using {d.Pages.Search}"));
                search = d.Pages.Search;
            }
            var pages = new PagesSpec(r.Enum(pg, "colorScheme", d.Pages.ColorScheme), r.Bool(pg, "peek", d.Pages.Peek), search, r.Bool(pg, "history", d.Pages.History),
                r.Bool(pg, "glass", d.Pages.Glass), r.Num(pg, "glassOpacity", 0.2, 1, d.Pages.GlassOpacity), r.Hosts(pg, "glassOff", d.Pages.GlassOffSites),
                r.Bool(pg, "forceDark", d.Pages.ForceDark), r.Enum(pg, "tracking", d.Pages.Tracking), r.Bool(pg, "gestures", d.Pages.Gestures),
                r.Enum(pg, "scrollbars", d.Pages.Scrollbars), r.Bool(pg, "restore", d.Pages.Restore), r.Bool(pg, "smoothScrolling", d.Pages.SmoothScrolling));

            var hm = r.Section(root, "home");
            var homeItems = d.Home.Items;
            if (hm is { } h && !h.Element.TryGetProperty("items", out _) && new[] { "clock", "search", "tiles" }.Any(k => h.Element.TryGetProperty(k, out _)))
                homeItems = new Ids(new[] { "clock", "search", "tiles" }.Where(k => r.Bool(hm, k, true)).ToArray());
            else
                homeItems = r.List(hm, "items", HomeSpec.All, d.Home.Items);
            var home = new HomeSpec(homeItems, r.Bool(hm, "glass", d.Home.Glass), r.Background(hm, "background", d.Home.Background), r.Num(hm, "dim", 0, 0.8, d.Home.Dim));

            var pw = r.Section(root, "power");
            var power = new PowerSpec(
                r.Bool(pw, "efficiency", d.Power.Efficiency),
                r.Int(pw, "suspendMinutes", 0, 240, d.Power.SuspendMinutes),
                r.Bool(pw, "hideWhenCovered", d.Power.HideWhenCovered),
                r.Bool(pw, "hideWhenOccluded", d.Power.HideWhenOccluded),
                r.Bool(pw, "lean", d.Power.Lean),
                r.Int(pw, "sleepMinutes", 0, 1440, d.Power.SleepMinutes),
                r.Bool(pw, "sleepWhenLowMemory", d.Power.SleepWhenLowMemory),
                r.Bool(pw, "solidWhenAway", d.Power.SolidWhenAway));

            var md = r.Section(root, "media");
            var dm = d.Media;
            var media = new MediaSpec(
                r.Widget(r.Sub(md, "player"), dm.Player),
                r.Widget(r.Sub(md, "visualizer"), dm.Visualizer),
                r.Widget(r.Sub(md, "pip"), dm.Pip),
                r.Int(md, "bars", 4, 96, dm.Bars),
                r.Enum(md, "style", dm.Style),
                r.Enum(md, "source", dm.Source),
                r.Bool(md, "mediaKeys", dm.MediaKeys),
                r.Bool(md, "autoPictureInPicture", dm.AutoPictureInPicture),
                r.Hosts(md, "musicSites", dm.MusicSites),
                r.Int(md, "fps", 10, 60, dm.Fps));

            var sp = r.Section(root, "split");
            var split = new SplitSpec(
                r.Enum(sp, "orientation", d.Split.Orientation),
                r.Num(sp, "ratio", 0.15, 0.85, d.Split.Ratio),
                r.Num(sp, "gap", 0, 24, d.Split.Gap));

            var pn = r.Section(root, "panel");
            var panelEdge = r.Enum(pn, "edge", d.Panel.Edge);
            if (!panelEdge.IsVertical())
            {
                issues.Add(new("panel.edge", "the panel goes on the left or the right; using right"));
                panelEdge = DockEdge.Right;
            }
            var panel = new PanelSpec(panelEdge, r.Num(pn, "width", 200, 1200, d.Panel.Width), r.List(pn, "sections", PanelSpec.All, d.Panel.Sections));

            var st = r.Section(root, "storage");
            var cacheLimit = r.Int(st, "cacheLimit", 0, 8192, d.Storage.CacheLimit);
            var storage = new StorageSpec(cacheLimit is > 0 and < 50 ? 50 : cacheLimit, r.Int(st, "forgetAfterDays", 0, 3650, d.Storage.ForgetAfterDays),
                r.Hosts(st, "keep", d.Storage.Keep), r.Bool(st, "signOut", d.Storage.SignOut));

            var sc = r.Section(root, "shortcuts");
            var shortcuts = new ShortcutMap(Commands.Defaults.Keys.ToDictionary(k => k, k => r.Gestures(sc, k, d.Shortcuts.For(k))));

            // "backdrop" is either a material name or an object with tint settings.
            BackdropSpec backdrop;
            if (root.TryGetProperty("backdrop", out var bd) && bd.ValueKind == JsonValueKind.Object)
            {
                var b = r.Section(root, "backdrop");
                backdrop = new BackdropSpec(
                    r.Enum(b, "material", d.Backdrop.Material),
                    r.OptColor(b, "tint"),
                    r.OptNum(b, "tintOpacity", 0, 1),
                    r.OptNum(b, "luminosityOpacity", 0, 1));
            }
            else
            {
                backdrop = new BackdropSpec(r.Enum(top, "backdrop", d.Backdrop.Material));
            }

            var theme = new Theme(
                Math.Min(version, CurrentVersion),
                r.Str(top, "name", 64, d.Name),
                r.Enum(top, "base", d.Base),
                backdrop,
                colors,
                typography,
                shape,
                r.Enum(top, "density", d.Density),
                toolbar,
                sidebar,
                motion,
                pages,
                home,
                power,
                media,
                split,
                shortcuts,
                panel,
                storage);

            return new ThemeLoadResult(theme, issues, false);
        }
    }

    static ThemeLoadResult Fail(string message) => new(Theme.Default, [new ThemeIssue("", message, IsError: true)], true);

    readonly struct Obj(JsonElement element, string path)
    {
        public readonly JsonElement Element = element;
        public readonly string Path = path;
    }

    sealed class Reader(List<ThemeIssue> issues)
    {
        public void Unknown(JsonElement obj, string path)
        {
            var known = KnownKeys[path];
            foreach (var p in obj.EnumerateObject())
                if (Array.IndexOf(known, p.Name) < 0)
                    issues.Add(new(Join(path, p.Name), "unknown setting, ignored"));
        }

        public Obj? Sub(Obj? parent, string name)
        {
            if (parent is not { } p || !p.Element.TryGetProperty(name, out var v)) return null;
            var path = Join(p.Path, name);
            if (v.ValueKind != JsonValueKind.Object)
            {
                issues.Add(new(path, "expected an object; using defaults"));
                return null;
            }
            Unknown(v, path);
            return new Obj(v, path);
        }

        public WidgetSpec Widget(Obj? o, WidgetSpec d) => new(
            Enum(o, "show", d.Show), Num(o, "x", 0, 1, d.X), Num(o, "y", 0, 1, d.Y),
            Num(o, "width", 120, 900, d.Width), Num(o, "height", 0, 600, d.Height), Num(o, "opacity", 0.2, 1, d.Opacity));

        // Host names only ("open.spotify.com"); anything else is dropped with a warning.
        public Ids Hosts(Obj? o, string key, Ids fallback)
        {
            if (!Get(o, key, out var v, out var path)) return fallback;
            if (v.ValueKind != JsonValueKind.Array) return Bad(path, "expected a list of site names like \"open.spotify.com\"", fallback);
            var hosts = new List<string>();
            foreach (var e in v.EnumerateArray())
            {
                var h = e.ValueKind == JsonValueKind.String ? e.GetString()?.Trim().ToLowerInvariant() : null;
                if (h is { Length: > 0 and <= 253 } && Uri.CheckHostName(h) == UriHostNameType.Dns && !hosts.Contains(h)) hosts.Add(h);
                else issues.Add(new(path, $"{(h is null ? "a non-text entry" : $"\"{h}\"")} isn't a site name; skipped"));
            }
            return new Ids(hosts.ToArray());
        }

        // A shortcut: "Ctrl+Shift+T", a list of them, or "" / [] for none.
        public Gesture[] Gestures(Obj? o, string key, Gesture[] fallback)
        {
            if (!Get(o, key, out var v, out var path)) return fallback;
            var texts = v.ValueKind switch
            {
                JsonValueKind.String => [v.GetString()!],
                JsonValueKind.Array => v.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString()! : "?").ToArray(),
                _ => (string[]?)null,
            };
            if (texts is null) return Bad(path, "expected a shortcut like \"Ctrl+Shift+T\", a list of them, or \"\"", fallback);
            var list = new List<Gesture>();
            foreach (var t in texts.Where(t => t.Length > 0))
            {
                if (Gesture.TryParse(t, out var g)) list.Add(g);
                else issues.Add(new(path, $"\"{t}\" isn't a shortcut Lilt understands (Ctrl/Shift/Alt plus a key; letters need Ctrl or Alt); skipped"));
            }
            return list.ToArray();
        }

        public Obj? Section(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out var v)) return null;
            if (v.ValueKind != JsonValueKind.Object)
            {
                issues.Add(new(name, "expected an object; using defaults"));
                return null;
            }
            Unknown(v, name);
            return new Obj(v, name);
        }

        public Rgba Color(Obj? o, string key, Rgba fallback)
        {
            if (!Get(o, key, out var v, out var path)) return fallback;
            if (v.ValueKind == JsonValueKind.String && Rgba.TryParse(v.GetString(), out var c)) return c;
            return Bad(path, "expected a color like \"#RRGGBB\" or \"#RRGGBBAA\"", fallback);
        }

        public Rgba? OptColor(Obj? o, string key)
        {
            if (!Get(o, key, out var v, out var path)) return null;
            if (v.ValueKind == JsonValueKind.String && Rgba.TryParse(v.GetString(), out var c)) return c;
            issues.Add(new(path, "expected a color like \"#RRGGBB\"; ignored"));
            return null;
        }

        public double? OptNum(Obj? o, string key, double min, double max)
        {
            if (!Get(o, key, out var v, out var path)) return null;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) && n >= min && n <= max) return n;
            issues.Add(new(path, $"expected a number from {min} to {max}; ignored"));
            return null;
        }

        public double Num(Obj? o, string key, double min, double max, double fallback)
        {
            if (!Get(o, key, out var v, out var path)) return fallback;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) && n >= min && n <= max) return n;
            return Bad(path, $"expected a number from {min} to {max}", fallback);
        }

        public int Int(Obj? o, string key, int min, int max, int fallback)
        {
            if (!Get(o, key, out var v, out var path)) return fallback;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) && n >= min && n <= max) return n;
            return Bad(path, max == int.MaxValue ? $"expected a whole number of at least {min}" : $"expected a whole number from {min} to {max}", fallback);
        }

        // A list of known ids; unknown or repeated ids are dropped with a warning. An empty list is valid (everything hidden).
        public Ids List(Obj? o, string key, string[] known, Ids fallback)
        {
            if (!Get(o, key, out var v, out var path)) return fallback;
            if (v.ValueKind != JsonValueKind.Array) return Bad(path, $"expected a list drawn from: {string.Join(", ", known)}", fallback);
            var items = new List<string>();
            foreach (var e in v.EnumerateArray())
            {
                var id = e.ValueKind == JsonValueKind.String ? e.GetString() : null;
                if (id is not null && Array.IndexOf(known, id) >= 0 && !items.Contains(id)) items.Add(id);
                else issues.Add(new(path, $"{(id is null ? "a non-text entry" : $"\"{id}\"")} isn't one of: {string.Join(", ", known)}; skipped"));
            }
            return new Ids(items.ToArray());
        }

        // "", "hills", or a plain image file name in the newtab folder (no folders, so a theme can't point at other files).
        public string Background(Obj? o, string key, string fallback)
        {
            if (!Get(o, key, out var v, out var path)) return fallback;
            if (v.ValueKind == JsonValueKind.String && v.GetString() is { } s && IsBackground(s)) return s;
            return Bad(path, "expected \"\", \"hills\", \"aurora\", \"waves\" or an image file name like \"background.jpg\"", fallback);
        }

        public bool Bool(Obj? o, string key, bool fallback)
        {
            if (!Get(o, key, out var v, out var path)) return fallback;
            if (v.ValueKind is JsonValueKind.True or JsonValueKind.False) return v.GetBoolean();
            return Bad(path, "expected true or false", fallback);
        }

        public string Str(Obj? o, string key, int maxLength, string fallback)
        {
            if (!Get(o, key, out var v, out var path)) return fallback;
            if (v.ValueKind == JsonValueKind.String && v.GetString() is { } s && s.Trim().Length > 0 && s.Length <= maxLength) return s.Trim();
            return Bad(path, $"expected text of 1 to {maxLength} characters", fallback);
        }

        public T Enum<T>(Obj? o, string key, T fallback) where T : struct, System.Enum
        {
            if (!Get(o, key, out var v, out var path)) return fallback;
            if (v.ValueKind == JsonValueKind.String && v.GetString() is { } s)
                foreach (var value in System.Enum.GetValues<T>())
                    if (string.Equals(value.ToString(), s, StringComparison.OrdinalIgnoreCase)) return value;
            var names = string.Join(", ", System.Enum.GetNames<T>().Select(Camel));
            return Bad(path, $"expected one of: {names}", fallback);
        }

        static bool Get(Obj? o, string key, out JsonElement value, out string path)
        {
            value = default;
            path = key;
            if (o is not { } obj) return false;
            path = Join(obj.Path, key);
            return obj.Element.TryGetProperty(key, out value);
        }

        T Bad<T>(string path, string message, T fallback)
        {
            var shown = fallback switch
            {
                bool b => b ? "true" : "false",
                System.Enum e => Camel(e.ToString()),
                string s => $"\"{s}\"",
            Gesture[] g => g.Length == 0 ? "none" : string.Join(" or ", g.Select(x => x.ToString())),
                IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
                _ => fallback?.ToString(),
            };
            issues.Add(new(path, $"{message}; using {shown}"));
            return fallback;
        }
    }

    public static bool IsBackground(string s) =>
        s.Length == 0 || HomeSpec.Scenes.Contains(s)
        || (s.Length <= 100 && s.IndexOfAny(['/', '\\', ':']) < 0 && !s.StartsWith('.')
            && new[] { ".png", ".jpg", ".jpeg", ".webp", ".gif", ".svg" }.Contains(Path.GetExtension(s).ToLowerInvariant()));

    static string Join(string path, string key) => path.Length == 0 ? key : $"{path}.{key}";

    static string Camel(string s) => s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s[1..];
}
