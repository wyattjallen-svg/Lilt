using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lilt.Core;
using Lilt.Core.Extensions;
using Lilt.Core.Navigation;
using Lilt.Core.Theming;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace Lilt.App.Browser;

sealed record EngineOptions(
    string UserDataFolder,
    string NewTabFolder,
    string UserContentFolder,
    string ExtensionsFolder,
    bool InPrivate,
    string? UserAgent,
    string? BrowserArgs,
    bool Extensions,
    bool MediaKeys,
    bool Lean,
    bool ForceDark = false,
    bool ClassicScrollbars = false,
    int CacheLimitMb = 0,
    bool SmoothScrolling = true);

enum NoticeKind { Info, Success, Warning, Error }

sealed record Notice(string Message, NoticeKind Kind, string? ActionLabel = null, Action? Action = null);

// A page of Lilt's own (about:logs), built by the window for one request.
sealed record InternalPage(int Status, string ContentType, string Body, string? Csp = null);

sealed record ExtensionInfo(string Id, string Name, bool Enabled, string? Popup, string? Options, bool Bundled);

// One WebView2 environment and profile shared by every view (tabs and pop-ups).
// All page-facing policy lives here so every view gets exactly the same rules.
sealed class BrowserEngine
{
    public const string NewTabHost = "newtab.lilt.example";
    // Virtual host mapping serves files, not directories, so the page is named explicitly.
    public static readonly Uri NewTabUri = new($"https://{NewTabHost}/index.html");
    // uBlock Origin Lite ships inside Lilt; a fixed manifest key keeps its ID (and its settings) stable across updates.
    public const string UbolId = "pnpnpkpkbopgcebjepaopplahkdnmbfc";

    // Lilt's page helper (Browser/page.js, embedded): runs in an isolated world the page can't see.
    static readonly string pageScript = new StreamReader(typeof(BrowserEngine).Assembly.GetManifestResourceStream("Lilt.App.Browser.page.js")!).ReadToEnd();

    readonly EngineOptions options;
    readonly Task<CoreWebView2Environment> environment;
    readonly List<CoreWebView2> cores = [];
    readonly List<PopupWindow> popups = [];
    readonly UserContent userContent;
    // The isolated world's execution context in each view's current document, for Evaluate.
    readonly Dictionary<CoreWebView2, int> contexts = [];
    CoreWebView2PreferredColorScheme colorScheme = CoreWebView2PreferredColorScheme.Auto;
    CoreWebView2TrackingPreventionLevel tracking = CoreWebView2TrackingPreventionLevel.Balanced;
    string keysJson = "[]";
    // Page settings for the page helper (see Configure).
    bool peekEnabled = true, gesturesEnabled = true;
    double glassAlpha;
    string[] glassOff = [];
    string fontFamily = "";

    public BrowserEngine(EngineOptions options, DispatcherQueue queue)
    {
        this.options = options;
        // Started immediately so environment creation overlaps window creation.
        environment = CreateEnvironmentAsync();
        userContent = new UserContent(options.UserContentFolder, queue);
        userContent.Changed += OnUserContentChanged;
    }

    public CoreWebView2Environment? Environment => environment.IsCompletedSuccessfully ? environment.Result : null;
    public UserContent UserContent => userContent;
    public ThemeBase ThemeBase { get; private set; } = ThemeBase.Dark;
    public bool ExtensionsEnabled => options.Extensions;
    public string ExtensionsFolder => options.ExtensionsFolder;

    public event Action<Notice>? Noticed;
    public event Action<CoreWebView2DownloadOperation>? DownloadStarted;
    public event Action<CoreWebView2, string>? KeyCommand;
    public event Action<CoreWebView2, MediaInfo?>? MediaChanged;
    public event Action<CoreWebView2, string>? PeekRequested;
    public event Action<CoreWebView2, byte[]>? VisualizerData;
    public event Action<CoreWebView2, string>? Gesture;
    // The site's own color ("#rrggbb", or "" for none), for the toolbar's tint.
    public event Action<CoreWebView2, string>? SiteColor;
    // Set by the window: asks you whether a site may use something (camera, location...). Null means you closed the
    // question without answering.
    public Func<string, CoreWebView2PermissionKind, Task<(bool Allow, bool Remember)?>>? AskPermission { get; set; }
    // Diagnostics for the startup report (CI only).
    public event Action<string>? Trace;

