namespace Lilt.Core;

// --user-agent, --inprivate and --browser-args exist for the sign-in and HTTPS-flag spikes, so they can be tried without a rebuild.
// --install-extension takes a Chrome Web Store ID or URL; --no-extensions turns extension support off (for comparisons).
// --run "split; wait 500; open example.com; probe" runs commands once the first page is up (tests and power users).
public sealed record LaunchOptions(
    string? Url,
    string? ThemePath,
    string? UserDataDir,
    string? StartupReportPath,
    bool InPrivate = false,
    string? UserAgent = null,
    string? BrowserArgs = null,
    string? InstallExtension = null,
    bool NoExtensions = false,
    string? Run = null)
{
    public static LaunchOptions Parse(IReadOnlyList<string> args)
    {
        string? url = null, theme = null, data = null, report = null, agent = null, browserArgs = null, install = null, run = null;
        bool inPrivate = false, noExtensions = false;
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            string? Next() => i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : null;
            string? Raw() => i + 1 < args.Count ? args[++i] : null;
            switch (a)
            {
                case "--url": url = Next() ?? url; break;
                case "--theme": theme = Next() ?? theme; break;
                case "--user-data": data = Next() ?? data; break;
                case "--startup-report": report = Next() ?? report; break;
                case "--user-agent": agent = Next() ?? agent; break;
                case "--browser-args": browserArgs = Raw() ?? browserArgs; break;
                case "--inprivate": inPrivate = true; break;
                case "--install-extension": install = Next() ?? install; break;
                case "--no-extensions": noExtensions = true; break;
                case "--run": run = Raw() ?? run; break;
                default:
                    if (!a.StartsWith("--", StringComparison.Ordinal)) url ??= a;
                    break;
            }
        }
        return new(url, theme, data, report, inPrivate, agent, browserArgs, install, noExtensions, run);
    }
}
