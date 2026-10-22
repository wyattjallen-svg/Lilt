using System.Net;

namespace Lilt.Core.Navigation;

public static class UrlPolicy
{
    public static bool IsWeb(Uri uri) => uri.IsAbsoluteUri && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    // What Lilt itself will navigate to on a page's or the user's behalf.
    public static bool IsNavigable(Uri uri) =>
        (IsWeb(uri) && uri.Host.Length > 0) || IsBlank(uri);

    public static bool IsNavigable(string? uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && IsNavigable(parsed);

    // Pages of installed extensions (options, dashboards). Openable from the address bar or Lilt's menus, never by websites.
    // Tracking parameters that name a campaign or a click, not content. Removed from addresses Lilt opens itself.
    static readonly HashSet<string> trackers = new(StringComparer.OrdinalIgnoreCase)
    {
        "fbclid", "gclid", "gclsrc", "dclid", "msclkid", "mc_eid", "mc_cid", "yclid", "igshid", "_hsenc", "_hsmi", "mkt_tok",
        "vero_id", "oly_anon_id", "oly_enc_id", "wickedid", "twclid", "ttclid",
    };

    public static Uri StripTracking(Uri uri)
    {
        if (!IsWeb(uri) || uri.Query.Length <= 1) return uri;
        var all = uri.Query[1..].Split('&', StringSplitOptions.RemoveEmptyEntries);
        var kept = all.Where(p => { var k = p.Split('=')[0]; return !k.StartsWith("utm_", StringComparison.OrdinalIgnoreCase) && !trackers.Contains(k); }).ToArray();
        if (kept.Length == all.Length) return uri;
        return new UriBuilder(uri) { Query = string.Join('&', kept) }.Uri;
    }

    public static bool IsExtensionPage(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == "chrome-extension" && uri.Host.Length == 32;

    public static bool IsBlank(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == "about" && uri.AbsolutePath == "blank";

    public static bool IsInsecure(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttp && !IsLoopback(uri.Host);

    // How the address bar shows a page while you aren't editing it: https:// and a lone trailing slash left off
    // (http:// stays, so an insecure page never looks like a secure one).
    public static string Pretty(Uri uri)
    {
        var s = uri.AbsoluteUri;
        if (uri.Scheme == Uri.UriSchemeHttps) s = s["https://".Length..];
        return uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.Fragment.Length == 0 ? s.TrimEnd('/') : s;
    }

    public static string DisplayHost(string? uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.Host.Length > 0 ? parsed.Host : uri ?? "";

    static bool IsLoopback(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));
}