    // Set by the main window: open a page-requested window as a tab (returns its not-yet-navigated core), or open a URL in a new tab.
    public Func<bool, Task<CoreWebView2>>? OpenTabForPage { get; set; }
    // The link last Ctrl+clicked or middle-clicked in a page (page helper), so the tab it opens can stay behind.
    (string Url, long At) behindLink;
    public Action<string>? OpenUrlInTab { get; set; }

    // Lilt's own pages (about:logs) are built by the window and served from memory. Only Lilt opens them (the address bar,
    // a restored tab, a notice's button): a page that links or redirects to one is stopped, other sites can't frame or read
    // them, and their buttons need a token only the page has.
    public Func<Uri, string, Task<InternalPage>>? ServeInternal { get; set; }
    readonly HashSet<CoreWebView2> internalAllowed = [];

    // Every navigation Lilt itself starts goes through here, so its own pages are allowed for it.
    public void Navigate(CoreWebView2 core, string url)
    {
        if (InternalPages.Is(url)) internalAllowed.Add(core);
        core.Navigate(url);
    }

    Task<CoreWebView2Environment> CreateEnvironmentAsync()
    {
        var o = new CoreWebView2EnvironmentOptions
        {
            AdditionalBrowserArguments = BrowserArguments(options),
            IsCustomCrashReportingEnabled = true,
            AreBrowserExtensionsEnabled = options.Extensions,
            EnableTrackingPrevention = true,
            ScrollBarStyle = options.ClassicScrollbars ? CoreWebView2ScrollbarStyle.Default : CoreWebView2ScrollbarStyle.FluentOverlay,
        };
        return CoreWebView2Environment.CreateWithOptionsAsync("", options.UserDataFolder, o).AsTask();
    }

    public Task WhenEnvironmentReady => environment;

    // Lilt's own Chromium switches, merged with any --browser-args (features into one --enable-features switch).
    static string BrowserArguments(EngineOptions o)
    {
        var features = new List<string>();
        var args = new List<string>();
        if (o.MediaKeys) features.Add("HardwareMediaKeyHandling");
        if (o.Lean) args.AddRange(["--enable-low-end-device-mode", "--process-per-site"]);
        // Chromium's automatic dark mode for sites that have no dark look of their own.
        if (o.ForceDark) features.Add("WebContentsForceDark");
        // storage.cacheLimit: Chromium's cap on its cache of downloaded files.
        if (o.CacheLimitMb > 0) args.Add($"--disk-cache-size={o.CacheLimitMb * 1048576L}");
        if (!o.SmoothScrolling) args.Add("--disable-smooth-scrolling");
        foreach (var part in (o.BrowserArgs ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.StartsWith("--enable-features=", StringComparison.Ordinal)) features.AddRange(part["--enable-features=".Length..].Split(',', StringSplitOptions.RemoveEmptyEntries));
            else args.Add(part);
        }
        if (features.Count > 0) args.Add("--enable-features=" + string.Join(',', features.Distinct()));
        return string.Join(' ', args);
    }

    public async Task<CoreWebView2> AttachAsync(WebView2 view)
    {
        var env = await environment;
        var controllerOptions = env.CreateCoreWebView2ControllerOptions();
        controllerOptions.ProfileName = "Default";
        controllerOptions.IsInPrivateModeEnabled = options.InPrivate;
        await view.EnsureCoreWebView2Async(env, controllerOptions);
        var core = view.CoreWebView2;
        Configure(core);
        cores.Add(core);
        return core;
    }

    public void Detach(CoreWebView2? core)
    {
        if (core is null) return;
        cores.Remove(core);
        contexts.Remove(core);
        internalAllowed.Remove(core);
        userContent.Forget(core);
    }

