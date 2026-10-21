using System.Text.Json;
using System.Diagnostics;
using Lilt.Core;
using Lilt.Core.Bookmarks;
using Lilt.Core.Customization;
using Lilt.Core.Extensions;
using System.Security.Cryptography;
using System.Buffers.Binary;
using Lilt.Core.Layout;
using Lilt.Core.Navigation;
using Lilt.Core.Theming;
using static Lilt.Core.Tests.Check;

var themes = "src/Lilt.App/Themes";

Test("empty theme uses defaults, warns about missing version", () =>
{
    var r = ThemeParser.Parse("{}");
    True(!r.Failed);
    Equal(Theme.Default, r.Theme);
    Equal(1, r.Issues.Count);
    Equal("schemaVersion", r.Issues[0].Path);
});

Test("bundled minimal.json is exactly the built-in default", () =>
{
    var r = ThemeParser.LoadFile(RepoFile($"{themes}/minimal.json"));
    Equal(0, r.Issues.Count);
    Equal(Theme.Default, r.Theme);
});

Test("bundled light.json loads cleanly as a light theme", () =>
{
    var r = ThemeParser.LoadFile(RepoFile($"{themes}/light.json"));
    Equal(0, r.Issues.Count);
    Equal(ThemeBase.Light, r.Theme.Base);
    Equal(Backdrop.MicaAlt, r.Theme.Backdrop.Material);
    Equal("hills", r.Theme.Home.Background);
    True(!r.Theme.Home.Glass);
});

Test("comments and trailing commas are allowed", () =>
{
    var r = ThemeParser.Parse("""
        {
          // comment
          "schemaVersion": 1, /* block */
          "colors": { "accent": "#FF0000", },
        }
        """);
    True(!r.Failed);
    Equal(0, r.Issues.Count);
    Equal(new Rgba(255, 0, 0), r.Theme.Colors.Accent);
});

Test("syntax error fails with a line number", () =>
{
    var r = ThemeParser.Parse("{\n  \"schemaVersion\": 1,\n  \"name\": \n}");
    True(r.Failed);
    Equal(Theme.Default, r.Theme);
    True(r.Issues[0].IsError);
    True(r.Issues[0].Message.Contains("line 4"));
});

Test("root must be an object", () =>
{
    True(ThemeParser.Parse("[1,2]").Failed);
    True(ThemeParser.Parse("\"dark\"").Failed);
});

Test("bad color falls back for that value only", () =>
{
    var r = ThemeParser.Parse("""{ "schemaVersion": 1, "colors": { "accent": "purple", "text": "#000" } }""");
    Equal(Theme.Default.Colors.Accent, r.Theme.Colors.Accent);
    Equal(new Rgba(0, 0, 0), r.Theme.Colors.Text);
    Equal(1, r.Issues.Count);
    Equal("colors.accent", r.Issues[0].Path);
    True(r.Issues[0].Message.Contains("#638B7B"));
});

Test("out-of-range and wrong-type numbers fall back", () =>
{
    var r = ThemeParser.Parse("""{ "schemaVersion": 1, "toolbar": { "height": 400 }, "sidebar": { "width": "wide", "hideDelay": 10.5 } }""");
    Equal(Theme.Default.Toolbar.Height, r.Theme.Toolbar.Height);
    Equal(Theme.Default.Sidebar.Width, r.Theme.Sidebar.Width);
    Equal(Theme.Default.Sidebar.HideDelay, r.Theme.Sidebar.HideDelay);
    Equal(3, r.Issues.Count);
    True(r.Issues.Any(i => i.Path == "sidebar.hideDelay" && i.Message.Contains("whole number")));
});

Test("unknown keys are reported with their path", () =>
{
    var r = ThemeParser.Parse("""{ "schemaVersion": 1, "colour": {}, "colors": { "acent": "#fff" } }""");
    Equal(2, r.Issues.Count);
    True(r.Issues.Any(i => i.Path == "colour"));
    True(r.Issues.Any(i => i.Path == "colors.acent"));
});

Test("section with the wrong type keeps its defaults", () =>
{
    var r = ThemeParser.Parse("""{ "schemaVersion": 1, "colors": 5 }""");
    Equal(Theme.Default.Colors, r.Theme.Colors);
    Equal("colors", r.Issues.Single().Path);
});

Test("newer schema version loads with a warning", () =>
{
    var r = ThemeParser.Parse("""{ "schemaVersion": 7, "name": "Future" }""");
    True(!r.Failed);
    Equal("Future", r.Theme.Name);
    Equal(ThemeParser.CurrentVersion, r.Theme.SchemaVersion);
    Equal("schemaVersion", r.Issues.Single().Path);
});

Test("enums are case-insensitive and list choices when wrong", () =>
{
    var r = ThemeParser.Parse("""{ "schemaVersion": 1, "backdrop": "MicaAlt", "base": "dim", "sidebar": { "edge": "TOP" } }""");
    Equal(Backdrop.MicaAlt, r.Theme.Backdrop.Material);
    Equal(DockEdge.Top, r.Theme.Sidebar.Edge);
    Equal(ThemeBase.Dark, r.Theme.Base);
    True(r.Issues.Single().Message.Contains("dark, light"));
});

Test("blank and overlong text fall back", () =>
{
    var r = ThemeParser.Parse($$"""{ "schemaVersion": 1, "name": "  ", "typography": { "family": "{{new string('x', 101)}}" } }""");
    Equal(Theme.Default.Name, r.Theme.Name);
    Equal(Theme.Default.Typography.Family, r.Theme.Typography.Family);
    Equal(2, r.Issues.Count);
});

