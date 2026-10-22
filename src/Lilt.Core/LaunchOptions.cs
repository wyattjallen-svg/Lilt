namespace Lilt.Core;

// Developer switches (--user-data, --startup-report, --user-agent, --browser-args, --install-extension, --run) only work when
// LILT_DEV=1 is set (CI sets it). Anything else that launches Lilt, a shortcut or another app, can't change the engine
// or run commands. --inprivate and --no-extensions are safe to use anywhere.
// --run "split; wait 500; open example.com; probe" runs commands once the first page is up.
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
    public static LaunchOptions Parse(IReadOnlyList<string> args, bool dev = false)
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
                case "--inprivate": inPrivate = true; break;
                case "--no-extensions": noExtensions = true; break;
                // Developer switches: outside LILT_DEV their value is skipped.
                case "--user-data" when dev: data = Next() ?? data; break;
                case "--startup-report" when dev: report = Next() ?? report; break;
                case "--user-agent" when dev: agent = Next() ?? agent; break;
                case "--browser-args" when dev: browserArgs = Raw() ?? browserArgs; break;
                case "--install-extension" when dev: install = Next() ?? install; break;
                case "--run" when dev: run = Raw() ?? run; break;
                case "--user-data" or "--startup-report" or "--user-agent" or "--browser-args" or "--install-extension" or "--run":
                    Raw();
                    break;
                default:
                    if (!a.StartsWith("--", StringComparison.Ordinal)) url ??= a;
                    break;
            }
        }
        return new(url, theme, data, report, inPrivate, agent, browserArgs, install, noExtensions, run);
    }
}