    void Configure(CoreWebView2 core)
    {
        var s = core.Settings;
        s.AreHostObjectsAllowed = false;
        s.IsWebMessageEnabled = false;
        s.IsPasswordAutosaveEnabled = true;
        s.IsGeneralAutofillEnabled = false;
        s.IsReputationCheckingRequired = true;
        s.AreDevToolsEnabled = true;
        s.IsStatusBarEnabled = true;
        if (!string.IsNullOrWhiteSpace(options.UserAgent)) s.UserAgent = options.UserAgent;

        core.Profile.PreferredColorScheme = colorScheme;
        core.Profile.PreferredTrackingPreventionLevel = tracking;
        core.SetVirtualHostNameToFolderMapping(NewTabHost, options.NewTabFolder, CoreWebView2HostResourceAccessKind.DenyCors);
        _ = userContent.ApplyAsync(core);
        InstallPageHelper(core);

        core.AddWebResourceRequestedFilter($"https://{InternalPages.Host}/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnInternalRequest;
        core.NavigationStarting += OnNavigationStarting;
        core.PermissionRequested += OnPermissionRequested;
        core.LaunchingExternalUriScheme += OnExternalScheme;
        core.NewWindowRequested += OnNewWindowRequested;
        core.DownloadStarting += OnDownloadStarting;
        core.ServerCertificateErrorDetected += OnCertificateError;
    }

    // DevTools Protocol, per document: an isolated world, a binding that exists only there, and page.js in it.
    // Runtime.enable is deliberately never called (sites can detect it), so name-based bindings wouldn't reach new
    // documents; the world and binding are created directly for each document at DOMContentLoaded.
    void InstallPageHelper(CoreWebView2 core)
    {
        // The captured core, not the event's sender: the sender can be a different projection of the same view.
        core.GetDevToolsProtocolEventReceiver("Runtime.bindingCalled").DevToolsProtocolEventReceived += (_, e) =>
        {
            try
            {
                using var doc = JsonDocument.Parse(e.ParameterObjectAsJson);
                var root = doc.RootElement;
                if (root.GetProperty("name").GetString() != "__liltKey" || root.GetProperty("payload").GetString() is not { Length: > 0 and <= 16384 } payload) return;
                OnPageMessage(core, payload);
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { }
        };
        core.DOMContentLoaded += (sender, e) => _ = AddPageHelperAsync(core);
        // Single-page sites change content without a new document; see-through pages look again.
        core.SourceChanged += (sender, e) =>
        {
            if (!e.IsNewDocument) _ = EvaluateAsync(core, glassAlpha > 0 ? "__lilt.reglass(), __lilt.recolor()" : "__lilt.recolor()");
        };
    }

    void OnPageMessage(CoreWebView2 core, string payload)
    {
        if (payload == "ready")
        {
            Trace?.Invoke("shim ready");
            return;
        }
        if (payload[0] == 'v')
        {
            // Visualizer levels: "v12,200,..." (0-255 per bar).
            var parts = payload[1..].Split(',');
            if (parts.Length > 96) return;
            var levels = new byte[parts.Length];
            for (var i = 0; i < parts.Length; i++) levels[i] = byte.TryParse(parts[i], out var b) ? b : (byte)0;
            VisualizerData?.Invoke(core, levels);
            return;
        }
        using var doc = JsonDocument.Parse(payload);
        var m = doc.RootElement;
        switch (m.TryGetProperty("t", out var t) ? t.GetString() : null)
        {
            case "key" when m.TryGetProperty("c", out var c) && c.GetString() is { Length: <= 32 } command:
                KeyCommand?.Invoke(core, command);
                break;
            case "media":
                MediaChanged?.Invoke(core, MediaInfo.Parse(m));
                break;
            case "peek" when m.TryGetProperty("url", out var u) && UrlPolicy.IsNavigable(u.GetString()):
                PeekRequested?.Invoke(core, u.GetString()!);
                break;
            case "gesture" when m.TryGetProperty("g", out var g) && g.GetString() is { Length: > 0 and <= 4 } path:
                Gesture?.Invoke(core, path);
                break;
            case "color" when m.TryGetProperty("c", out var sc) && sc.GetString() is { Length: 0 or 7 } color:
                SiteColor?.Invoke(core, color);
                break;
            case "behind" when m.TryGetProperty("url", out var b) && b.GetString() is { } link:
                behindLink = (link, System.Environment.TickCount64);
                break;
        }
    }

    async Task AddPageHelperAsync(CoreWebView2 core)
    {
        try
        {
            using var tree = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Page.getFrameTree", "{}"));
            var frameId = tree.RootElement.GetProperty("frameTree").GetProperty("frame").GetProperty("id").GetString();
            using var world = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Page.createIsolatedWorld", JsonSerializer.Serialize(new { frameId, worldName = "lilt" })));
            var context = world.RootElement.GetProperty("executionContextId").GetInt32();
            await core.CallDevToolsProtocolMethodAsync("Runtime.addBinding", JsonSerializer.Serialize(new { name = "__liltKey", executionContextId = context }));
            var script = pageScript.Replace("__KEYS__", keysJson);
            var result = await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", JsonSerializer.Serialize(new { expression = script, contextId = context }));
            contexts[core] = context;
            await EvaluateAsync(core, ConfigureScript(core));
            Trace?.Invoke($"shim context {context}: {result}");
        }
        catch (Exception e) when (e.Message.Contains("-3260", StringComparison.Ordinal))
        {
            // Protocol errors (a method or parameter Chromium no longer accepts) are bugs worth logging.
            LiltPaths.LogError(e);
        }
        catch (Exception e)
        {
            // The document went away mid-setup (navigation, closed tab); the next one gets its own attempt.
            Trace?.Invoke($"shim failed: {e.Message}");
        }
    }

    // Runs an expression in the page helper's world and returns its value as text (null if the document has no helper yet).
    // userGesture lets it do what pages may only do on a click, like picture in picture.
    public async Task<string?> EvaluateAsync(CoreWebView2 core, string expression, bool userGesture = false)
    {
        if (!contexts.TryGetValue(core, out var context)) return null;
        try
        {
            var json = await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate",
                JsonSerializer.Serialize(new { expression, contextId = context, userGesture, awaitPromise = true, returnByValue = true }));
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("exceptionDetails", out var ex))
                return "error: " + (ex.TryGetProperty("exception", out var x) && x.TryGetProperty("description", out var d) ? d.GetString() : "exception");
            return !root.GetProperty("result").TryGetProperty("value", out var v) ? null : v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText();
        }
        catch (Exception)
        {
            return null;
        }
    }