Test("missing and oversized files fail cleanly", () =>
{
    var missing = ThemeParser.LoadFile(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"));
    True(missing.Failed && missing.Issues[0].Message.Contains("not found"));

    var big = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
    File.WriteAllText(big, "{\"name\":\"" + new string('a', (int)ThemeParser.MaxBytes) + "\"}");
    try
    {
        var r = ThemeParser.LoadFile(big);
        True(r.Failed && r.Issues[0].Message.Contains("larger"));
    }
    finally { File.Delete(big); }
});

Test("schema file and validator know the same settings", () =>
{
    using var doc = JsonDocument.Parse(File.ReadAllText(RepoFile($"{themes}/theme.schema.json")));
    var props = doc.RootElement.GetProperty("properties");
    foreach (var (section, keys) in ThemeParser.KnownKeys)
    {
        var sectionNode = default(JsonElement);
        if (section.Length > 0)
        {
            var names = section.Split('.');
            sectionNode = props.GetProperty(names[0]);
            foreach (var n in names.Skip(1)) sectionNode = sectionNode.GetProperty("properties").GetProperty(n);
        }
        if (section.Length > 0 && sectionNode.TryGetProperty("anyOf", out var anyOf))
            sectionNode = anyOf.EnumerateArray().First(a => a.TryGetProperty("properties", out _));
        var node = section.Length == 0 ? props : sectionNode.GetProperty("properties");
        var schemaKeys = node.EnumerateObject().Select(p => p.Name).Order().ToArray();
        Equal(string.Join(",", keys.Order()), string.Join(",", schemaKeys));
    }
});

Test("colors parse in CSS order", () =>
{
    Equal(new Rgba(0x11, 0x22, 0x33), Rgba.Parse("#123"));
    Equal(new Rgba(0x12, 0x34, 0x56), Rgba.Parse("#123456"));
    Equal(new Rgba(0x12, 0x34, 0x56, 0x78), Rgba.Parse("#12345678"));
    Equal("#12345678", Rgba.Parse("#12345678").ToString());
    Equal("#ABCDEF", Rgba.Parse("#abcdef").ToString());
    foreach (var bad in new[] { "#12", "123456", "#GGGGGG", "#1234", "", "#12345" })
        True(!Rgba.TryParse(bad, out _));
});

Test("metrics follow toolbar height and density", () =>
{
    var m = ShellMetrics.From(Theme.Default);
    Equal(6.0, m.Unit);
    Equal(30.0, m.ControlHeight);
    Equal(15.0, m.IconSize);
    True(!m.TallCaption);

    var tall = ShellMetrics.From(Theme.Default with { Toolbar = Theme.Default.Toolbar with { Height = 48 }, Density = Density.Compact });
    True(tall.TallCaption);
    Equal(4.0, tall.Unit);
    Equal(36.0, tall.ControlHeight);

    var small = ShellMetrics.From(Theme.Default with { Toolbar = Theme.Default.Toolbar with { Height = 32 } });
    Equal(24.0, small.ControlHeight);
});

Test("pinned sidebar starts open and toggles", () =>
{
    var s = new SidebarState(DockEdge.Left, autoHide: false);
    True(s.Open);
    s.PointerExited();
    True(s.Open && !s.HidePending);
    s.Toggle();
    True(!s.Open);
    s.Toggle();
    True(s.Open);
    s.Dismiss();
    True(s.Open);
});

Test("auto-hide reveals at the edge and hides after the delay", () =>
{
    var s = new SidebarState(DockEdge.Right, autoHide: true);
    True(!s.Open);
    True(s.PointerAtEdge(buttonsPressed: false));
    True(s.Open);
    True(!s.PointerAtEdge(buttonsPressed: false));
    s.PointerEntered();
    s.PointerExited();
    True(s.HidePending);
    s.HideDelayElapsed();
    True(!s.Open && !s.HidePending);
});

Test("re-entering cancels a pending hide", () =>
{
    var s = new SidebarState(DockEdge.Left, autoHide: true);
    s.PointerAtEdge(false);
    s.PointerEntered();
    s.PointerExited();
    s.PointerEntered();
    True(!s.HidePending);
    s.HideDelayElapsed();
    True(s.Open);
});

Test("held mouse button never reveals", () =>
{
    var s = new SidebarState(DockEdge.Bottom, autoHide: true);
    True(!s.PointerAtEdge(buttonsPressed: true));
    True(!s.Open);
});

Test("keyboard focus keeps the sidebar open", () =>
{
    var s = new SidebarState(DockEdge.Left, autoHide: true);
    s.Toggle();
    s.FocusEntered();
    s.PointerEntered();
    s.PointerExited();
    True(!s.HidePending);
    s.FocusExited();
    True(s.HidePending);
    s.HideDelayElapsed();
    True(!s.Open);
});

Test("dismiss closes only in auto-hide", () =>
{
    var s = new SidebarState(DockEdge.Top, autoHide: true);
    s.PointerAtEdge(false);
    s.PointerEntered();
    s.FocusEntered();
    s.Dismiss();
    True(!s.Open);
    True(s.PointerAtEdge(false));
    s.PointerExited();
    True(s.HidePending);
});

Test("switching modes resets visibility", () =>
{
    var s = new SidebarState(DockEdge.Left, autoHide: false);
    s.SetAutoHide(true);
    True(s.AutoHide && !s.Open);
    s.PointerAtEdge(false);
    s.SetAutoHide(false);
    True(!s.AutoHide && s.Open && !s.HidePending);
});

Test("edge hit-testing on all four edges", () =>
{
    const double w = 1000, h = 600, t = 6;
    True(DockEdge.Left.NearEdge(0, 300, w, h, t));
    True(DockEdge.Left.NearEdge(6, 300, w, h, t));
    True(!DockEdge.Left.NearEdge(6.5, 300, w, h, t));
    True(DockEdge.Right.NearEdge(995, 10, w, h, t));
    True(!DockEdge.Right.NearEdge(990, 10, w, h, t));
    True(DockEdge.Top.NearEdge(500, 2, w, h, t));
    True(DockEdge.Bottom.NearEdge(500, 597, w, h, t));
    True(!DockEdge.Bottom.NearEdge(500, 590, w, h, t));
    True(!DockEdge.Left.NearEdge(-1, 300, w, h, t));
    True(!DockEdge.Bottom.NearEdge(500, 601, w, h, t));
    True(DockEdge.Left.IsVertical() && !DockEdge.Top.IsVertical());
});

static void Nav(string input, string expected)
{
    var r = AddressResolver.Resolve(input);
    Equal(AddressKind.Navigate, r.Kind);
    Equal(expected, r.Uri!.AbsoluteUri);
}

static void Search(string input)
{
    var r = AddressResolver.Resolve(input);
    Equal(AddressKind.Search, r.Kind);
    True(r.Uri!.AbsoluteUri.StartsWith("https://duckduckgo.com/?q=", StringComparison.Ordinal));
}

static void Rejected(string input)
{
    var r = AddressResolver.Resolve(input);
    Equal(AddressKind.Rejected, r.Kind);
    True(!string.IsNullOrEmpty(r.Reason));
}

Test("addresses: domains get https", () =>
{
    Nav("example.com", "https://example.com/");
    Nav("Example.com/a/b?c=d#e", "https://example.com/a/b?c=d#e");
    Nav("sub.domain.co.uk:8443/x", "https://sub.domain.co.uk:8443/x");
    var idn = AddressResolver.Resolve("例え.テスト");
    Equal(AddressKind.Navigate, idn.Kind);
    Equal("xn--r8jz45g.xn--zckzah", idn.Uri!.IdnHost);
    Equal("https", idn.Uri.Scheme);
});

Test("addresses: explicit schemes are kept", () =>
{
    Nav("http://example.com", "http://example.com/");
    Nav("HTTPS://example.com/path", "https://example.com/path");
    Nav("about:blank", "about:blank");
});

Test("addresses: local hosts get http", () =>
{
    Nav("localhost", "http://localhost/");
    Nav("localhost:3000", "http://localhost:3000/");
    Nav("192.168.1.1", "http://192.168.1.1/");
    Nav("127.0.0.1:8080/api", "http://127.0.0.1:8080/api");
    Nav("[::1]:5000", "http://[::1]:5000/");
});

Test("addresses: everything else is a search", () =>
{
    Search("hello world");
    Search("foo");
    Search("foo.c");
    Search("user@example.com");
    Search("what is 2+2?");
    Search("1.2.3");
    Equal("https://duckduckgo.com/?q=a%26b%3Dc", AddressResolver.Resolve("a&b=c").Uri!.AbsoluteUri);
});

Test("addresses: non-web schemes are refused", () =>
{
    Rejected("javascript:alert(1)");
    Rejected("file:///C:/Windows/win.ini");
    Rejected(@"C:\Windows\win.ini");
    Rejected("data:text/html,<b>hi</b>");
    Rejected("about:config");
    Rejected("ms-settings:privacy");
    Rejected("https://");
    Equal(AddressKind.Empty, AddressResolver.Resolve("   ").Kind);
    Equal(AddressKind.Empty, AddressResolver.Resolve(null).Kind);
});

Test("url policy", () =>
{
    True(UrlPolicy.IsNavigable("https://example.com/"));
    True(UrlPolicy.IsNavigable("about:blank"));
    True(!UrlPolicy.IsNavigable("data:text/html,hi"));
    True(!UrlPolicy.IsNavigable("javascript:void(0)"));
    True(!UrlPolicy.IsNavigable("file:///C:/x"));
    True(!UrlPolicy.IsNavigable("not a url"));
    True(UrlPolicy.IsInsecure(new Uri("http://example.com/")));
    True(!UrlPolicy.IsInsecure(new Uri("http://localhost:3000/")));
    True(!UrlPolicy.IsInsecure(new Uri("http://127.0.0.1/")));
    True(!UrlPolicy.IsInsecure(new Uri("https://example.com/")));
    Equal("example.com", UrlPolicy.DisplayHost("https://example.com/a"));
});

Test("launch options", () =>
{
    var o = LaunchOptions.Parse(["--theme", "t.json", "example.com", "--user-data", @"C:\tmp\lilt", "--startup-report", "r.json"]);
    Equal("t.json", o.ThemePath);
    Equal("example.com", o.Url);
    Equal(@"C:\tmp\lilt", o.UserDataDir);
    Equal("r.json", o.StartupReportPath);

    var p = LaunchOptions.Parse(["--url", "--theme", "x.json", "--bogus"]);
    Equal(null, p.Url);
    Equal("x.json", p.ThemePath);
    True(!p.InPrivate);

    var q = LaunchOptions.Parse(["--inprivate", "--user-agent", "Mozilla/5.0 test", "--browser-args", "--enable-features=HttpsUpgrades --disable-quic"]);
    True(q.InPrivate);
    Equal("Mozilla/5.0 test", q.UserAgent);
    Equal("--enable-features=HttpsUpgrades --disable-quic", q.BrowserArgs);
    Equal(null, q.Url);
});

Test("pages.colorScheme parses and defaults to theme", () =>
{
    Equal(PageColorScheme.Theme, ThemeParser.Parse("{\"schemaVersion\":1}").Theme.Pages.ColorScheme);
    Equal(PageColorScheme.Dark, ThemeParser.Parse("{\"schemaVersion\":1,\"pages\":{\"colorScheme\":\"dark\"}}").Theme.Pages.ColorScheme);
});

Test("user content: names, limits and grouping", () =>
{
    var b = UserContentScripts.Build([
        new("example.com.css", "body { color: red }"),
        new("all.css", "a { outline: 0 }"),
        new("github.com.js", "window.x = 1;"),
        new("notes.txt", "ignored"),
        new("Bad Name.css", "x"),
        new("huge.example.css", new string('a', UserContentScripts.MaxFileBytes + 1)),
    ]);
    Equal(2, b.Styles);
    Equal(1, b.UserScripts);
    Equal(2, b.Scripts.Count);
    Equal(2, b.Skipped.Count);
    True(b.Scripts[0].Contains("CSSStyleSheet"));
    True(b.Scripts[1].Contains("window.x = 1;"));
    Equal(0, UserContentScripts.Build([]).Scripts.Count);
});

static string? RunInPage(string script, string protocol, string host)
{
    var node = new[] { "/opt/node22/bin/node", "/usr/bin/node", "/usr/local/bin/node" }.FirstOrDefault(File.Exists);
    if (node is null) return null;
    var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".js");
    File.WriteAllText(file, $$"""
        globalThis.location = { protocol: {{JsonSerializer.Serialize(protocol)}}, hostname: {{JsonSerializer.Serialize(host)}} };
        globalThis.CSSStyleSheet = class { replaceSync(t) { this.text = t; } };
        globalThis.document = { adoptedStyleSheets: [], addEventListener() {} };
        globalThis.ran = [];
        {{script}}
        console.log(JSON.stringify({ css: document.adoptedStyleSheets.map(s => s.text), ran: globalThis.ran }));
        """);
    try
    {
        using var p = Process.Start(new ProcessStartInfo(node, file) { RedirectStandardOutput = true, RedirectStandardError = true })!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return output.Trim();
    }
    finally { File.Delete(file); }
}

