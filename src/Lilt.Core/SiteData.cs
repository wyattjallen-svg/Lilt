using System.Net;

namespace Lilt.Core;

// Storage cleanup's rules: which sites to forget, and how much room the engine's folders take.
public static class SiteData
{
    // A host's site, the part sign-ins usually belong to: "open.spotify.com" → "spotify.com", "news.bbc.co.uk" → "bbc.co.uk".
    // ponytail: a two-letter-country heuristic instead of the public suffix list; a wrong guess only makes a site narrower.
    public static string Site(string host)
    {
        host = host.Trim().Trim('.').ToLowerInvariant();
        if (IPAddress.TryParse(host, out _)) return host;
        var parts = host.Split('.');
        if (parts.Length <= 2) return host;
        var take = parts[^1].Length == 2 && parts[^2].Length <= 3 ? 3 : 2;
        return string.Join('.', parts[^take..]);
    }

    // Sites (from candidates, usually cookie domains) last opened more than `days` ago. A site never opened counts only
    // once Lilt's history goes back that far, so clearing history (or turning it off) never signs you out of everything.
    public static IReadOnlyList<string> Stale(IEnumerable<string> candidates, IEnumerable<(string Host, long Last)> visits, IEnumerable<string> keep, long now, int days)
    {
        if (days <= 0) return [];
        var last = new Dictionary<string, long>();
        var oldest = now;
        foreach (var (host, at) in visits)
        {
            var s = Site(host);
            last[s] = Math.Max(last.GetValueOrDefault(s), at);
            oldest = Math.Min(oldest, at);
        }
        var kept = keep.Where(k => k.Length > 0).Select(Site).ToHashSet();
        var cutoff = now - days * 86400000L;
        var covered = oldest <= cutoff;
        return candidates.Where(c => c.Trim('.').Length > 0).Select(Site).Distinct()
            .Where(s => !kept.Contains(s) && (last.TryGetValue(s, out var at) ? at < cutoff : covered)).ToList();
    }
}

// Bytes in the engine's folders: Cache is cached files (and compiled scripts and shaders), Offline is sites' offline
// copies (service workers and their caches), SiteData is what sites store (sign-ins included), Total is everything.
public sealed record StorageUse(long Cache, long Offline, long SiteData, long Total)
{
    static readonly HashSet<string> cacheDirs = new(StringComparer.OrdinalIgnoreCase)
        { "Cache", "Code Cache", "GPUCache", "GrShaderCache", "ShaderCache", "DawnCache", "DawnGraphiteCache", "DawnWebGPUCache", "GraphiteDawnCache" };
    static readonly HashSet<string> siteDirs = new(StringComparer.OrdinalIgnoreCase)
        { "IndexedDB", "Local Storage", "Session Storage", "WebStorage", "File System", "blob_storage", "databases", "Storage" };

    public static StorageUse Measure(string folder)
    {
        long cache = 0, offline = 0, site = 0, total = 0;
        if (!Directory.Exists(folder)) return new(0, 0, 0, 0);
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", options))
        {
            long size;
            try
            {
                size = file.Length;
            }
            catch (IOException)
            {
                continue;
            }
            total += size;
            var dirs = Path.GetRelativePath(folder, file.DirectoryName ?? folder).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (dirs.Any(cacheDirs.Contains)) cache += size;
            else if (dirs.Contains("Service Worker", StringComparer.OrdinalIgnoreCase)) offline += size;
            else if (dirs.Any(siteDirs.Contains) || file.Name is "Cookies" or "Cookies-journal") site += size;
        }
        return new(cache, offline, site, total);
    }

    public static string Size(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" : $"{Math.Max(0, bytes) / (double)(1 << 20):0} MB";
}
