using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    bool Extensions);

enum NoticeKind { Info, Success, Warning, Error }

sealed record Notice(string Message, NoticeKind Kind, string? ActionLabel = null, Action? Action = null);

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

    // Runs in an isolated world (pages can't see or call it) and reports browser shortcuts pressed inside a page.
    const string KeyScript = """
        (() => {
          const send = globalThis.__liltKey;
          if (typeof send !== 'function' || globalThis.__liltKeyOn) return;
          globalThis.__liltKeyOn = true;
          addEventListener('keydown', e => {
            const k = e.key.length === 1 ? e.key.toLowerCase() : e.key;
            const ctrl = e.ctrlKey && !e.altKey && !e.metaKey;
            let cmd = null;
            if (ctrl && !e.shiftKey && k === 't') cmd = 'newTab';
            else if (ctrl && e.shiftKey && k === 't') cmd = 'reopenTab';
            else if (ctrl && !e.shiftKey && k === 'w') cmd = 'closeTab';
            else if (ctrl && k === 'Tab') cmd = e.shiftKey ? 'prevTab' : 'nextTab';
            else if (ctrl && !e.shiftKey && k >= '1' && k <= '9') cmd = 'tab' + k;
            else if ((ctrl && !e.shiftKey && k === 'l') || (e.altKey && !e.ctrlKey && k === 'd') || (k === 'F6' && !e.ctrlKey && !e.altKey)) cmd = 'address';
            else if (ctrl && e.shiftKey && k === 'b') cmd = 'sidebar';
            else if (ctrl && !e.shiftKey && k === 'd') cmd = 'bookmark';
            if (!cmd) return;
            e.preventDefault();
            e.stopImmediatePropagation();
            send(cmd);
          }, true);
          send('ready');
        })();
        """;

    readonly EngineOptions options;
    readonly Task<CoreWebView2Environment> environment;
    readonly List<CoreWebView2> cores = [];
    readonly List<PopupWindow> popups = [];
    readonly UserContent userContent;
    CoreWebView2PreferredColorScheme colorScheme = CoreWebView2PreferredColorScheme.Auto;

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
    // Diagnostics for the startup report (CI only).
    public event Action<string>? Trace;

    // Set by the main window: open a page-requested window as a tab (returns its not-yet-navigated core), or open a URL in a new tab.
    public Func<Task<CoreWebView2>>? OpenTabForPage { get; set; }
    public Action<string>? OpenUrlInTab { get; set; }

    Task<CoreWebView2Environment> CreateEnvironmentAsync()
    {
        var o = new CoreWebView2EnvironmentOptions
        {
            AdditionalBrowserArguments = options.BrowserArgs ?? "",
            IsCustomCrashReportingEnabled = true,
            AreBrowserExtensionsEnabled = options.Extensions,
            EnableTrackingPrevention = true,
            ScrollBarStyle = CoreWebView2ScrollbarStyle.FluentOverlay,
        };
        return CoreWebView2Environment.CreateWithOptionsAsync("", options.UserDataFolder, o).AsTask();
    }

    public Task WhenEnvironmentReady => environment;

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
        core.Profile.PreferredTrackingPreventionLevel = CoreWebView2TrackingPreventionLevel.Balanced;
        core.SetVirtualHostNameToFolderMapping(NewTabHost, options.NewTabFolder, CoreWebView2HostResourceAccessKind.DenyCors);
        _ = userContent.ApplyAsync(core);
        _ = InstallKeyShimAsync(core);

        core.PermissionRequested += OnPermissionRequested;
        core.LaunchingExternalUriScheme += OnExternalScheme;
        core.NewWindowRequested += OnNewWindowRequested;
        core.DownloadStarting += OnDownloadStarting;
        core.ServerCertificateErrorDetected += OnCertificateError;
    }

    // DevTools Protocol: a binding that exists only in Lilt's isolated world, plus the listener script for that world.
    // Runtime.enable is deliberately never called (sites can detect it), which means a binding registered by world name
    // may not reach new documents; so each document also gets the world, binding and script directly at DOMContentLoaded.
    async Task InstallKeyShimAsync(CoreWebView2 core)
    {
        try
        {
            core.GetDevToolsProtocolEventReceiver("Runtime.bindingCalled").DevToolsProtocolEventReceived += (sender, e) =>
            {
                using var doc = JsonDocument.Parse(e.ParameterObjectAsJson);
                var root = doc.RootElement;
                if (root.GetProperty("name").GetString() == "__liltKey" && root.GetProperty("payload").GetString() is { Length: <= 16 } command)
                    KeyCommand?.Invoke(sender, command);
            };
            core.DOMContentLoaded += (sender, e) => _ = AddKeyShimToDocumentAsync(sender);
            await core.CallDevToolsProtocolMethodAsync("Runtime.addBinding", """{"name":"__liltKey","executionContextName":"lilt"}""");
            await core.CallDevToolsProtocolMethodAsync("Page.addScriptToEvaluateOnNewDocument", JsonSerializer.Serialize(new { source = KeyScript, worldName = "lilt" }));
        }
        catch (Exception e)
        {
            LiltPaths.LogError(e);
        }
    }

    async Task AddKeyShimToDocumentAsync(CoreWebView2 core)
    {
        try
        {
            using var tree = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Page.getFrameTree", "{}"));
            var frameId = tree.RootElement.GetProperty("frameTree").GetProperty("frame").GetProperty("id").GetString();
            using var world = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Page.createIsolatedWorld", JsonSerializer.Serialize(new { frameId, worldName = "lilt" })));
            var context = world.RootElement.GetProperty("executionContextId").GetInt32();
            await core.CallDevToolsProtocolMethodAsync("Runtime.addBinding", JsonSerializer.Serialize(new { name = "__liltKey", executionContextId = context }));
            var result = await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", JsonSerializer.Serialize(new { expression = KeyScript, contextId = context }));
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

    void OnPermissionRequested(CoreWebView2 sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        if (e.PermissionKind == CoreWebView2PermissionKind.Autoplay) return;
        e.State = CoreWebView2PermissionState.Deny;
        if (e.IsUserInitiated || e.PermissionKind != CoreWebView2PermissionKind.Notifications)
            Noticed?.Invoke(new($"Blocked {Describe(e.PermissionKind)} for {UrlPolicy.DisplayHost(e.Uri)}. Per-site permissions come in a later version.", NoticeKind.Info));
    }

    static string Describe(CoreWebView2PermissionKind kind) => kind switch
    {
        CoreWebView2PermissionKind.Microphone => "microphone access",
        CoreWebView2PermissionKind.Camera => "camera access",
        CoreWebView2PermissionKind.Geolocation => "location access",
        CoreWebView2PermissionKind.Notifications => "notifications",
        CoreWebView2PermissionKind.OtherSensors => "motion sensors",
        CoreWebView2PermissionKind.ClipboardRead => "clipboard reading",
        CoreWebView2PermissionKind.MultipleAutomaticDownloads => "multiple downloads",
        CoreWebView2PermissionKind.FileReadWrite => "file access",
        _ => $"a permission ({kind})",
    };

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
            if (!UrlPolicy.IsNavigable(uri))
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
                e.NewWindow = await openTab();
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
