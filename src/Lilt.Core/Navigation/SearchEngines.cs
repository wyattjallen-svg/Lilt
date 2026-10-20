namespace Lilt.Core.Navigation;

// pages.search: a known engine's name, or an https address with %s where the words go.
public static class SearchEngines
{
    public static readonly IReadOnlyDictionary<string, (string Name, string Template)> Known = new Dictionary<string, (string, string)>
    {
        ["duckduckgo"] = ("DuckDuckGo", "https://duckduckgo.com/?q={0}"),
        ["google"] = ("Google", "https://www.google.com/search?q={0}"),
        ["bing"] = ("Bing", "https://www.bing.com/search?q={0}"),
        ["brave"] = ("Brave Search", "https://search.brave.com/search?q={0}"),
        ["startpage"] = ("Startpage", "https://www.startpage.com/do/search?q={0}"),
        ["ecosia"] = ("Ecosia", "https://www.ecosia.org/search?q={0}"),
        ["kagi"] = ("Kagi", "https://kagi.com/search?q={0}"),
    };

    public static bool IsValid(string? value) => Resolve(value) is not null;

    // Display name and a string.Format template ({0} = the escaped words); null if the value isn't usable.
    public static (string Name, string Template)? Resolve(string? value)
    {
        var v = value?.Trim() ?? "";
        if (Known.TryGetValue(v.ToLowerInvariant(), out var known)) return known;
        if (!v.Contains("%s", StringComparison.Ordinal) || !Uri.TryCreate(v.Replace("%s", "x"), UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps) return null;
        return (u.Host.StartsWith("www.", StringComparison.Ordinal) ? u.Host[4..] : u.Host, v.Replace("{", "{{").Replace("}", "}}").Replace("%s", "{0}"));
    }

    public static (string Name, string Template) For(string? value) => Resolve(value) ?? Known["duckduckgo"];
}
