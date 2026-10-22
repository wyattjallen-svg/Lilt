using System.Net;
using System.Text.RegularExpressions;

namespace Lilt.Core.Navigation;

public enum AddressKind { Empty, Navigate, Search, Rejected }

public readonly record struct AddressResult(AddressKind Kind, Uri? Uri = null, string? Reason = null);

public static partial class AddressResolver
{
    public const string DefaultSearch = "https://duckduckgo.com/?q={0}";

    [GeneratedRegex(@"^[a-zA-Z][a-zA-Z0-9+.\-]*:")]
    private static partial Regex SchemePrefix();

    [GeneratedRegex(@"^[^/\s:?#]+:\d{1,5}(?:[/?#].*)?$")]
    private static partial Regex HostWithPort();

    public static AddressResult Resolve(string? input, string searchTemplate = DefaultSearch)
    {
        var text = input?.Trim() ?? "";
        if (text.Length == 0) return new(AddressKind.Empty);
        // Lilt's own pages: about:logs, about:crashes.
        if (text.StartsWith("about:", StringComparison.OrdinalIgnoreCase) && !text.Equals("about:blank", StringComparison.OrdinalIgnoreCase))
            return InternalPages.FromAbout(text) is { } page ? new(AddressKind.Navigate, page)
                : new(AddressKind.Rejected, Reason: $"Lilt's own pages are {string.Join(" and ", InternalPages.Names)}.");
        if (text.Any(char.IsWhiteSpace)) return Search(text, searchTemplate);

        if (SchemePrefix().IsMatch(text) && !HostWithPort().IsMatch(text))
        {
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return new(AddressKind.Rejected, Reason: "That isn't a valid address.");
            if (UrlPolicy.IsNavigable(uri) || UrlPolicy.IsExtensionPage(uri)) return new(AddressKind.Navigate, UrlPolicy.StripTracking(uri));
            return new(AddressKind.Rejected, Reason: $"Lilt only opens web addresses (http and https), not {uri.Scheme}: links.");
        }

        var host = HostPart(text);
        if (IsLocalHost(host) && Uri.TryCreate("http://" + text, UriKind.Absolute, out var local)) return new(AddressKind.Navigate, local);
        if (IsDomain(host) && Uri.TryCreate("https://" + text, UriKind.Absolute, out var web)) return new(AddressKind.Navigate, UrlPolicy.StripTracking(web));
        return Search(text, searchTemplate);
    }

    // Always a search, even for text that looks like an address.
    public static Uri SearchUri(string text, string template = DefaultSearch) => Search(text.Trim(), template).Uri!;

    static AddressResult Search(string text, string template) =>
        new(AddressKind.Search, new Uri(string.Format(System.Globalization.CultureInfo.InvariantCulture, template, Uri.EscapeDataString(text))));

    static string HostPart(string text)
    {
        var end = text.IndexOfAny(['/', '?', '#']);
        var authority = end < 0 ? text : text[..end];
        if (authority.StartsWith('['))
        {
            var close = authority.IndexOf(']');
            return close < 0 ? authority : authority[..(close + 1)];
        }
        var colon = authority.LastIndexOf(':');
        return colon < 0 ? authority : authority[..colon];
    }

    static bool IsLocalHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || (host.StartsWith('[') && host.EndsWith(']') && IPAddress.TryParse(host[1..^1], out _))
        || (host.Count(c => c == '.') == 3 && IPAddress.TryParse(host, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);

    static bool IsDomain(string host)
    {
        var labels = host.Split('.');
        if (labels.Length < 2) return false;
        foreach (var label in labels)
        {
            if (label.Length is 0 or > 63 || label[0] == '-' || label[^1] == '-') return false;
            if (!label.All(c => char.IsLetterOrDigit(c) || c == '-')) return false;
        }
        var tld = labels[^1];
        return tld.Length >= 2 && tld.All(char.IsLetter);
    }
}
