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
    bool InPrivate,
    string? UserAgent,
    string? BrowserArgs);

enum NoticeKind { Info, Success, Warning, Error }

sealed record Notice(string Message, NoticeKind Kind, string? ActionLabel = null, Action? Action = null);

// One WebView2 environment and profile shared by every view (main window and pop-ups).
// All page-facing policy lives here so pop-ups get exactly the same rules as the main view.
sealed class BrowserEngine
{
    public const string NewTabHost = "newtab.lilt.example";
    public static readonly Uri NewTabUri = new($"https://{NewTabHost}/");

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

    public event Action<Notice>? Noticed;
    public event Action<CoreWebView2DownloadOperation>? DownloadStarted;

    Task<CoreWebView2Environment> CreateEnvironmentAsync()
    {
        var o = new CoreWebView2EnvironmentOptions
        {
            AdditionalBrowserArguments = options.BrowserArgs ?? "",
            IsCustomCrashReportingEnabled = true,
            AreBrowserExtensionsEnabled = false,
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

        core.PermissionRequested += OnPermissionRequested;
        core.LaunchingExternalUriScheme += OnExternalScheme;
        core.NewWindowRequested += OnNewWindowRequested;
        core.DownloadStarting += OnDownloadStarting;
        core.ServerCertificateErrorDetected += OnCertificateError;
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

    // Pop-ups get a real, related WebView2 so window.opener and postMessage keep working (OAuth, 3-D Secure).
    async void OnNewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            e.Handled = true;
            if (!UrlPolicy.IsNavigable(e.Uri))
            {
                Noticed?.Invoke(new("Blocked a pop-up to an address Lilt doesn't open.", NoticeKind.Warning));
                return;
            }
            var popup = new PopupWindow(this, e.WindowFeatures);
            popups.Add(popup);
            popup.Closed += (_, _) => popups.Remove(popup);
            e.NewWindow = await popup.OpenAsync();
        }
        catch (Exception ex)
        {
            LiltPaths.LogError(ex);
            Noticed?.Invoke(new("A pop-up window couldn't be opened.", NoticeKind.Error));
        }
        finally
        {
            deferral.Complete();
        }
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

    public void CloseWindows()
    {
        foreach (var popup in popups.ToArray()) popup.Close();
    }

    void OnUserContentChanged()
    {
        foreach (var core in cores) _ = userContent.ApplyAsync(core);
        var b = userContent.Last;
        var skipped = b.Skipped.Count > 0 ? $" Skipped: {string.Join("; ", b.Skipped)}." : "";
        Noticed?.Invoke(new($"User styles and scripts updated ({b.Styles} CSS, {b.UserScripts} JS).{skipped} Reload to apply.",
            b.Skipped.Count > 0 ? NoticeKind.Warning : NoticeKind.Info, "Reload", () => cores.FirstOrDefault()?.Reload()));
    }
}
