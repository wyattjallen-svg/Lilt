namespace Lilt.Core.Navigation;

// Lilt's own pages, typed as about: addresses. They're served from memory under a reserved host (.example names never
// exist on the internet), and the address bar shows them by their about: name.
public static class InternalPages
{
    public const string Host = "about.lilt.example";
    public static readonly Uri Logs = new($"https://{Host}/logs");

    static readonly Dictionary<string, Uri> pages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["logs"] = Logs,
        ["crashes"] = new($"https://{Host}/logs#crash"),
    };

    public static IEnumerable<string> Names => pages.Keys.Select(k => "about:" + k);

    // "about:logs" → its page; null for about: addresses Lilt doesn't have.
    public static Uri? FromAbout(string text)
    {
        var t = text.Trim();
        if (!t.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return null;
        return pages.TryGetValue(t["about:".Length..].Trim('/'), out var uri) ? uri : null;
    }

    public static bool Is(Uri? uri) => uri is { IsAbsoluteUri: true } && uri.Scheme == Uri.UriSchemeHttps && uri.Host.Equals(Host, StringComparison.OrdinalIgnoreCase);
    public static bool Is(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var u) && Is(u);

    // How the address bar writes one of these pages.
    public static string? AboutName(Uri? uri) =>
        !Is(uri) ? null : uri!.AbsolutePath.Trim('/') is { Length: > 0 } p ? "about:" + (uri.Fragment == "#crash" ? "crashes" : p) : "about:logs";
}
