using System.Security.Cryptography;
using System.Text;
using Lilt.App.Browser;
using Lilt.Core.Diagnostics;
using Lilt.Core.Navigation;
using Microsoft.UI.Xaml;

namespace Lilt.App;

// about:logs (Lilt's answer to Firefox's about:crashes and about:support): Lilt's own log, what started and what went
// wrong; the crashes and hangs Windows recorded for Lilt.exe, which catch the ones Lilt couldn't write down itself; and the
// web engine's crash reports. Newest first, with filters and a plain-text copy for bug reports. Built fresh for each visit,
// from memory; its Clear and Open folder buttons work only with this run's token.
public sealed partial class MainWindow
{
    static readonly string LiltVersion = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "?";
    readonly string logsToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    void OpenLogs()
    {
        if (tabs.FirstOrDefault(t => InternalPages.Is(t.Url)) is { } open)
        {
            Activate(open);
            open.Core?.Reload();
        }
        else AddTab(InternalPages.Logs.AbsoluteUri);
    }

    void Logs_Click(object sender, RoutedEventArgs e) => OpenLogs();

    async Task<InternalPage> ServeInternalAsync(Uri uri, string method)
    {
        var path = uri.AbsolutePath.TrimEnd('/');
        if (method == "POST")
        {
            var token = uri.Query.TrimStart('?').Split('&').FirstOrDefault(p => p.StartsWith("t=", StringComparison.Ordinal))?[2..] ?? "";
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(token), Encoding.ASCII.GetBytes(logsToken))) return new(403, "text/plain", "");
            switch (path)
            {
                case "/logs/clear":
                    LiltPaths.Log.Clear();
                    LiltPaths.Log.Write(LogLevel.Info, "lilt", "The log was cleared");
                    break;
                case "/logs/folder":
                    Open("explorer.exe", paths.Logs);
                    break;
                default:
                    return new(404, "text/plain", "");
            }
            return new(204, "text/plain", "");
        }
        if (path != "/logs")
            return new(404, "text/html; charset=utf-8", $"<!doctype html><meta charset=utf-8><title>Not found</title><p>Lilt's own pages are {string.Join(" and ", InternalPages.Names)}.");
        string engineVersion = "starting", dumps = "";
        try
        {
            if (engine.Environment is { } env)
            {
                engineVersion = env.BrowserVersionString;
                dumps = env.FailureReportFolderPath;
            }
        }
        catch (Exception e) when (e is InvalidCastException or NotImplementedException or System.Runtime.InteropServices.COMException) { }
        var entries = await Task.Run(() => CollectLogs(dumps));
        var c = theme.Colors;
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
        var info = new LogsPageInfo(LiltVersion, engineVersion, Environment.OSVersion.Version.ToString(), logsToken, nonce,
            c.Page.Opaque.ToString(), c.SurfaceRaised.Opaque.ToString(), c.Text.Opaque.ToString(), c.TextMuted.Opaque.ToString(), c.Accent.Opaque.ToString(),
            c.Border.ToString(), theme.Base == Lilt.Core.Theming.ThemeBase.Light);
        return new(200, "text/html; charset=utf-8", LogsPage.Render(info, entries, DateTimeOffset.Now), LogsPage.Csp(nonce));
    }

    // Lilt's log, Windows' reports for Lilt.exe and the engine's crash dumps, newest first.
    static List<LogEntry> CollectLogs(string dumpsFolder)
    {
        var all = new List<LogEntry>(LiltPaths.Log.Read());
        foreach (var r in WindowsReports.Find(WindowsReports.Roots(), "Lilt.exe"))
            all.Add(new(r.Time, LogLevel.Crash, "windows", r.Describe(), r.Folder));
        try
        {
            if (dumpsFolder.Length > 0 && Directory.Exists(dumpsFolder))
                foreach (var f in new DirectoryInfo(dumpsFolder).EnumerateFiles("*.dmp", SearchOption.AllDirectories).OrderByDescending(f => f.LastWriteTimeUtc).Take(20))
                    all.Add(new(f.LastWriteTime, LogLevel.Crash, "engine", "The web engine saved a crash report", $"{f.FullName} ({f.Length / 1024} KB)"));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return all.OrderByDescending(e => e.Time).ToList();
    }
}