Test("user content: generated scripts match hosts in a JS engine", () =>
{
    var b = UserContentScripts.Build([
        new("example.com.css", "p{color:red}"),
        new("all.css", "a{x:1}"),
        new("example.com.js", "ran.push('ex');"),
        new("other.org.js", "ran.push('other');"),
        new("broken.example.com.js", "this is not javascript ("),
    ]);
    var all = string.Join("\n;\n", b.Scripts.Select(s => $"try {{ new Function({JsonSerializer.Serialize(s)})(); }} catch (e) {{ ran.push('syntax'); }}"));
    var sub = RunInPage(all, "https:", "www.example.com");
    if (sub is null) { Console.WriteLine("      (node not found; JS checks skipped)"); return; }
    Equal("""{"css":["a{x:1}\np{color:red}"],"ran":["syntax","ex"]}""", sub);
    Equal("""{"css":["a{x:1}"],"ran":["syntax","other"]}""", RunInPage(all, "https:", "other.org"));
    Equal("""{"css":[],"ran":["syntax"]}""", RunInPage(all, "about:", ""));
    Equal("""{"css":["a{x:1}"],"ran":["syntax"]}""", RunInPage(all, "https:", "notexample.com"));
});

Test("glass.json: backdrop object with tint", () =>
{
    var r = ThemeParser.LoadFile(RepoFile($"{themes}/glass.json"));
    Equal(0, r.Issues.Count);
    Equal(Backdrop.Acrylic, r.Theme.Backdrop.Material);
    Equal(Rgba.Parse("#171A1E"), r.Theme.Backdrop.Tint);
    Equal(0.22, r.Theme.Backdrop.TintOpacity);
    True(r.Theme.Pages.Glass);
    Equal("aurora", r.Theme.Home.Background);
    True(r.Theme.Home.Glass);
});

Test("lists: order kept, unknown and repeated ids dropped, empty allowed", () =>
{
    var r = ThemeParser.Parse("""{ "schemaVersion": 1, "toolbar": { "items": ["menu", "address", "back", "rocket", "menu", 3] }, "sidebar": { "sections": [] } }""");
    Equal(new Ids("menu", "address", "back"), r.Theme.Toolbar.Items);
    Equal(new Ids(), r.Theme.Sidebar.Sections);
    Equal(3, r.Issues.Count);
    var bad = ThemeParser.Parse("""{ "schemaVersion": 1, "home": { "items": "logo" } }""");
    Equal(Theme.Default.Home.Items, bad.Theme.Home.Items);
    Equal(1, bad.Issues.Count);
});

Test("home: v0.2 switches still work; backgrounds can't point outside the folder", () =>
{
    var old = ThemeParser.Parse("""{ "schemaVersion": 1, "home": { "clock": false, "search": true, "tiles": true, "glass": false } }""");
    Equal(0, old.Issues.Count);
    Equal(new Ids("search", "tiles"), old.Theme.Home.Items);
    foreach (var ok in new[] { "", "hills", "beach.jpg", "My Photo.PNG", "art.svg" }) True(ThemeParser.IsBackground(ok));
    foreach (var bad in new[] { "../x.jpg", "C:\\x.jpg", "sub/x.png", "x.exe", ".jpg", "x" }) True(!ThemeParser.IsBackground(bad));
    Equal("", ThemeParser.Parse("""{ "schemaVersion": 1, "home": { "background": "..\\..\\secret.png" } }""").Theme.Home.Background);
});

