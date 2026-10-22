using System.Diagnostics;

namespace Lilt.Core;

// "Wipe everything and start fresh": the running Lilt only leaves a note (wipe.json) and restarts. The next start,
// before anything opens Lilt's files, waits for the old one to close, then deletes Lilt's own files and folders except
// the groups you chose to keep. Nothing else in the folder is touched (a --user-data folder may hold other things).
public sealed record WipeRequest(IReadOnlyList<string> Keep, int Pid);

public static class DataWipe
{
    public const string Marker = "wipe.json";

    static readonly string[] items =
        ["theme.json", "theme.schema.json", "session.json", "bookmarks.json", "history.json", "themes", "newtab", "user", "Extensions", "WebView2", "logs", "running"];

    // What each "keep" choice spares.
    public static readonly IReadOnlyDictionary<string, string[]> KeepGroups = new Dictionary<string, string[]>
    {
        ["bookmarks"] = ["bookmarks.json"],
        ["look"] = ["theme.json", "themes"],
    };

    public static void Request(string root, IEnumerable<string> keep, int pid) =>
        Files.WriteJson(Path.Combine(root, Marker), new WipeRequest(keep.Where(KeepGroups.ContainsKey).ToList(), pid));

    // null: no wipe was asked for. Otherwise what was left because it stayed in use past `wait` (empty when all went).
    // presets: the bundled theme files Lilt copies into the folder (also its own).
    public static IReadOnlyList<string>? RunIfRequested(string root, IEnumerable<string> presets, TimeSpan wait)
    {
        var marker = Path.Combine(root, Marker);
        if (!File.Exists(marker)) return null;
        var request = Files.ReadJson<WipeRequest>(marker) ?? new WipeRequest([], 0);
        WaitForExit(request.Pid, wait);
        var keep = request.Keep.SelectMany(k => KeepGroups.GetValueOrDefault(k) ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var left = new List<string>();
        var deadline = Environment.TickCount64 + (long)wait.TotalMilliseconds;
        foreach (var name in items.Concat(presets).Distinct(StringComparer.OrdinalIgnoreCase).Where(n => !keep.Contains(n)))
        {
            var path = Path.Combine(root, name);
            // The old engine's processes can hold the profile for a moment after the window closes.
            while (true)
                try
                {
                    if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                    else if (File.Exists(path)) File.Delete(path);
                    break;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    if (Environment.TickCount64 > deadline)
                    {
                        left.Add(name);
                        break;
                    }
                    Thread.Sleep(200);
                }
        }
        File.Delete(marker);
        return left;
    }

    static void WaitForExit(int pid, TimeSpan wait)
    {
        if (pid <= 0 || pid == Environment.ProcessId) return;
        try
        {
            using var old = Process.GetProcessById(pid);
            // Only another Lilt (a reused process id isn't worth waiting for).
            using var self = Process.GetCurrentProcess();
            if (old.ProcessName == self.ProcessName) old.WaitForExit(wait);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
}