    // Shortcut definitions for page.js; sent to every open page when they change.
    public void SetShortcuts(ShortcutMap map)
    {
        var list = new List<object>();
        foreach (var (command, gestures) in map.Map)
            foreach (var g in gestures)
                list.Add(new { c = command, ctrl = g.Ctrl, shift = g.Shift, alt = g.Alt, code = g.Code, key = g.Key.Length == 1 && char.IsAsciiLetter(g.Key[0]) ? g.Key : "", r = Commands.Reserved.Contains(command) });
        for (var n = 1; n <= 9; n++)
            list.Add(new { c = "tab" + n, ctrl = true, shift = false, alt = false, code = "Digit" + n, key = "", r = true });
        var json = JsonSerializer.Serialize(list);
        if (json == keysJson) return;
        keysJson = json;
        foreach (var core in contexts.Keys.ToArray()) _ = EvaluateAsync(core, $"__lilt.setKeys({json})");
    }

    // Peek, gestures and see-through pages (glass: 0 for off, else how much of a page's color stays; glassOff lists
    // sites left solid). Sent to every open page when anything changes.
    public void Configure(bool peek, bool gestures, double glass, IEnumerable<string> off, string font = "")
    {
        var offList = off.ToArray();
        if (peek == peekEnabled && gestures == gesturesEnabled && glass == glassAlpha && font == fontFamily && offList.SequenceEqual(glassOff)) return;
        (peekEnabled, gesturesEnabled, glassAlpha, glassOff, fontFamily) = (peek, gestures, glass, offList, font);
        foreach (var core in contexts.Keys.ToArray()) _ = EvaluateAsync(core, ConfigureScript(core));
    }

