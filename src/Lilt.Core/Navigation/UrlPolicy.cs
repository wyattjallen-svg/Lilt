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

    public static bool IsBlank(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == "about" && uri.AbsolutePath == "blank";

    public static bool IsInsecure(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttp && !IsLoopback(uri.Host);

    public static string DisplayHost(string? uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.Host.Length > 0 ? parsed.Host : uri ?? "";

    static bool IsLoopback(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));
}