Test("power policy: full while used or audible, reduced when away, paused when unseen", () =>
{
    var on = Theme.Default.Power;
    Equal(PowerLevel.Full, PowerPolicy.Decide(on, focused: true, minimized: false, 999, audible: false, covered: false, energySaver: false));
    Equal(PowerLevel.Full, PowerPolicy.Decide(on, false, false, 3, false, false, false));
    Equal(PowerLevel.Reduced, PowerPolicy.Decide(on, false, false, 3, false, false, energySaver: true));
    Equal(PowerLevel.Reduced, PowerPolicy.Decide(on, false, false, 30, false, false, false));
    Equal(PowerLevel.Paused, PowerPolicy.Decide(on, false, minimized: true, 30, false, false, false));
    Equal(PowerLevel.Paused, PowerPolicy.Decide(on, false, false, 30, false, covered: true, false));
    Equal(PowerLevel.Reduced, PowerPolicy.Decide(on with { HideWhenCovered = false }, false, false, 30, false, true, false));
    Equal(PowerLevel.Full, PowerPolicy.Decide(on, false, true, 30, audible: true, true, false));
    Equal(PowerLevel.Full, PowerPolicy.Decide(on with { Efficiency = false }, false, true, 30, false, true, false));
    True(PowerPolicy.ShouldSuspend(on, 5, false) && !PowerPolicy.ShouldSuspend(on, 4.9, false));
    True(PowerPolicy.ShouldSuspend(on, 1, energySaver: true));
    True(!PowerPolicy.ShouldSuspend(on with { SuspendMinutes = 0 }, 999, true));
});

Test("power policy: hidden behind another window, unless turned off", () =>
{
    var on = Theme.Default.Power;
    Equal(PowerLevel.Hidden, PowerPolicy.Decide(on, false, false, 30, false, covered: false, energySaver: false, occluded: true));
    Equal(PowerLevel.Reduced, PowerPolicy.Decide(on with { HideWhenOccluded = false }, false, false, 30, false, false, false, occluded: true));
    Equal(PowerLevel.Paused, PowerPolicy.Decide(on, false, false, 30, false, covered: true, false, occluded: true));
    True(PowerPolicy.Covers((0, 0, 1920, 1080), (100, 100, 900, 700)));
    True(PowerPolicy.Covers((-8, -8, 1928, 1048), (0, 0, 1920, 1040)));
    True(!PowerPolicy.Covers((0, 0, 1000, 1080), (100, 100, 1500, 700)));
    True(!PowerPolicy.Covers((0, 0, 1920, 1080), (0, 0, 0, 0)));
});

Test("shortcuts: parse, display, codes and conflicts with typing", () =>
{
    True(Gesture.TryParse("ctrl+shift+t", out var g) && g == new Gesture(true, true, false, "T"));
    Equal("Ctrl+Shift+T", g.ToString());
    Equal("KeyT", g.Code);
    Equal((int)'T', g.VirtualKey);
    True(Gesture.TryParse("Ctrl+Shift+,", out var comma) && comma.Code == "Comma" && comma.VirtualKey == 188);
    True(Gesture.TryParse("F6", out var f6) && f6.VirtualKey == 117);
    True(Gesture.TryParse("Alt+Left", out var left) && left.Code == "ArrowLeft" && left.VirtualKey == 37);
    foreach (var bad in new[] { "T", "Shift+K", "Ctrl+", "Hyper+K", "Ctrl+F25", "Ctrl+Pause", "", "Space" }) True(!Gesture.TryParse(bad, out _));
    Equal("newTab", ShortcutMap.Default.Find(true, false, false, 'T'));
    Equal("address", ShortcutMap.Default.Find(false, false, true, 'D'));
    Equal(null, ShortcutMap.Default.Find(false, false, false, 'T'));
    foreach (var (command, keys) in Commands.Defaults) True(keys.All(k => Gesture.TryParse(k, out _)) && Commands.Labels.ContainsKey(command));
});

Test("shortcuts in the theme: override, disable, bad entries skipped", () =>
{
    var r = ThemeParser.Parse("""{ "schemaVersion": 1, "shortcuts": { "palette": "Alt+Space", "split": "", "newTab": ["Ctrl+T", "Ctrl+N", "Z"], "rocket": "Ctrl+R" } }""");
    Equal(new Gesture(false, false, true, "Space"), r.Theme.Shortcuts.For("palette").Single());
    Equal(0, r.Theme.Shortcuts.For("split").Length);
    Equal(2, r.Theme.Shortcuts.For("newTab").Length);
    Equal(ShortcutMap.Default.For("reload").Length, r.Theme.Shortcuts.For("reload").Length);
    Equal(2, r.Issues.Count);
});

Test("media and split settings parse with limits", () =>
{
    var r = ThemeParser.Parse("""{ "schemaVersion": 1, "media": { "bars": 500, "style": "wave", "player": { "show": "always", "x": 2, "width": 280 }, "musicSites": ["Example.COM", "not a host", 5] }, "split": { "orientation": "vertical", "ratio": 0.99 } }""");
    Equal(Theme.Default.Media.Bars, r.Theme.Media.Bars);
    Equal(VisualizerStyle.Wave, r.Theme.Media.Style);
    Equal(WidgetShow.Always, r.Theme.Media.Player.Show);
    Equal(Theme.Default.Media.Player.X, r.Theme.Media.Player.X);
    Equal(280.0, r.Theme.Media.Player.Width);
    Equal(new Ids("example.com"), r.Theme.Media.MusicSites);
    Equal(SplitOrientation.Vertical, r.Theme.Split.Orientation);
    Equal(Theme.Default.Split.Ratio, r.Theme.Split.Ratio);
    Equal(5, r.Issues.Count);
});

Test("spectrum: a 1 kHz tone lights the band around 1 kHz, silence stays dark", () =>
{
    const int rate = 48000;
    var s = new Spectrum(32, rate);
    var tone = new float[4096];
    for (var i = 0; i < tone.Length; i++) tone[i] = 0.5f * MathF.Sin(2 * MathF.PI * 1000 * i / rate);
    s.Add(tone);
    True(s.Update());
    var loudest = Enumerable.Range(0, 32).MaxBy(b => s.Bars[b]);
    // Band b covers 40 * 400^(b/32) .. 40 * 400^((b+1)/32) Hz; 1 kHz falls in band 17.
    True(Math.Abs(loudest - 17) <= 1);
    True(s.Bars[2] < s.Bars[loudest] * 0.5);
    s.Clear();
    s.Add(new float[2048]);
    True(!s.Update());
    var pcm = new byte[4096 * 4];
    for (var i = 0; i < 4096; i++)
    {
        var v = (short)(16000 * MathF.Sin(2 * MathF.PI * 1000 * i / rate));
        pcm[4 * i] = pcm[4 * i + 2] = (byte)v;
        pcm[4 * i + 1] = pcm[4 * i + 3] = (byte)(v >> 8);
    }
    s.AddPcm16(pcm, 2);
    True(s.Update());
    True(Math.Abs(Enumerable.Range(0, 32).MaxBy(b => s.Bars[b]) - 17) <= 1);
});

Test("media info: page reports are checked and clipped", () =>
{
    MediaInfo? Parse(string json) { using var d = JsonDocument.Parse(json); return MediaInfo.Parse(d.RootElement); }
    var m = Parse("""{"t":"media","playing":true,"title":"Song\u0007 A","artist":"Band","art":"https://i.example/a.jpg","video":false,"pos":61.5,"dur":200,"rate":1,"muted":false}""")!;
    True(m.Playing);
    Equal("Song A", m.Title);
    Equal("https://i.example/a.jpg", m.Artwork);
    Equal(61.5, m.Position);
    Equal(null, Parse("""{"t":"media","none":true}"""));
    var bad = Parse("""{"t":"media","playing":"yes","title":5,"art":"javascript:alert(1)","pos":-3,"dur":"x","rate":0}""")!;
    True(!bad.Playing && bad.Title == "" && bad.Artwork == null && bad.Position == 0 && bad.Duration == 0 && bad.Rate == 1);
    Equal(300, Parse("{\"title\":\"" + new string('x', 1000) + "\"}")!.Title.Length);
    Equal("1:01", MediaInfo.Clock(61.4));
    Equal("1:00:00", MediaInfo.Clock(3600));
});