    public bool GlassFor(string? url) =>
        glassAlpha > 0 && Uri.TryCreate(url, UriKind.Absolute, out var u) && UrlPolicy.IsWeb(u) && u.Host != NewTabHost
        && !glassOff.Any(h => u.Host == h || u.Host.EndsWith("." + h, StringComparison.Ordinal));

    string ConfigureScript(CoreWebView2 core) => string.Create(System.Globalization.CultureInfo.InvariantCulture,
        $"__lilt.configure({{ peek: {(peekEnabled ? "true" : "false")}, gestures: {(gesturesEnabled ? "true" : "false")}, glass: {(GlassFor(core.Source) ? glassAlpha : 0)}, font: {JsonSerializer.Serialize(fontFamily)} }})");

    public void SetTracking(TrackingLevel level)
    {
        tracking = level switch
        {
            TrackingLevel.Off => CoreWebView2TrackingPreventionLevel.None,
            TrackingLevel.Basic => CoreWebView2TrackingPreventionLevel.Basic,
            TrackingLevel.Strict => CoreWebView2TrackingPreventionLevel.Strict,
            _ => CoreWebView2TrackingPreventionLevel.Balanced,
        };
        foreach (var core in cores) core.Profile.PreferredTrackingPreventionLevel = tracking;
    }

    public void SetPageColorScheme(PageColorScheme scheme, ThemeBase themeBase)
    {
        ThemeBase = themeBase;
        colorScheme = scheme switch
        {
            PageColorScheme.Light => CoreWebView2PreferredColorScheme.Light,
            PageColorScheme.Dark => CoreWebView2PreferredColorScheme.Dark,
            PageColorScheme.Auto => CoreWebView2PreferredColorScheme.Auto,
            _ => themeBase == ThemeBase.Light ? CoreWebView2PreferredColorScheme.Light : CoreWebView2PreferredColorScheme.Dark,
        };
        foreach (var core in cores) core.Profile.PreferredColorScheme = colorScheme;
    }

    // You decide: Lilt asks (Allow / Block, remembered for the site unless you untick it). Notifications a site asks
    // for on its own, without a click, are refused quietly; that's how most notification spam starts.
    async void OnPermissionRequested(CoreWebView2 sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        if (e.PermissionKind == CoreWebView2PermissionKind.Autoplay) return;
        if (AskPermission is not { } ask || (!e.IsUserInitiated && e.PermissionKind == CoreWebView2PermissionKind.Notifications))
        {
            e.SavesInProfile = false;
            e.State = CoreWebView2PermissionState.Deny;
            return;
        }
        var deferral = e.GetDeferral();
        try
        {
            var answer = await ask(UrlPolicy.DisplayHost(e.Uri), e.PermissionKind);
            e.SavesInProfile = answer is { Remember: true };
            e.State = answer is { Allow: true } ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
        }
        catch (Exception ex)
        {
            LiltPaths.LogError(ex);
            e.State = CoreWebView2PermissionState.Deny;
        }
        finally
        {
            deferral.Complete();
        }
    }

    public static string Describe(CoreWebView2PermissionKind kind) => kind switch
    {
        CoreWebView2PermissionKind.Microphone => "use your microphone",
        CoreWebView2PermissionKind.Camera => "use your camera",
        CoreWebView2PermissionKind.Geolocation => "know your location",
        CoreWebView2PermissionKind.Notifications => "show notifications",
        CoreWebView2PermissionKind.OtherSensors => "use motion sensors",
        CoreWebView2PermissionKind.ClipboardRead => "read your clipboard",
        CoreWebView2PermissionKind.MultipleAutomaticDownloads => "download several files",
        CoreWebView2PermissionKind.FileReadWrite => "open and save files",
        CoreWebView2PermissionKind.LocalFonts => "see your installed fonts",
        CoreWebView2PermissionKind.WindowManagement => "place windows on your screens",
        CoreWebView2PermissionKind.MidiSystemExclusiveMessages => "control MIDI devices",
        _ => $"use {kind}",
    };

