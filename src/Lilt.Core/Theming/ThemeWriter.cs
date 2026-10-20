using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lilt.Core.Theming;

// Serializes a theme back to the same format ThemeParser reads (used by the Customize panel).
public static class ThemeWriter
{
    public const string Header = "// Saved by Lilt's Customize panel. Hand edits are fine too; comments aren't kept on the next save.\n";

    public static string ToJson(Theme t)
    {
        var c = t.Colors;
        var b = t.Backdrop;
        JsonNode backdrop = b is { Tint: null, TintOpacity: null, LuminosityOpacity: null }
            ? Camel(b.Material)
            : Obj(("material", Camel(b.Material)), ("tint", b.Tint?.ToString()), ("tintOpacity", b.TintOpacity), ("luminosityOpacity", b.LuminosityOpacity));
        var root = new JsonObject
        {
            ["$schema"] = "./theme.schema.json",
            ["schemaVersion"] = ThemeParser.CurrentVersion,
            ["name"] = t.Name,
            ["base"] = Camel(t.Base),
            ["backdrop"] = backdrop,
            ["colors"] = Obj(
                ("background", c.Background.ToString()), ("surface", c.Surface.ToString()), ("surfaceRaised", c.SurfaceRaised.ToString()),
                ("border", c.Border.ToString()), ("text", c.Text.ToString()), ("textMuted", c.TextMuted.ToString()),
                ("accent", c.Accent.ToString()), ("onAccent", c.OnAccent.ToString()), ("hover", c.Hover.ToString()),
                ("pressed", c.Pressed.ToString()), ("page", c.Page.ToString())),
            ["typography"] = Obj(("family", t.Typography.Family), ("size", t.Typography.Size), ("weight", t.Typography.Weight)),
            ["shape"] = Obj(("radius", t.Shape.Radius), ("controlRadius", t.Shape.ControlRadius), ("borderWidth", t.Shape.BorderWidth)),
            ["density"] = Camel(t.Density),
            ["toolbar"] = With(Obj(("height", t.Toolbar.Height)), "items", t.Toolbar.Items),
            ["sidebar"] = With(Obj(
                ("edge", Camel(t.Sidebar.Edge)), ("width", t.Sidebar.Width), ("height", t.Sidebar.Height),
                ("autoHide", t.Sidebar.AutoHide), ("revealEdge", t.Sidebar.RevealEdge), ("hideDelay", t.Sidebar.HideDelay)), "sections", t.Sidebar.Sections),
            ["motion"] = Obj(("animations", t.Motion.Animations), ("duration", t.Motion.Duration)),
            ["pages"] = Obj(("colorScheme", Camel(t.Pages.ColorScheme))),
            ["home"] = With(Obj(("glass", t.Home.Glass), ("background", t.Home.Background)), "items", t.Home.Items),
            ["power"] = Obj(("efficiency", t.Power.Efficiency), ("suspendMinutes", t.Power.SuspendMinutes), ("hideWhenCovered", t.Power.HideWhenCovered)),
        };
        return Header + root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }

    static JsonObject With(JsonObject o, string key, Ids ids)
    {
        o[key] = new JsonArray(ids.Items.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray());
        return o;
    }

    static JsonObject Obj(params (string Key, object? Value)[] pairs)
    {
        var o = new JsonObject();
        foreach (var (key, value) in pairs)
        {
            if (value is null) continue;
            o[key] = value switch
            {
                double d => JsonValue.Create(Math.Round(d, 3)),
                int i => JsonValue.Create(i),
                bool x => JsonValue.Create(x),
                _ => JsonValue.Create(Convert.ToString(value, CultureInfo.InvariantCulture)),
            };
        }
        return o;
    }

    static string Camel<T>(T value) where T : struct, Enum
    {
        var s = value.ToString();
        return char.ToLowerInvariant(s[0]) + s[1..];
    }
}
