using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lilt.Core.Navigation;

namespace Lilt.Core.Bookmarks;

// Folder "" is the bookmarks bar (shown on the home tab when there's no "Home" folder).
public sealed record Bookmark(string Title, string Url, string Folder = "");

public sealed class BookmarkStore(string path)
{
    public List<Bookmark> Items { get; private set; } = [];

    // A file that can't be read is kept as bookmarks.json.bak instead of being overwritten by the next save.
    public void Load()
    {
        var items = Files.ReadJson<List<Bookmark?>>(path);
        if (items is null && File.Exists(path)) File.Copy(path, path + ".bak", overwrite: true);
        Items = (items ?? [])
            .Select(b => (b, url: Normalize(b?.Url)))
            .Where(x => x.url is not null)
            .Select(x => new Bookmark(x.b!.Title ?? x.url!, x.url!, x.b.Folder ?? ""))
            .ToList();
    }

    public void Save() => Files.WriteJson(path, Items);

    public bool Contains(string? url) => Normalize(url) is { } u && Items.Any(b => b.Url == u);

    public bool Add(string title, string url, string folder = "")
    {
        if (Normalize(url) is not { } u || Contains(u)) return false;
        Items.Add(new Bookmark(string.IsNullOrWhiteSpace(title) ? u : title.Trim(), u, folder));
        return true;
    }

    public bool Remove(string? url) => Normalize(url) is { } u && Items.RemoveAll(b => b.Url == u) > 0;

    // Replaces a bookmark (title, address, folder). False if the new address is invalid or already another bookmark.
    public bool Update(Bookmark old, string title, string url, string folder)
    {
        var i = Items.IndexOf(old);
        if (i < 0 || Normalize(url) is not { } u || (u != old.Url && Contains(u))) return false;
        Items[i] = new Bookmark(string.IsNullOrWhiteSpace(title) ? u : title.Trim(), u, folder.Trim().Trim('/'));
        return true;
    }

    // The bookmarks actually added (already saved ones are skipped), so an import can be undone.
    public List<Bookmark> Merge(IEnumerable<Bookmark> incoming) => incoming.Where(b => Add(b.Title, b.Url, b.Folder)).Select(b => Items[^1]).ToList();

    // Only web addresses are kept: javascript: bookmarklets and file: links are dropped on import.
    public static string? Normalize(string? url) =>
        Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var u) && UrlPolicy.IsWeb(u) && u.Host.Length > 0 ? u.AbsoluteUri : null;
}

// Passwords: the file was a password export (it has a password column). Nothing in it is imported as bookmarks;
// PasswordSites lists its sites (name and address only, never the passwords) as they'd have looked as bookmarks, so
// bookmarks left by importing it earlier can be found and removed.
public sealed record ImportResult(IReadOnlyList<Bookmark> Bookmarks, int Skipped, string Format, bool Passwords = false, IReadOnlyList<Bookmark>? PasswordSites = null);

// Reads Chromium's Bookmarks JSON (Chrome, Edge, Helium, Brave...), the Netscape HTML every browser exports,
// and CSV files with a url column (title/name and folder columns optional).
public static partial class BookmarkImport
{
    public const long MaxBytes = 20 * 1024 * 1024;

    public static ImportResult Parse(string text)
    {
        var trimmed = text.TrimStart('﻿', ' ', '\r', '\n', '\t');
        var raw = new List<Bookmark>();
        string format;
        if (trimmed.StartsWith('{'))
        {
            format = "Chromium bookmarks";
            using var doc = JsonDocument.Parse(trimmed);
            if (doc.RootElement.TryGetProperty("roots", out var roots))
                foreach (var (key, label) in new[] { ("bookmark_bar", ""), ("other", "Other bookmarks"), ("synced", "Mobile bookmarks") })
                    if (roots.TryGetProperty(key, out var node)) Walk(node, label, raw);
        }
        else if (trimmed.Contains("<DL", StringComparison.OrdinalIgnoreCase) || trimmed.Contains("<A ", StringComparison.OrdinalIgnoreCase))
        {
            format = "HTML bookmarks";
            ParseHtml(trimmed, raw);
        }
        else
        {
            format = "CSV";
            if (ParseCsv(trimmed, raw))
            {
                var sites = raw.Select(b => BookmarkStore.Normalize(b.Url) is { } u ? b with { Url = u, Title = string.IsNullOrWhiteSpace(b.Title) ? u : b.Title.Trim() } : null)
                    .OfType<Bookmark>().DistinctBy(b => b.Url).ToList();
                return new([], 0, "passwords", true, sites);
            }
        }

        var seen = new HashSet<string>();
        var kept = new List<Bookmark>();
        var skipped = 0;
        foreach (var b in raw)
        {
            if (BookmarkStore.Normalize(b.Url) is not { } url || !seen.Add(url))
            {
                skipped++;
                continue;
            }
            kept.Add(b with { Url = url, Title = string.IsNullOrWhiteSpace(b.Title) ? url : b.Title.Trim() });
        }
        return new(kept, skipped, format);
    }

