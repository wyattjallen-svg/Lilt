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
        [""] = ["$schema", "schemaVersion", "name", "base", "backdrop", "colors", "typography", "shape", "density", "toolbar", "sidebar", "motion", "pages"],
        ["colors"] = ["background", "surface", "surfaceRaised", "border", "text", "textMuted", "accent", "onAccent", "hover", "pressed", "page"],
        ["typography"] = ["family", "size", "weight"],
        ["shape"] = ["radius", "controlRadius", "borderWidth"],
        ["toolbar"] = ["height"],
        ["sidebar"] = ["edge", "width", "height", "autoHide", "revealEdge", "hideDelay"],
        ["motion"] = ["animations", "duration"],
        ["pages"] = ["colorScheme"],
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
            var toolbar = new ToolbarSpec(r.Num(tb, "height", 32, 64, d.Toolbar.Height));

            var sb = r.Section(root, "sidebar");
            var ds = d.Sidebar;
            var sidebar = new SidebarSpec(
                r.Enum(sb, "edge", ds.Edge),
                r.Num(sb, "width", 120, 600, ds.Width),
                r.Num(sb, "height", 32, 200, ds.Height),
                r.Bool(sb, "autoHide", ds.AutoHide),
                r.Num(sb, "revealEdge", 1, 32, ds.RevealEdge),
                r.Int(sb, "hideDelay", 0, 5000, ds.HideDelay));

            var mo = r.Section(root, "motion");
            var motion = new MotionSpec(
                r.Bool(mo, "animations", d.Motion.Animations),
                r.Int(mo, "duration", 0, 1000, d.Motion.Duration));

            var pg = r.Section(root, "pages");
            var pages = new PagesSpec(r.Enum(pg, "colorScheme", d.Pages.ColorScheme));

            var theme = new Theme(
                Math.Min(version, CurrentVersion),
                r.Str(top, "name", 64, d.Name),
                r.Enum(top, "base", d.Base),
                r.Enum(top, "backdrop", d.Backdrop),
                colors,
                typography,
                shape,
                r.Enum(top, "density", d.Density),
                toolbar,
                sidebar,
                motion,
                pages);

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
                IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
                _ => fallback?.ToString(),
            };
            issues.Add(new(path, $"{message}; using {shown}"));
            return fallback;
        }
    }

    static string Join(string path, string key) => path.Length == 0 ? key : $"{path}.{key}";

    static string Camel(string s) => s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s[1..];
}
