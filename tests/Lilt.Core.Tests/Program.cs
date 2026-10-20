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

Test("bundled dusk.json is exactly the built-in default", () =>
{
    var r = ThemeParser.LoadFile(RepoFile($"{themes}/dusk.json"));
    Equal(0, r.Issues.Count);
    Equal(Theme.Default, r.Theme);
});

Test("bundled paper.json loads cleanly as a light theme", () =>
{
    var r = ThemeParser.LoadFile(RepoFile($"{themes}/paper.json"));
    Equal(0, r.Issues.Count);
    Equal(ThemeBase.Light, r.Theme.Base);
    Equal(Backdrop.MicaAlt, r.Theme.Backdrop.Material);
    Equal(DockEdge.Right, r.Theme.Sidebar.Edge);
    True(r.Theme.Sidebar.AutoHide);
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
    True(r.Issues[0].Message.Contains("#C4A7FF"));
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
        var sectionNode = section.Length == 0 ? default : props.GetProperty(section);
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

    var tall = ShellMetrics.From(Theme.Default with { Toolbar = new ToolbarSpec(48), Density = Density.Compact });
    True(tall.TallCaption);
    Equal(4.0, tall.Unit);
    Equal(36.0, tall.ControlHeight);

    var small = ShellMetrics.From(Theme.Default with { Toolbar = new ToolbarSpec(32) });
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

Test("frost.json: backdrop object with tint", () =>
{
    var r = ThemeParser.LoadFile(RepoFile($"{themes}/frost.json"));
    Equal(0, r.Issues.Count);
    Equal(Backdrop.AcrylicThin, r.Theme.Backdrop.Material);
    Equal(Rgba.Parse("#10131A"), r.Theme.Backdrop.Tint);
    Equal(0.15, r.Theme.Backdrop.TintOpacity);
    True(r.Theme.Home.Glass);
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
    foreach (var name in new[] { "dusk", "paper", "frost" })
    {
        var t = ThemeParser.LoadFile(RepoFile($"{themes}/{name}.json")).Theme;
        var back = ThemeParser.Parse(ThemeWriter.ToJson(t));
        Equal(0, back.Issues.Count);
        Equal(t, back.Theme);
    }
    var tweaked = Theme.Default with { Backdrop = new BackdropSpec(Backdrop.Acrylic, null, 0.2, null), Home = new HomeSpec(false, true, false, true) };
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
        Equal(1, store.Merge([new Bookmark("B", "https://b.example"), new Bookmark("A", "https://example.com/")]));
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
});

return Summary();
