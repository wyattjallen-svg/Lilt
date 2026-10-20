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
