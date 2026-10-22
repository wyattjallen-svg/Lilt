namespace Lilt.Core.Diagnostics;

// A crash or hang Windows recorded for Lilt. Windows Error Reporting keeps a Report.wer for each one, even when Lilt
// itself had no chance to write anything (a native crash ends the process before any handler runs).
public sealed record WindowsReport(DateTimeOffset Time, bool Hang, string Module, string Code, string Folder)
{
    public string Describe()
    {
        if (Hang) return "Lilt stopped responding and Windows closed it";
        var where = Module.Length > 0 ? $" in {Module}" : "";
        var why = WindowsReports.Explain(Code) is { Length: > 0 } e ? $": {e}" : "";
        return $"Lilt crashed{where}" + (Code.Length > 0 ? $" (code {Code}{why})" : "");
    }
}

public static class WindowsReports
{
    // Report.wer is "Key=Value" lines: EventType, EventTime (a FILETIME) and Sig[n].Name/Sig[n].Value pairs naming the
    // faulting module and the exception code.
    public static WindowsReport? Parse(string text, string folder)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim('\r', '﻿', ' ');
            var i = line.IndexOf('=');
            if (i > 0) values[line[..i]] = line[(i + 1)..];
        }
        if (!values.TryGetValue("EventType", out var type)) return null;
        string Sig(string name)
        {
            for (var k = 0; k < 16; k++)
                if (values.TryGetValue($"Sig[{k}].Name", out var n) && n.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return values.TryGetValue($"Sig[{k}].Value", out var v) ? v : "";
            return "";
        }
        var time = values.TryGetValue("EventTime", out var ft) && long.TryParse(ft, out var f) && f > 0 ? DateTimeOffset.FromFileTime(f) : DateTimeOffset.MinValue;
        return new(time, type.Contains("Hang", StringComparison.OrdinalIgnoreCase), Sig("Fault Module Name"), Sig("Exception Code").ToLowerInvariant(), folder);
    }

    public static string Explain(string code) => code.ToLowerInvariant() switch
    {
        "c000027b" => "an error inside WinUI, the toolkit Lilt's window is built with",
        "c0000005" => "a read or write of memory that wasn't there",
        "c0000409" => "a fast stop after an unrecoverable error",
        "e0434352" => "an unhandled .NET error",
        "c00000fd" => "a stack overflow",
        "c0000374" => "damaged memory",
        "80000003" => "a breakpoint",
        _ => "",
    };

    // The folders Windows keeps its reports in, for you and for the whole PC.
    public static IEnumerable<string> Roots()
    {
        foreach (var root in new[] { Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.CommonApplicationData })
            foreach (var kind in new[] { "ReportArchive", "ReportQueue" })
                yield return Path.Combine(Environment.GetFolderPath(root), "Microsoft", "Windows", "WER", kind);
    }

    // Reports for exeName only (folders are named like AppCrash_Lilt.exe_…), newest first.
    public static List<WindowsReport> Find(IEnumerable<string> roots, string exeName, int max = 30)
    {
        var found = new List<WindowsReport>();
        foreach (var root in roots)
            try
            {
                if (!Directory.Exists(root)) continue;
                foreach (var dir in Directory.EnumerateDirectories(root, $"*_{exeName}_*"))
                {
                    var file = Path.Combine(dir, "Report.wer");
                    if (File.Exists(file) && Parse(File.ReadAllText(file), dir) is { } r) found.Add(r);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        return found.OrderByDescending(r => r.Time).Take(max).ToList();
    }
}