    static void Walk(JsonElement node, string folder, List<Bookmark> into)
    {
        if (!node.TryGetProperty("children", out var children) || children.ValueKind != JsonValueKind.Array) return;
        foreach (var child in children.EnumerateArray())
        {
            var name = child.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var type = child.TryGetProperty("type", out var ty) ? ty.GetString() : null;
            if (type == "url" && child.TryGetProperty("url", out var u)) into.Add(new(name, u.GetString() ?? "", folder));
            else if (type == "folder") Walk(child, Join(folder, name), into);
        }
    }

    [GeneratedRegex("""<H3([^>]*)>(.*?)</H3>|<A\s([^>]*)>(.*?)</A>|<DL\b|</DL>""", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HtmlToken();

    [GeneratedRegex("""HREF\s*=\s*"([^"]*)"|HREF\s*=\s*'([^']*)'""", RegexOptions.IgnoreCase)]
    private static partial Regex Href();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tag();

    static void ParseHtml(string html, List<Bookmark> into)
    {
        var stack = new List<string>();
        string? pending = null;
        foreach (Match m in HtmlToken().Matches(html))
        {
            if (m.Groups[2].Success)
            {
                // Chromium marks its bookmarks bar folder; treat it as the root like the JSON import does.
                pending = m.Groups[1].Value.Contains("PERSONAL_TOOLBAR_FOLDER", StringComparison.OrdinalIgnoreCase) ? "" : Text(m.Groups[2].Value);
            }
            else if (m.Groups[4].Success)
            {
                var href = Href().Match(m.Groups[3].Value);
                var url = href.Groups[1].Success ? href.Groups[1].Value : href.Groups[2].Value;
                into.Add(new(Text(m.Groups[4].Value), WebUtility.HtmlDecode(url), string.Join("/", stack.Where(s => s.Length > 0))));
            }
            else if (m.Value.StartsWith("</", StringComparison.Ordinal))
            {
                if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
            }
            else
            {
                stack.Add(pending ?? "");
                pending = null;
            }
        }
    }

    static string Text(string html) => WebUtility.HtmlDecode(Tag().Replace(html, "")).Trim();

    // True for a password export (Chrome, Edge, Firefox, Bitwarden, 1Password...: a header with a password column);
    // then only each row's name and address are read.
    static bool ParseCsv(string text, List<Bookmark> into)
    {
        var rows = Csv(text);
        if (rows.Count == 0) return false;
        var header = rows[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
        int Col(params string[] names) => header.FindIndex(h => names.Contains(h));
        if (header.Any(h => h.Contains("password")))
        {
            int url = Col("url", "login_uri", "website", "origin", "address"), name = Col("name", "title");
            if (url >= 0)
                foreach (var row in rows.Skip(1))
                    into.Add(new(name >= 0 && name < row.Count ? row[name] : "", url < row.Count ? row[url] : ""));
            return true;
        }
        var urlCol = Col("url", "href", "link", "address");
        var titleCol = Col("title", "name");
        var folderCol = Col("folder", "category", "path", "tags");
        var body = urlCol >= 0 ? rows.Skip(1) : rows;
        foreach (var row in body)
        {
            string Cell(int i) => i >= 0 && i < row.Count ? row[i] : "";
            if (urlCol >= 0)
            {
                into.Add(new(Cell(titleCol), Cell(urlCol), Cell(folderCol)));
                continue;
            }
            // No header: take the first web address in the row as the URL and the first other cell as the title.
            var url = row.FirstOrDefault(c => BookmarkStore.Normalize(c) is not null) ?? row.FirstOrDefault() ?? "";
            into.Add(new(row.FirstOrDefault(c => c != url) ?? "", url));
        }
        return false;
    }

    // RFC 4180: quoted fields, doubled quotes, newlines inside quotes.
    static List<List<string>> Csv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cell.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { row.Add(cell.ToString()); cell.Clear(); }
            else if (c is '\n' or '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(cell.ToString());
                cell.Clear();
                if (row.Any(x => x.Length > 0)) rows.Add(row);
                row = [];
            }
            else cell.Append(c);
        }
        row.Add(cell.ToString());
        if (row.Any(x => x.Length > 0)) rows.Add(row);
        return rows;
    }

    static string Join(string folder, string name) => folder.Length == 0 ? name : $"{folder}/{name}";
}