Test("power settings parse with limits", () =>
{
    var r = ThemeParser.Parse("""{ "schemaVersion": 1, "power": { "efficiency": false, "suspendMinutes": 0, "hideWhenCovered": false } }""");
    Equal(new PowerSpec(false, 0, false, true, false), r.Theme.Power);
    Equal(5, ThemeParser.Parse("""{ "schemaVersion": 1, "power": { "suspendMinutes": 9999 } }""").Theme.Power.SuspendMinutes);
});

Test("backdrop object: bad values are ignored, material falls back", () =>
{
    var r = ThemeParser.Parse("""{ "schemaVersion": 1, "backdrop": { "material": "glass", "tint": "blue", "tintOpacity": 2 } }""");
    Equal(new BackdropSpec(Backdrop.Mica), r.Theme.Backdrop);
    Equal(3, r.Issues.Count);
    Equal(Backdrop.Acrylic, ThemeParser.Parse("""{"schemaVersion":1,"backdrop":"acrylic"}""").Theme.Backdrop.Material);
});

Test("theme writer round-trips through the parser", () =>
{
    foreach (var name in new[] { "minimal", "glass", "light" })
    {
        var t = ThemeParser.LoadFile(RepoFile($"{themes}/{name}.json")).Theme;
        var back = ThemeParser.Parse(ThemeWriter.ToJson(t));
        Equal(0, back.Issues.Count);
        Equal(t, back.Theme);
    }
    var tweaked = Theme.Default with
    {
        Backdrop = new BackdropSpec(Backdrop.Acrylic, null, 0.2, null),
        Home = new HomeSpec(new Ids("tiles", "clock"), true, "me.jpg"),
        Toolbar = Theme.Default.Toolbar with { Items = new Ids("menu", "address") },
        Sidebar = Theme.Default.Sidebar with { Sections = new Ids() },
        Power = new PowerSpec(false, 30, true, false, true),
        Media = Theme.Default.Media with { Bars = 48, Style = VisualizerStyle.Mirror, Player = Theme.Default.Media.Player with { X = 0.1, Show = WidgetShow.Always }, MusicSites = new Ids("example.com") },
        Split = new SplitSpec(SplitOrientation.Vertical, 0.3, 0),
        Shortcuts = new ShortcutMap(new Dictionary<string, Gesture[]>(ShortcutMap.Default.Map) { ["palette"] = [], ["split"] = [new(true, false, true, "S"), new(false, false, false, "F9")] }),
    };
    Equal(tweaked, ThemeParser.Parse(ThemeWriter.ToJson(tweaked)).Theme);
});

Test("bookmarks: Chromium JSON with folders", () =>
{
    var json = """
        { "roots": {
            "bookmark_bar": { "children": [
              { "type": "url", "name": "GitHub", "url": "https://github.com/" },
              { "type": "folder", "name": "Dev", "children": [
                { "type": "url", "name": "Docs", "url": "https://learn.microsoft.com/" },
                { "type": "url", "name": "Bad", "url": "javascript:alert(1)" } ] } ] },
            "other": { "children": [ { "type": "url", "name": "News", "url": "https://news.ycombinator.com" },
                                     { "type": "url", "name": "Dup", "url": "https://github.com" } ] },
            "synced": { "children": [] } } }
        """;
    var r = BookmarkImport.Parse(json);
    Equal("Chromium bookmarks", r.Format);
    Equal(3, r.Bookmarks.Count);
    Equal(2, r.Skipped);
    Equal(new Bookmark("GitHub", "https://github.com/", ""), r.Bookmarks[0]);
    Equal("Dev", r.Bookmarks[1].Folder);
    Equal(new Bookmark("News", "https://news.ycombinator.com/", "Other bookmarks"), r.Bookmarks[2]);
});

Test("bookmarks: Netscape HTML from Chrome/Google", () =>
{
    var html = """
        <!DOCTYPE NETSCAPE-Bookmark-file-1>
        <DL><p>
          <DT><H3 ADD_DATE="1" PERSONAL_TOOLBAR_FOLDER="true">Bookmarks bar</H3>
          <DL><p>
            <DT><A HREF="https://example.com/?a=1&amp;b=2" ADD_DATE="1">Example &amp; Co</A>
            <DT><H3>Folder</H3>
            <DL><p>
              <DT><A HREF="https://inner.example.org">Inner</A>
            </DL><p>
          </DL><p>
          <DT><H3>Other bookmarks</H3>
          <DL><p><DT><A HREF="file:///C:/x">Local</A></DL><p>
        </DL><p>
        """;
    var r = BookmarkImport.Parse(html);
    Equal("HTML bookmarks", r.Format);
    Equal(2, r.Bookmarks.Count);
    Equal(1, r.Skipped);
    Equal(new Bookmark("Example & Co", "https://example.com/?a=1&b=2", ""), r.Bookmarks[0]);
    Equal(new Bookmark("Inner", "https://inner.example.org/", "Folder"), r.Bookmarks[1]);
});

Test("bookmarks: CSV with header, quotes, and without header", () =>
{
    var r = BookmarkImport.Parse("Name,URL,Folder\r\n\"Hello, world\",https://a.example.com,Home\r\n\"Say \"\"hi\"\"\",https://b.example.com,\r\n");
    Equal("CSV", r.Format);
    Equal(new Bookmark("Hello, world", "https://a.example.com/", "Home"), r.Bookmarks[0]);
    Equal("Say \"hi\"", r.Bookmarks[1].Title);
    var bare = BookmarkImport.Parse("https://c.example.com,C site\nnot a url,also not\n");
    Equal(1, bare.Bookmarks.Count);
    Equal(new Bookmark("C site", "https://c.example.com/", ""), bare.Bookmarks[0]);
    Equal(1, bare.Skipped);
});

Test("bookmark store: add, dedupe, remove, persist", () =>
{
    var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
    try
    {
        var store = new BookmarkStore(path);
        store.Load();
        True(store.Add("A", "https://Example.com"));
        True(!store.Add("A again", "https://example.com/"));
        True(!store.Add("bad", "javascript:void(0)"));
        True(store.Contains("https://example.com"));
        Equal(new Bookmark("B", "https://b.example/"), store.Merge([new Bookmark("B", "https://b.example"), new Bookmark("A", "https://example.com/")]).Single());
        True(store.Update(store.Items[1], "Bee", "https://b.example/x", "Work"));
        Equal(new Bookmark("Bee", "https://b.example/x", "Work"), store.Items[1]);
        True(!store.Update(store.Items[1], "dup", "https://example.com", ""));
        True(store.Update(store.Items[1], "B", "https://b.example/", ""));
        store.Save();
        var again = new BookmarkStore(path);
        again.Load();
        Equal(2, again.Items.Count);
        True(again.Remove("https://example.com/"));
        Equal(1, again.Items.Count);

        File.WriteAllText(path, "[{\"url\":\"https://c.example\"},{\"title\":\"no url\"},null]");
        again.Load();
        Equal(new Bookmark("https://c.example/", "https://c.example/", ""), again.Items.Single());
        File.WriteAllText(path, "{ broken");
        again.Load();
        Equal(0, again.Items.Count);
        True(File.ReadAllText(path + ".bak") == "{ broken");
    }
    finally
    {
        File.Delete(path);
        File.Delete(path + ".bak");
    }
});

