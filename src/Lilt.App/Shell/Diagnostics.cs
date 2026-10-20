using System.Diagnostics;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace Lilt.App.Shell;

// Written once, only when --startup-report is given. Times are milliseconds since the Lilt process started.
sealed class StartupReport(string? path)
{
    readonly DateTime start = Process.GetCurrentProcess().StartTime;
    double? windowShown, environmentReady, engineReady;
    string[] extensions = [];

    public void WindowShown() => windowShown ??= Since();
    public void EnvironmentReady() => environmentReady ??= Since();
    public void EngineReady() => engineReady ??= Since();
    public void Extensions(IEnumerable<string> list) => extensions = list.ToArray();

    public void FirstNavigation(string url, bool succeeded, string status)
    {
        if (path is null) return;
        var first = Since();
        var json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["processToWindowShownMs"] = Round(windowShown),
            ["processToEnvironmentReadyMs"] = Round(environmentReady),
            ["processToWebViewReadyMs"] = Round(engineReady),
            ["processToFirstNavigationMs"] = Round(first),
            ["url"] = url,
            ["firstNavigationSucceeded"] = succeeded,
            ["firstNavigationStatus"] = status,
            ["extensions"] = extensions,
            ["pid"] = Environment.ProcessId,
            ["time"] = DateTimeOffset.Now.ToString("O"),
        }, new JsonSerializerOptions { WriteIndented = true });
        try
        {
            File.WriteAllText(path + ".tmp", json);
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            LiltPaths.LogError(e);
        }
    }

    double Since() => (DateTime.Now - start).TotalMilliseconds;

    static double? Round(double? v) => v is { } x ? Math.Round(x) : null;
}

// On-demand only: no background sampling.
static class MemorySnapshot
{
    public sealed record Result(long WorkingSet, long Private, int Processes);

    public static Result Take(CoreWebView2Environment? environment)
    {
        var pids = new HashSet<int> { Environment.ProcessId };
        if (environment is not null)
            foreach (var info in environment.GetProcessInfos())
                pids.Add(info.ProcessId);
        long ws = 0, priv = 0;
        var count = 0;
        foreach (var pid in pids)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                ws += p.WorkingSet64;
                priv += p.PrivateMemorySize64;
                count++;
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }
        return new(ws, priv, count);
    }

    public static string Format(Result r) =>
        $"{r.WorkingSet / 1048576.0:N0} MB working set · {r.Private / 1048576.0:N0} MB private · {r.Processes} processes";
}
