using Lilt.Core.Bookmarks;

namespace Lilt.Core.Navigation;

public enum SuggestionKind { Tab, Bookmark, History }

public sealed record Suggestion(SuggestionKind Kind, string Title, string Url);

// What the address bar offers as you type: open tabs, bookmarks and history whose address or title has every word
// you typed, best first; and the site to complete inline ("you" → "youtube.com").
public static class Suggestions
{
    public static List<Suggestion> Find(string query, IEnumerable<(string Title, string Url)> tabs, IEnumerable<Bookmark> bookmarks,
        IEnumerable<Visit> history, long now, int max = 8)
    {
        var q = query.Trim().ToLowerInvariant();
        var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return [];
        var scored = new Dictionary<string, (Suggestion S, double Score)>();
        void Offer(SuggestionKind kind, string title, string url, double weight)
        {
            var bare = Bare(url);
            var text = title.ToLowerInvariant() + " " + bare;
            if (!words.All(text.Contains)) return;
            var score = weight;
            if (bare.StartsWith(q, StringComparison.Ordinal)) score += 6;
            else if (Host(bare).Split('.').Any(part => part.StartsWith(words[0], StringComparison.Ordinal))) score += 3;
            if (title.StartsWith(q, StringComparison.OrdinalIgnoreCase)) score += 2;
            // Shorter addresses are usually the site rather than one deep page.
            score -= Math.Min(2, bare.Length / 60.0);
            var key = url.TrimEnd('/');
            if (scored.TryGetValue(key, out var have))
            {
                // One row per page: the open tab wins, then the bookmark; scores add up.
                var keep = kind < have.S.Kind ? new Suggestion(kind, title.Length > 0 ? title : have.S.Title, url) : have.S;
                scored[key] = (keep, have.Score + score);
            }
            else scored[key] = (new Suggestion(kind, title, url), score);
        }
        foreach (var (title, url) in tabs) Offer(SuggestionKind.Tab, title, url, 3);
        foreach (var b in bookmarks) Offer(SuggestionKind.Bookmark, b.Title, b.Url, 4);
        foreach (var v in history) Offer(SuggestionKind.History, v.Title, v.Url, Math.Min(8, 1 + 2 * Math.Log2(1 + History.Score(v, now))));
        return scored.Values.OrderByDescending(x => x.Score).Take(max).Select(x => x.S).ToList();
    }

    // The most visited site (bookmarks count too) whose name starts with what was typed, as it should appear in the box.
    public static string? Complete(string typed, IEnumerable<Bookmark> bookmarks, IEnumerable<Visit> history, long now)
    {
        var t = typed.ToLowerInvariant();
        if (t.Length == 0 || t.Contains(' ') || t.Contains("://")) return null;
        var hosts = new Dictionary<string, double>();
        void Add(string url, double score)
        {
            var host = Host(Bare(url));
            hosts[host] = hosts.GetValueOrDefault(host) + score;
        }
        foreach (var b in bookmarks) Add(b.Url, 2);
        foreach (var v in history) Add(v.Url, History.Score(v, now));
        var best = hosts.Where(h => h.Key.StartsWith(t, StringComparison.Ordinal) && h.Key.Length > t.Length).OrderByDescending(h => h.Value).FirstOrDefault();
        return best.Key is null ? null : typed + best.Key[t.Length..];
    }

    // "https://www.example.com/a" → "example.com/a"
    static string Bare(string url)
    {
        var s = url.ToLowerInvariant();
        foreach (var p in new[] { "https://", "http://" })
            if (s.StartsWith(p, StringComparison.Ordinal)) s = s[p.Length..];
        if (s.StartsWith("www.", StringComparison.Ordinal)) s = s[4..];
        return s.TrimEnd('/');
    }

    static string Host(string bare) => bare.Split('/', '?', '#')[0];
}