Test("session: save, load, clamp, empty", () =>
{
    var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
    try
    {
        new Session([new("https://a.example/", "A"), new("https://b.example/", "B")], 5).Save(path);
        var s = Session.Load(path)!;
        Equal(2, s.Tabs.Count);
        Equal(1, s.Active);
        Equal("B", s.Tabs[1].Title);
        new Session([new("https://a.example/", "A"), new("https://b.example/", "B", true)], 0, 1).Save(path);
        s = Session.Load(path)!;
        Equal(1, s.Split);
        True(s.Tabs[1].Pinned);
        new Session([new("https://a.example/", "A")], 0, 0).Save(path);
        Equal(-1, Session.Load(path)!.Split);
        new Session([], 0).Save(path);
        Equal(null, Session.Load(path));
        File.WriteAllText(path, "{ not json");
        Equal(null, Session.Load(path));
    }
    finally { File.Delete(path); }
});

static byte[] Field(int field, byte[] data)
{
    var o = new List<byte>();
    void Varint(ulong v) { while (v >= 0x80) { o.Add((byte)(v | 0x80)); v >>= 7; } o.Add((byte)v); }
    Varint((ulong)field << 3 | 2);
    Varint((ulong)data.Length);
    o.AddRange(data);
    return [.. o];
}

