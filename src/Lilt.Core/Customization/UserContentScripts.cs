using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Lilt.Core.Customization;

public sealed record UserFile(string Name, string Text);

public sealed record UserContentBuild(IReadOnlyList<string> Scripts, int Styles, int UserScripts, IReadOnlyList<string> Skipped);

// Turns files like "example.com.css", "github.com.js", "all.css" into document-start scripts.
// "example.com" also matches its subdomains; "all" matches every http(s) page.
// CSS uses a constructed stylesheet, which page Content-Security-Policy does not block.
// Each user script is its own script, so a syntax error in one file can't break the others.
public static partial class UserContentScripts
{
    public const int MaxFileBytes = 512 * 1024;

    [GeneratedRegex("^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+$")]
    private static partial Regex HostName();

    const string WebOnly = "if (location.protocol !== 'http:' && location.protocol !== 'https:') return;";

    public static UserContentBuild Build(IEnumerable<UserFile> files)
    {
        var scripts = new List<string>();
        var skipped = new List<string>();
        var css = new List<(string Host, string Text)>();
        var js = 0;
        foreach (var f in files.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            var ext = Path.GetExtension(f.Name).ToLowerInvariant();
            if (ext is not (".css" or ".js")) continue;
            var host = Path.GetFileNameWithoutExtension(f.Name).ToLowerInvariant();
            if (host == "all") host = "";
            else if (!HostName().IsMatch(host))
            {
                skipped.Add($"{f.Name}: name must be a host like example.com, or 'all'");
                continue;
            }
            if (Encoding.UTF8.GetByteCount(f.Text) > MaxFileBytes)
            {
                skipped.Add($"{f.Name}: larger than {MaxFileBytes / 1024} KB");
                continue;
            }
            if (ext == ".css") css.Add((host, f.Text));
            else
            {
                scripts.Add(Script(host, f.Name, f.Text));
                js++;
            }
        }
        if (css.Count > 0) scripts.Insert(0, Styles(css));
        return new(scripts, css.Count, js, skipped);
    }

    static string Styles(List<(string Host, string Text)> css)
    {
        var sb = new StringBuilder();
        sb.Append("(() => {").Append(WebOnly);
        sb.Append("const h = location.hostname, m = s => s === '' || h === s || h.endsWith('.' + s), css = [];");
        foreach (var (host, text) in css)
            sb.Append($"if (m({Json(host)})) css.push({Json(text)});");
        sb.Append("""
            if (!css.length) return;
            const sheet = new CSSStyleSheet();
            sheet.replaceSync(css.join('\n'));
            const add = () => { if (!document.adoptedStyleSheets.includes(sheet)) document.adoptedStyleSheets = [...document.adoptedStyleSheets, sheet]; };
            add();
            document.addEventListener('DOMContentLoaded', add, { once: true });
            })();
            """);
        return sb.ToString();
    }

    static string Script(string host, string name, string code)
    {
        var guard = host.Length == 0 ? "" : $"const h = location.hostname, s = {Json(host)}; if (h !== s && !h.endsWith('.' + s)) return;";
        return $"(() => {{{WebOnly}{guard}\ntry {{\n{code}\n}} catch (e) {{ console.error({Json($"[Lilt user script {name}]")}, e); }}\n}})();";
    }

    static string Json(string s) => JsonSerializer.Serialize(s);
}
