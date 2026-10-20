namespace Lilt.Core.Navigation;

// Pages you've visited, kept by Lilt for address-bar suggestions (WebView2's own history isn't readable by apps).
// Last is Unix milliseconds. Capped; the least useful entries go first.
public sealed record Visit(string Url, string Title, int Count, long Last);

public sealed class History(string path)
{
    public const int Max = 5000;
    readonly Dictionary<string, Visit> items = [];

    public IEnumerable<Visit> All => items.Values;
    public int Count => items.Count;
    public bool Dirty { get; private set; }

    public void Load()
    {
        items.Clear();
        foreach (var v in Files.ReadJson<List<Visit?>>(path) ?? [])
            if (v is not null && Key(v.Url) is { } key) items[key] = v with { Url = key, Title = v.Title ?? "" };
    }

    public void Save()
    {
        Files.WriteJson(path, items.Values.OrderByDescending(v => v.Last).ToList());
        Dirty = false;
    }

    public void Record(string? url, string? title, long now)
    {
        if (Key(url) is not { } key) return;
        items[key] = items.TryGetValue(key, out var v)
            ? v with { Count = Math.Min(v.Count + 1, 100000), Last = now, Title = string.IsNullOrWhiteSpace(title) ? v.Title : Clip(title) }
            : new Visit(key, Clip(title ?? ""), 1, now);
        Dirty = true;
        if (items.Count > Max)
            foreach (var old in items.Values.OrderBy(x => Score(x, now)).Take(items.Count - Max).ToList()) items.Remove(old.Url);
    }

    public void Retitle(string? url, string? title)
    {
        if (Key(url) is not { } key || string.IsNullOrWhiteSpace(title) || !items.TryGetValue(key, out var v) || v.Title == title) return;
        items[key] = v with { Title = Clip(title) };
        Dirty = true;
    }

    public bool Remove(string? url)
    {
        var removed = Key(url) is { } key && items.Remove(key);
        Dirty |= removed;
        return removed;
    }

    public void Clear()
    {
        items.Clear();
        Dirty = true;
    }

    // Frecency: visits, worth less as they age (half every two weeks).
    public static double Score(Visit v, long now) => v.Count * Math.Pow(0.5, Math.Max(0, now - v.Last) / (14 * 86400000.0));

    // Web pages only, without the #fragment, so one page is one entry.
    static string? Key(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || !UrlPolicy.IsWeb(u) || u.Host.Length == 0) return null;
        return u.Fragment.Length > 0 ? u.AbsoluteUri[..^u.Fragment.Length] : u.AbsoluteUri;
    }

    static string Clip(string s) => s.Length <= 300 ? s.Trim() : s[..300].Trim();
}