    void OnNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!InternalPages.Is(e.Uri)) return;
        if (internalAllowed.Remove(sender) || InternalPages.Is(sender.Source)
            || e.NavigationKind is CoreWebView2NavigationKind.Reload or CoreWebView2NavigationKind.BackOrForward) return;
        e.Cancel = true;
        Noticed?.Invoke(new("A page tried to open one of Lilt's own pages. Type about:logs in the address bar to see it.", NoticeKind.Warning));
    }

    async void OnInternalRequest(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri) || !InternalPages.Is(uri)) return;
        var deferral = e.GetDeferral();
        try
        {
            var page = ServeInternal is { } serve ? await serve(uri, e.Request.Method) : new InternalPage(404, "text/plain", "Not found");
            var env = await environment;
            var headers = $"Content-Type: {page.ContentType}\nCache-Control: no-store\nX-Frame-Options: DENY\nX-Content-Type-Options: nosniff\nReferrer-Policy: no-referrer"
                + (page.Csp is { } csp ? $"\nContent-Security-Policy: {csp}" : "");
            var reason = page.Status switch { 200 => "OK", 204 => "No Content", 403 => "Forbidden", _ => "Not Found" };
            e.Response = env.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(page.Body)).AsRandomAccessStream(), page.Status, reason, headers);
        }
        catch (Exception ex)
        {
            LiltPaths.LogError(ex);
        }
        finally
        {
            deferral.Complete();
        }
    }

    void OnExternalScheme(CoreWebView2 sender, CoreWebView2LaunchingExternalUriSchemeEventArgs e)
    {
        e.Cancel = true;
        var scheme = Uri.TryCreate(e.Uri, UriKind.Absolute, out var u) ? u.Scheme : "external";
        Noticed?.Invoke(new($"Blocked a {scheme}: link that would open another app.", NoticeKind.Warning));
    }

    // Page-opened windows keep their link to the opener (OAuth, 3-D Secure): sized window.open calls become pop-up windows,
    // plain new-window links become tabs. Pop-ups the user didn't click for are blocked.
    async void OnNewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            e.Handled = true;
            var uri = e.Uri;
            if (!UrlPolicy.IsNavigable(uri) || InternalPages.Is(uri))
            {
                Noticed?.Invoke(new("Blocked a pop-up to an address Lilt doesn't open.", NoticeKind.Warning));
                return;
            }
            if (!e.IsUserInitiated)
            {
                Noticed?.Invoke(new($"Blocked a pop-up from {UrlPolicy.DisplayHost(sender.Source)}.", NoticeKind.Info, "Open it in a tab", () => OpenUrlInTab?.Invoke(uri)));
                return;
            }
            var features = e.WindowFeatures;
            if (OpenTabForPage is { } openTab && features is not { HasSize: true } and not { HasPosition: true })
            {
                var behind = behindLink.Url == uri && System.Environment.TickCount64 - behindLink.At < 2000;
                behindLink = default;
                e.NewWindow = await openTab(behind);
                return;
            }
            var popup = Track(new PopupWindow(this, features));
            e.NewWindow = await popup.OpenAsync();
        }
        catch (Exception ex)
        {
            LiltPaths.LogError(ex);
            Noticed?.Invoke(new("A new window couldn't be opened.", NoticeKind.Error));
        }
        finally
        {
            deferral.Complete();
        }
    }

    public async Task OpenPopupPageAsync(Uri page)
    {
        var popup = Track(new PopupWindow(this, null, 400, 600));
        var core = await popup.OpenAsync();
        core.Navigate(page.AbsoluteUri);
    }

    PopupWindow Track(PopupWindow popup)
    {
        popups.Add(popup);
        popup.Closed += (_, _) => popups.Remove(popup);
        return popup;
    }

    public void CloseWindows()
    {
        foreach (var popup in popups.ToArray()) popup.Close();
    }

    void OnDownloadStarting(CoreWebView2 sender, CoreWebView2DownloadStartingEventArgs e)
    {
        e.Handled = true;
        DownloadStarted?.Invoke(e.DownloadOperation);
    }

    void OnCertificateError(CoreWebView2 sender, CoreWebView2ServerCertificateErrorDetectedEventArgs e)
    {
        e.Action = CoreWebView2ServerCertificateErrorAction.Cancel;
        Noticed?.Invoke(new($"Blocked {UrlPolicy.DisplayHost(e.RequestUri)}: its security certificate isn't valid ({e.ErrorStatus}). Lilt doesn't allow exceptions.", NoticeKind.Error));
    }

    void OnUserContentChanged()
    {
        foreach (var core in cores) _ = userContent.ApplyAsync(core);
        var b = userContent.Last;
        var skipped = b.Skipped.Count > 0 ? $" Skipped: {string.Join("; ", b.Skipped)}." : "";
        Noticed?.Invoke(new($"User styles and scripts updated ({b.Styles} CSS, {b.UserScripts} JS).{skipped} Reload to apply.",
            b.Skipped.Count > 0 ? NoticeKind.Warning : NoticeKind.Info, "Reload", () => cores.FirstOrDefault()?.Reload()));
    }

    // ---- Extensions ----
    // Each extension lives in Extensions\<id>\<version>\ under Lilt's data folder; the manifest gets a "key" so the
    // unpacked install keeps the same ID it has in the Chrome Web Store (some extensions, like password managers, rely on it).

    public async Task EnsureBundledExtensionsAsync(CoreWebView2Profile profile)
    {
        if (!options.Extensions) return;
        var bundled = Path.Combine(AppContext.BaseDirectory, "Extensions", "uBOLite");
        var keyFile = Path.Combine(AppContext.BaseDirectory, "Extensions", "ubol-key.txt");
        if (!File.Exists(Path.Combine(bundled, "manifest.json")) || !File.Exists(keyFile)) return;
        var version = ManifestVersion(Path.Combine(bundled, "manifest.json"));
        var marker = Path.Combine(options.ExtensionsFolder, UbolId, "installed.txt");
        var installed = await profile.GetBrowserExtensionsAsync();
        var existing = installed.FirstOrDefault(x => x.Id == UbolId);
        if (existing is not null && File.Exists(marker) && File.ReadAllText(marker).Trim() == version) return;

        var target = Path.Combine(options.ExtensionsFolder, UbolId, version);
        if (!Directory.Exists(target))
            await Task.Run(() =>
            {
                var staging = target + ".tmp";
                if (Directory.Exists(staging)) Directory.Delete(staging, true);
                CopyDirectory(bundled, staging);
                SetManifestKey(Path.Combine(staging, "manifest.json"), File.ReadAllText(keyFile).Trim());
                Directory.Move(staging, target);
            });
        await AddOrReplaceAsync(profile, existing, target);
        File.WriteAllText(marker, version);
        RemoveOtherVersions(UbolId, version);
    }

    public async Task<string> InstallFromWebStoreAsync(CoreWebView2Profile profile, string input)
    {
        if (!options.Extensions) throw new InvalidOperationException("Extensions are turned off (--no-extensions).");
        var id = ExtensionId.Parse(input) ?? throw new InvalidDataException("That isn't a Chrome Web Store link or extension ID.");
        var env = await environment;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/{env.BrowserVersionString} Lilt");
        using var response = await http.GetAsync(ExtensionId.WebStoreDownload(id, env.BrowserVersionString), HttpCompletionOption.ResponseHeadersRead);
        if (response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps) throw new InvalidDataException("The download wasn't served over HTTPS.");
        if (!response.IsSuccessStatusCode) throw new InvalidDataException($"The Chrome Web Store answered {(int)response.StatusCode}; check the link.");
        if (response.Content.Headers.ContentLength > 200 * 1024 * 1024) throw new InvalidDataException("The package is unexpectedly large.");
        var crx = await response.Content.ReadAsByteArrayAsync();
        var package = Crx3.Verify(crx, id);

        using var zip = new ZipArchive(new MemoryStream(package.Zip));
        var manifestEntry = zip.GetEntry("manifest.json") ?? throw new InvalidDataException("The package has no manifest.");
        string version;
        using (var reader = new StreamReader(manifestEntry.Open()))
            version = ManifestVersionFromText(reader.ReadToEnd());
        var target = Path.Combine(options.ExtensionsFolder, id, version);
        var staging = target + ".tmp";
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        zip.ExtractToDirectory(staging);
        SetManifestKey(Path.Combine(staging, "manifest.json"), Convert.ToBase64String(package.PublicKey));
        if (Directory.Exists(target)) Directory.Delete(target, true);
        Directory.Move(staging, target);

        var installed = await profile.GetBrowserExtensionsAsync();
        var extension = await AddOrReplaceAsync(profile, installed.FirstOrDefault(x => x.Id == id), target);
        RemoveOtherVersions(id, version);
        return extension.Name;
    }

    public async Task<IReadOnlyList<ExtensionInfo>> ListExtensionsAsync(CoreWebView2Profile profile)
    {
        var list = new List<ExtensionInfo>();
        // Only what Lilt installed; WebView2's own component extensions (PDF viewer, clipboard) stay out of the list.
        foreach (var x in await profile.GetBrowserExtensionsAsync())
        {
            if (!Directory.Exists(Path.Combine(options.ExtensionsFolder, x.Id))) continue;
            var (popup, optionsPage) = Pages(x.Id);
            list.Add(new(x.Id, x.Name, x.IsEnabled, popup, optionsPage, x.Id == UbolId));
        }
        return list;
    }

    public static async Task SetEnabledAsync(CoreWebView2Profile profile, string id, bool enabled)
    {
        foreach (var x in await profile.GetBrowserExtensionsAsync())
            if (x.Id == id) await x.EnableAsync(enabled);
    }

    public async Task RemoveAsync(CoreWebView2Profile profile, string id)
    {
        foreach (var x in await profile.GetBrowserExtensionsAsync())
            if (x.Id == id) await x.RemoveAsync();
        try
        {
            var dir = Path.Combine(options.ExtensionsFolder, id);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
        catch (IOException) { }
    }

    static async Task<CoreWebView2BrowserExtension> AddOrReplaceAsync(CoreWebView2Profile profile, CoreWebView2BrowserExtension? existing, string folder)
    {
        try
        {
            return await profile.AddBrowserExtensionAsync(folder);
        }
        catch (Exception) when (existing is not null)
        {
            await existing.RemoveAsync();
            return await profile.AddBrowserExtensionAsync(folder);
        }
    }

    // Popup and options pages from the newest installed copy's manifest.
    (string? Popup, string? Options) Pages(string id)
    {
        try
        {
            var dir = Path.Combine(options.ExtensionsFolder, id);
            var manifest = Directory.Exists(dir)
                ? Directory.GetDirectories(dir).Where(d => !d.EndsWith(".tmp", StringComparison.Ordinal)).Select(d => Path.Combine(d, "manifest.json")).FirstOrDefault(File.Exists)
                : null;
            if (manifest is null) return (null, null);
            var m = JsonNode.Parse(File.ReadAllText(manifest), documentOptions: new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var popup = (string?)m?["action"]?["default_popup"] ?? (string?)m?["browser_action"]?["default_popup"];
            var opts = (string?)m?["options_page"] ?? (string?)m?["options_ui"]?["page"];
            return (popup?.TrimStart('/'), opts?.TrimStart('/'));
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidOperationException)
        {
            return (null, null);
        }
    }

    void RemoveOtherVersions(string id, string keep)
    {
        try
        {
            foreach (var dir in Directory.GetDirectories(Path.Combine(options.ExtensionsFolder, id)))
                if (Path.GetFileName(dir) != keep) Directory.Delete(dir, true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    static string ManifestVersion(string path) => ManifestVersionFromText(File.ReadAllText(path));

    static string ManifestVersionFromText(string text)
    {
        var m = JsonNode.Parse(text, documentOptions: new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var version = (string?)m?["version"] ?? "0";
        return string.Concat(version.Where(c => char.IsAsciiDigit(c) || c == '.'));
    }

    static void SetManifestKey(string path, string key)
    {
        var m = JsonNode.Parse(File.ReadAllText(path), documentOptions: new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })!.AsObject();
        m["key"] = key;
        File.WriteAllText(path, m.ToJsonString());
    }

    static void CopyDirectory(string from, string to)
    {
        foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), overwrite: true);
    }
}