static byte[] MakeCrx(RSA key, byte[] zip, byte[]? idOverride = null)
{
    var pub = key.ExportSubjectPublicKeyInfo();
    var id = idOverride ?? SHA256.HashData(pub)[..16];
    var signed = Field(1, id);
    var message = "CRX3 SignedData\0"u8.ToArray().Concat(BitConverter.GetBytes((uint)signed.Length)).Concat(signed).Concat(zip).ToArray();
    var sig = key.SignData(message, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    var header = Field(2, [.. Field(1, pub), .. Field(2, sig)]).Concat(Field(10000, signed)).ToArray();
    var crx = new List<byte>("Cr24"u8.ToArray());
    crx.AddRange(BitConverter.GetBytes(3u));
    crx.AddRange(BitConverter.GetBytes((uint)header.Length));
    crx.AddRange(header);
    crx.AddRange(zip);
    return [.. crx];
}

Test("CRX3: valid package verifies; tampering and wrong IDs are rejected", () =>
{
    using var key = RSA.Create(2048);
    var zip = "PK fake zip bytes"u8.ToArray();
    var crx = MakeCrx(key, zip);
    var id = ExtensionId.FromPublicKey(key.ExportSubjectPublicKeyInfo());
    var pkg = Crx3.Verify(crx, id);
    True(pkg.Zip.SequenceEqual(zip));
    True(pkg.PublicKey.SequenceEqual(key.ExportSubjectPublicKeyInfo()));

    static void Throws(Action a, string why) { try { a(); } catch (InvalidDataException) { return; } throw new Exception("expected rejection: " + why); }
    var tampered = (byte[])crx.Clone();
    tampered[^1] ^= 1;
    Throws(() => Crx3.Verify(tampered, id), "tampered zip");
    Throws(() => Crx3.Verify(crx, new string('a', 32)), "wrong expected id");
    using var other = RSA.Create(2048);
    Throws(() => Crx3.Verify(MakeCrx(other, zip, SHA256.HashData(key.ExportSubjectPublicKeyInfo())[..16]), id), "key doesn't match id");
    Throws(() => Crx3.Verify("PK\u0003\u0004"u8.ToArray(), id), "not a crx");
    Throws(() => Crx3.Verify([.. "Cr24"u8, 3, 0, 0, 0, 255, 255, 0, 0], id), "truncated");
});

Test("extension IDs: from key, parse from URLs, store URL", () =>
{
    Equal("pnpnpkpkbopgcebjepaopplahkdnmbfc", ExtensionId.FromPublicKey(Convert.FromBase64String(File.ReadAllText(RepoFile("src/Lilt.App/Extensions/ubol-key.txt")).Trim())));
    Equal("ghmbeldphafepmbegfdlkpapadhbakde", ExtensionId.Parse("https://chromewebstore.google.com/detail/proton-pass-free-password/ghmbeldphafepmbegfdlkpapadhbakde?hl=en"));
    Equal("ghmbeldphafepmbegfdlkpapadhbakde", ExtensionId.Parse("  ghmbeldphafepmbegfdlkpapadhbakde "));
    Equal(null, ExtensionId.Parse("proton pass"));
    Equal(null, ExtensionId.Parse(null));
    True(ExtensionId.WebStoreDownload("ghmbeldphafepmbegfdlkpapadhbakde", "153.0.4234.48").AbsoluteUri.Contains("prodversion=153.0"));
});

Test("addresses: extension pages open from the address bar, not other schemes", () =>
{
    Nav("chrome-extension://pnpnpkpkbopgcebjepaopplahkdnmbfc/dashboard.html", "chrome-extension://pnpnpkpkbopgcebjepaopplahkdnmbfc/dashboard.html");
    Rejected("chrome://settings");
    True(!UrlPolicy.IsNavigable("chrome-extension://pnpnpkpkbopgcebjepaopplahkdnmbfc/popup.html"));
    var o = LaunchOptions.Parse(["--install-extension", "ghmbeldphafepmbegfdlkpapadhbakde", "--no-extensions"]);
    Equal("ghmbeldphafepmbegfdlkpapadhbakde", o.InstallExtension);
    True(o.NoExtensions);
    Equal("split; wait 200", LaunchOptions.Parse(["--run", "split; wait 200", "x.com"]).Run);
});

Test("password exports never become bookmarks", () =>
{
    var chrome = BookmarkImport.Parse("name,url,username,password,note\naccounts.google.com,https://accounts.google.com/signin,me,hunter2,\n,https://x.example/login,me2,pw,\n");
    True(chrome.Passwords);
    Equal(0, chrome.Bookmarks.Count);
    Equal("passwords", chrome.Format);
    Equal(new Bookmark("accounts.google.com", "https://accounts.google.com/signin"), chrome.PasswordSites![0]);
    Equal(new Bookmark("https://x.example/login", "https://x.example/login"), chrome.PasswordSites[1]);
    True(chrome.PasswordSites.All(b => !b.Title.Contains("hunter2") && !b.Url.Contains("hunter2")));
    var bitwarden = BookmarkImport.Parse("folder,favorite,type,name,notes,fields,reprompt,login_uri,login_username,login_password,login_totp\n,,login,Site,,,0,https://site.example,u,p,\n");
    True(bitwarden.Passwords);
    Equal("https://site.example/", bitwarden.PasswordSites!.Single().Url);
    var firefox = BookmarkImport.Parse("\"url\",\"username\",\"password\",\"httpRealm\"\n\"https://ff.example\",\"u\",\"p\",\"\"\n");
    True(firefox.Passwords);
    True(!BookmarkImport.Parse("title,url\nA,https://a.example\n").Passwords);
});

Test("search engines: names, custom templates, rejects", () =>
{
    Equal("Google", SearchEngines.For("google").Name);
    Equal("https://www.google.com/search?q=a%20b", AddressResolver.Resolve("a b", SearchEngines.For("Google").Template).Uri!.AbsoluteUri);
    var custom = SearchEngines.Resolve("https://search.example.org/find?{x}&q=%s")!.Value;
    Equal("search.example.org", custom.Name);
    Equal("https://search.example.org/find?%7Bx%7D&q=cats", AddressResolver.Resolve("cats", custom.Template).Uri!.AbsoluteUri);
    Equal(null, SearchEngines.Resolve("http://insecure.example/?q=%s"));
    Equal(null, SearchEngines.Resolve("https://no-placeholder.example/"));
    Equal("DuckDuckGo", SearchEngines.For("nonsense").Name);
    Equal("google", ThemeParser.Parse("{\"schemaVersion\":1,\"pages\":{\"search\":\"google\",\"history\":false}}").Theme.Pages.Search);
    var bad = ThemeParser.Parse("{\"schemaVersion\":1,\"pages\":{\"search\":\"altavista\"}}");
    Equal("duckduckgo", bad.Theme.Pages.Search);
    True(bad.Issues.Any(i => i.Path == "pages.search"));
});

Test("history: records, merges fragments, ages, caps, persists", () =>
{
    var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
    try
    {
        var h = new History(path);
        long day = 86400000, now = 100 * day;
        h.Record("https://a.example/page#top", "A", now);
        h.Record("https://a.example/page", "", now);
        h.Record("about:blank", "no", now);
        h.Record("https://newtab.example/", "", now);
        Equal(2, h.Count);
        var a = h.All.First(v => v.Url == "https://a.example/page");
        Equal(2, a.Count);
        Equal("A", a.Title);
        h.Retitle("https://a.example/page#x", "Better");
        Equal("Better", h.All.First(v => v.Url == "https://a.example/page").Title);
        True(History.Score(a with { Last = now - 14 * day }, now) < History.Score(a, now) * 0.51);
        h.Save();
        var again = new History(path);
        again.Load();
        Equal(2, again.Count);
        True(again.Remove("https://a.example/page"));
        Equal(1, again.Count);
        for (var i = 0; i < History.Max + 10; i++) again.Record($"https://n{i}.example/", "", now + i);
        Equal(History.Max, again.Count);
    }
    finally
    {
        File.Delete(path);
    }
});

Test("address suggestions: every word matches, sites rank first, inline completion", () =>
{
    long now = 1000L * 86400000;
    var history = new[]
    {
        new Visit("https://www.youtube.com/", "YouTube", 50, now),
        new Visit("https://www.youtube.com/watch?v=1", "Cats video", 2, now),
        new Visit("https://news.ycombinator.com/", "Hacker News", 10, now),
    };
    var marks = new[] { new Bookmark("Your bank", "https://bank.example/") };
    var tabs = new[] { ("Docs", "https://docs.example/") };
    var r = Suggestions.Find("you", tabs, marks, history, now);
    Equal("https://www.youtube.com/", r[0].Url);
    True(r.Any(x => x.Title == "Cats video"));
    True(r.Any(x => x.Kind == SuggestionKind.Bookmark));
    Equal("Cats video", Suggestions.Find("cats you", tabs, marks, history, now).Single().Title);
    Equal(SuggestionKind.Tab, Suggestions.Find("docs", tabs, [new Bookmark("Docs", "https://docs.example/")], history, now).Single().Kind);
    Equal(0, Suggestions.Find("  ", tabs, marks, history, now).Count);
    Equal("youtube.com", Suggestions.Complete("you", marks, history, now));
    Equal("YOutube.com", Suggestions.Complete("YOu", marks, history, now));
    Equal("news.ycombinator.com", Suggestions.Complete("news", marks, history, now));
    Equal(null, Suggestions.Complete("youtube.com", marks, history, now));
    Equal(null, Suggestions.Complete("you tube", marks, history, now));
});

Test("address bar shows pages without https:// and a lone slash", () =>
{
    Equal("example.com", UrlPolicy.Pretty(new Uri("https://example.com/")));
    Equal("example.com/a/", UrlPolicy.Pretty(new Uri("https://example.com/a/")));
    Equal("http://example.com", UrlPolicy.Pretty(new Uri("http://example.com/")));
    Equal("example.com/?q=1", UrlPolicy.Pretty(new Uri("https://example.com/?q=1")));
});

Test("workspaces: names cleaned, split kept only within one workspace", () =>
{
    var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
    try
    {
        new Session([new("https://a.example/", "A"), new("https://b.example/", "B", false, "Work"), new("https://c.example/", "C", false, "Main")], 0, 1, ["Work", "Play"]).Save(path);
        var s = Session.Load(path)!;
        Equal(-1, s.Split);
        Equal("", s.Tabs[2].Workspace);
        Equal("Work", s.Tabs[1].Workspace);
        True(s.Workspaces!.SequenceEqual(["", "Work", "Play"]));
        Equal("Main", Workspace.Display(""));
        Equal("", Workspace.Clean(" Main "));
    }
    finally
    {
        File.Delete(path);
    }
});

Test("v0.6 settings parse, write back and keep defaults equal", () =>
{
    var json = """
    {"schemaVersion":1,
     "toolbar":{"addressWidth":1200},
     "sidebar":{"tabWidth":160,"closeButtons":false},
     "motion":{"windowDrag":false},
     "pages":{"glass":true,"glassOpacity":0.5,"glassOff":["mail.example.com"],"forceDark":true,"tracking":"strict","gestures":false,"scrollbars":"classic"},
     "home":{"background":"aurora","dim":0.4}}
    """;
    var r = ThemeParser.Parse(json);
    Equal(0, r.Issues.Count);
    var t = r.Theme;
    Equal(1200.0, t.Toolbar.AddressWidth);
    Equal(160.0, t.Sidebar.TabWidth);
    True(!t.Sidebar.CloseButtons && !t.Motion.WindowDrag && t.Pages.Glass && t.Pages.ForceDark && !t.Pages.Gestures);
    Equal(TrackingLevel.Strict, t.Pages.Tracking);
    Equal(Scrollbars.Classic, t.Pages.Scrollbars);
    True(t.Pages.GlassOffSites.Has("mail.example.com"));
    Equal("aurora", t.Home.Background);
    Equal(0.4, t.Home.Dim);
    Equal(t, ThemeParser.Parse(ThemeWriter.ToJson(t)).Theme);
    Equal(Theme.Default, ThemeParser.Parse(ThemeWriter.ToJson(Theme.Default)).Theme);
    True(ThemeParser.Parse("{\"schemaVersion\":1,\"home\":{\"background\":\"lava\"}}").Issues.Count == 1);
});

Test("every bundled preset loads without issues", () =>
{
    foreach (var file in Directory.GetFiles(RepoFile(themes), "*.json").Where(f => !f.EndsWith("schema.json")))
    {
        var r = ThemeParser.LoadFile(file);
        Equal($"{Path.GetFileName(file)}: 0", $"{Path.GetFileName(file)}: {r.Issues.Count}");
    }
    Equal(Rgba.Parse("#000000"), ThemeParser.LoadFile(RepoFile($"{themes}/midnight.json")).Theme.Colors.Background);
});

Test("v0.7: panel, storage and site color settings, panel tabs in the session", () =>
{
    var json = """
    {"schemaVersion":1,
     "toolbar":{"siteColor":0.8,"items":["back","address","panel"]},
     "sidebar":{"siteColor":0},
     "panel":{"edge":"left","width":420,"sections":["visualizer","web"]},
     "storage":{"cacheLimit":200,"forgetAfterDays":30,"keep":["bank.example.com"],"signOut":true}}
    """;
    var r = ThemeParser.Parse(json);
    Equal(0, r.Issues.Count);
    var t = r.Theme;
    Equal(0.8, t.Toolbar.SiteColor);
    Equal(0.0, t.Sidebar.SiteColor);
    Equal(DockEdge.Left, t.Panel.Edge);
    Equal(420.0, t.Panel.Width);
    True(t.Panel.Sections.Items.SequenceEqual(["visualizer", "web"]));
    Equal(200, t.Storage.CacheLimit);
    Equal(30, t.Storage.ForgetAfterDays);
    True(t.Storage.Keep.Has("bank.example.com") && t.Storage.SignOut);
    Equal(t, ThemeParser.Parse(ThemeWriter.ToJson(t)).Theme);
    var top = ThemeParser.Parse("{\"schemaVersion\":1,\"panel\":{\"edge\":\"top\"},\"storage\":{\"cacheLimit\":10}}");
    Equal(DockEdge.Right, top.Theme.Panel.Edge);
    Equal(50, top.Theme.Storage.CacheLimit);
    Equal(1, top.Issues.Count);

    var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
    try
    {
        new Session([new("https://a.example/", "A")], 0, Panel: [new("https://open.spotify.com/", "Spotify"), new("https://www.youtube.com/", "YouTube")], PanelShown: 5, PanelOpen: true).Save(path);
        var s = Session.Load(path)!;
        Equal(2, s.Panel!.Count);
        Equal(1, s.PanelShown);
        True(s.PanelOpen);
        new Session([new("https://a.example/", "A")], 0).Save(path);
        Equal(0, Session.Load(path)!.Panel!.Count);
    }
    finally
    {
        File.Delete(path);
    }
});

Test("storage cleanup: sites, which ones are forgotten, folder sizes", () =>
{
    Equal("spotify.com", SiteData.Site("open.spotify.com"));
    Equal("spotify.com", SiteData.Site(".spotify.com"));
    Equal("bbc.co.uk", SiteData.Site("news.bbc.co.uk"));
    Equal("example.com", SiteData.Site("www.example.com"));
    Equal("github.io", SiteData.Site("me.github.io"));
    Equal("127.0.0.1", SiteData.Site("127.0.0.1"));
    var day = 86400000L;
    var now = 1000 * day;
    var visits = new[] { ("www.youtube.com", now - 2 * day), ("old.example", now - 200 * day), ("accounts.google.com", now - 5 * day) };
    var cookies = new[] { ".youtube.com", "old.example", ".google.com", ".doubleclick.net", ".kept.example" };
    var stale = SiteData.Stale(cookies, visits, ["www.kept.example"], now, 90);
    True(stale.SequenceEqual(["old.example", "doubleclick.net"]));
    // History that doesn't go back far enough (just cleared, or turned off) never forgets sites it hasn't seen.
    True(SiteData.Stale(cookies, [("www.youtube.com", now - day)], [], now, 90).Count == 0);
    True(SiteData.Stale(cookies, visits, [], now, 0).Count == 0);

    var dir = Directory.CreateTempSubdirectory().FullName;
    try
    {
        void Put(string rel, int bytes)
        {
            var f = Path.Combine(dir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(f)!);
            File.WriteAllBytes(f, new byte[bytes]);
        }
        Put("EBWebView/Default/Cache/Cache_Data/data_1", 1000);
        Put("EBWebView/Default/Code Cache/js/a", 500);
        Put("EBWebView/Default/Service Worker/CacheStorage/x/y", 300);
        Put("EBWebView/Default/IndexedDB/https_a.example_0.indexeddb.leveldb/1.log", 200);
        Put("EBWebView/Default/Network/Cookies", 50);
        Put("EBWebView/Default/Preferences", 10);
        var u = StorageUse.Measure(dir);
        Equal(1500L, u.Cache);
        Equal(300L, u.Offline);
        Equal(250L, u.SiteData);
        Equal(2060L, u.Total);
        Equal("2 MB", StorageUse.Size(2 << 20));
    }
    finally
    {
        Directory.Delete(dir, true);
    }
});

Test("site tint keeps the theme's text readable", () =>
{
    var surface = Rgba.Parse("#171A1E");
    var text = Rgba.Parse("#F3F4F1");
    var dark = SiteTint.For(Rgba.Parse("#0F0F0F"), surface, text, 0.45);
    Equal(115, (int)dark.A);
    var white = SiteTint.For(Rgba.Parse("#FFFFFF"), surface, text, 0.9);
    True(white.A < 0.9 * 255 && white.A > 0);
    var mixed = new Rgba((byte)(23 + (255 - 23) * white.A / 255.0), (byte)(26 + (255 - 26) * white.A / 255.0), (byte)(30 + (255 - 30) * white.A / 255.0));
    True(SiteTint.Contrast(mixed, text) >= 4.4);
    Equal(0, (int)SiteTint.For(Rgba.Parse("#FF0000"), surface, text, 0).A);
});

Test("v0.8: wipe keeps only what was chosen and nothing that isn't Lilt's", () =>
{
    var dir = Directory.CreateTempSubdirectory().FullName;
    try
    {
        foreach (var f in new[] { "theme.json", "session.json", "bookmarks.json", "history.json", "glass.json", "notes.txt" }) File.WriteAllText(Path.Combine(dir, f), "{}");
        Directory.CreateDirectory(Path.Combine(dir, "WebView2", "EBWebView", "Default"));
        File.WriteAllText(Path.Combine(dir, "WebView2", "EBWebView", "Default", "Cookies"), "x");
        Directory.CreateDirectory(Path.Combine(dir, "themes"));
        Equal(null, DataWipe.RunIfRequested(dir, ["glass.json"], TimeSpan.FromSeconds(1)));
        DataWipe.Request(dir, ["bookmarks", "nonsense"], 0);
        var left = DataWipe.RunIfRequested(dir, ["glass.json"], TimeSpan.FromSeconds(1))!;
        Equal(0, left.Count);
        True(File.Exists(Path.Combine(dir, "bookmarks.json")) && File.Exists(Path.Combine(dir, "notes.txt")));
        True(!File.Exists(Path.Combine(dir, "session.json")) && !File.Exists(Path.Combine(dir, "theme.json")) && !File.Exists(Path.Combine(dir, "glass.json")));
        True(!Directory.Exists(Path.Combine(dir, "WebView2")) && !Directory.Exists(Path.Combine(dir, "themes")));
        True(!File.Exists(Path.Combine(dir, DataWipe.Marker)));
    }
    finally
    {
        Directory.Delete(dir, true);
    }
});

Test("v0.8: sleep rules and new settings", () =>
{
    var p = Theme.Default.Power;
    True(!PowerPolicy.ShouldSleep(p, 30, 50));
    True(PowerPolicy.ShouldSleep(p, 60, 50));
    True(PowerPolicy.ShouldSleep(p, 6, 90));
    True(!PowerPolicy.ShouldSleep(p, 3, 90));
    True(!PowerPolicy.ShouldSleep(p with { SleepMinutes = 0, SleepWhenLowMemory = false }, 600, 99));
    var t = ThemeParser.Parse("{\"schemaVersion\":1,\"power\":{\"sleepMinutes\":15,\"sleepWhenLowMemory\":false},\"media\":{\"fps\":20},\"pages\":{\"restore\":false}}");
    Equal(0, t.Issues.Count);
    Equal(15, t.Theme.Power.SleepMinutes);
    True(!t.Theme.Power.SleepWhenLowMemory && !t.Theme.Pages.Restore);
    Equal(20, t.Theme.Media.Fps);
    var u = ThemeParser.Parse("{\"schemaVersion\":1,\"pages\":{\"smoothScrolling\":false},\"power\":{\"solidWhenAway\":true}}");
    Equal(0, u.Issues.Count);
    True(!u.Theme.Pages.SmoothScrolling && u.Theme.Power.SolidWhenAway);
    Equal(u.Theme, ThemeParser.Parse(ThemeWriter.ToJson(u.Theme)).Theme);
    Equal(t.Theme, ThemeParser.Parse(ThemeWriter.ToJson(t.Theme)).Theme);
});

return Summary();
