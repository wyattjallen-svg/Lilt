using System.Globalization;
using System.Net;
using System.Text;

namespace Lilt.Core.Diagnostics;

// What about:logs shows besides the entries. Colors are the theme's, as #rrggbb. Token lets the page's own buttons clear
// the log or open its folder; Nonce allows the page's one style and one script under its content security policy.
public sealed record LogsPageInfo(string Version, string Engine, string Windows, string Token, string Nonce,
    string Background, string Surface, string Text, string Muted, string Accent, string Border, bool Light, double Zoom = 1);

public static class LogsPage
{
    public static string Csp(string nonce) =>
        $"default-src 'none'; style-src 'nonce-{nonce}'; script-src 'nonce-{nonce}'; connect-src 'self'; img-src 'none'; " +
        "frame-ancestors 'none'; base-uri 'none'; form-action 'none'";

    static readonly (LogLevel Level, string Id, string Name)[] levels =
        [(LogLevel.Crash, "crash", "Crashes"), (LogLevel.Error, "error", "Errors"), (LogLevel.Warning, "warning", "Warnings"), (LogLevel.Info, "info", "Info")];

    static string Id(LogLevel l) => levels.First(x => x.Level == l).Id;
    static string E(string s) => WebUtility.HtmlEncode(s);
    static string When(DateTimeOffset t) => t.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    // One line for the top: how many crashes there were this week and when the last one was.
    public static string Summary(IReadOnlyList<LogEntry> entries, DateTimeOffset now)
    {
        var week = entries.Where(e => e.Level == LogLevel.Crash && now - e.Time < TimeSpan.FromDays(7)).ToList();
        if (week.Count == 0) return "No crashes in the last 7 days.";
        var ago = now - week.Max(e => e.Time);
        var last = ago.TotalMinutes < 1 ? "just now" : ago.TotalHours < 1 ? $"{(int)ago.TotalMinutes} min ago" : ago.TotalDays < 1 ? $"{(int)ago.TotalHours} h ago" : $"{(int)ago.TotalDays} d ago";
        return $"{week.Count} crash{(week.Count == 1 ? "" : "es")} in the last 7 days, the latest {last}.";
    }

    // For bug reports: no file paths beyond what the entries carry, and capped.
    public static string PlainText(LogsPageInfo info, IReadOnlyList<LogEntry> entries)
    {
        var b = new StringBuilder($"Lilt {info.Version} · engine {info.Engine} · Windows {info.Windows}\n\n");
        foreach (var e in entries)
        {
            if (b.Length > 200_000) break;
            b.Append(When(e.Time)).Append(' ').Append(Id(e.Level)).Append(' ').Append(e.Source).Append(": ").Append(e.Message).Append('\n');
            if (e.Detail.Length > 0) b.Append("    ").Append(e.Detail.Replace("\n", "\n    ")).Append('\n');
        }
        return b.ToString();
    }

    public static string Render(LogsPageInfo info, IReadOnlyList<LogEntry> entries, DateTimeOffset now)
    {
        var b = new StringBuilder();
        b.Append($$"""
            <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="color-scheme" content="{{(info.Light ? "light" : "dark")}}">
            <meta name="viewport" content="width=device-width"><title>Logs · Lilt</title>
            <style nonce="{{E(info.Nonce)}}">
            :root{--bg:{{E(info.Background)}};--surface:{{E(info.Surface)}};--text:{{E(info.Text)}};--muted:{{E(info.Muted)}};--accent:{{E(info.Accent)}};--border:{{E(info.Border)}};
            --crash:#e5484d;--error:#f2994a;--warning:#d6b13c;--info:var(--muted)}
            *{box-sizing:border-box}
            html{zoom:{{Math.Clamp(info.Zoom, 0.75, 1.5).ToString(CultureInfo.InvariantCulture)}}}
            body{margin:0;background:var(--bg);color:var(--text);font:14px/1.5 "Segoe UI Variable Text","Segoe UI",system-ui,sans-serif}
            main{max-width:960px;margin:0 auto;padding:40px 28px 64px}
            h1{font:600 28px/1.2 "Segoe UI Variable Display","Segoe UI",system-ui,sans-serif;margin:0 0 4px}
            .meta,.summary{color:var(--muted);margin:0}
            .summary{margin-top:14px;font-size:15px;color:var(--text)}
            .bar{display:flex;flex-wrap:wrap;gap:8px;align-items:center;margin:22px 0 14px}
            .actions{display:flex;gap:8px;align-items:center;margin-left:auto}
            button{font:inherit;color:var(--text);background:var(--surface);border:1px solid var(--border);border-radius:8px;padding:5px 12px;cursor:pointer}
            button:hover{border-color:var(--accent)}
            .chip[aria-pressed=true]{background:var(--accent);border-color:var(--accent);color:var(--bg)}
            .chip b{font-weight:600;margin-left:4px;opacity:.8}
            ol{list-style:none;margin:0;padding:0;border:1px solid var(--border);border-radius:12px;overflow:hidden;background:var(--surface)}
            li{display:grid;grid-template-columns:150px 78px 1fr;gap:4px 14px;padding:10px 16px;border-top:1px solid var(--border);border-left:3px solid var(--l)}
            li:first-child{border-top:0}
            li.crash{--l:var(--crash)} li.error{--l:var(--error)} li.warning{--l:var(--warning)} li.info{--l:transparent}
            time{color:var(--muted);font-variant-numeric:tabular-nums;font-size:13px;padding-top:1px}
            .level{font-size:12px;font-weight:600;letter-spacing:.04em;text-transform:uppercase;color:var(--l);padding-top:2px}
            li.info .level{color:var(--muted)}
            .msg{min-width:0;overflow-wrap:anywhere}
            .src{display:inline-block;font-size:12px;color:var(--muted);border:1px solid var(--border);border-radius:6px;padding:0 6px;margin-right:8px}
            details{grid-column:3}
            summary{color:var(--muted);cursor:pointer;font-size:13px}
            pre{margin:6px 0 2px;padding:10px 12px;background:var(--bg);border-radius:8px;overflow:auto;max-height:340px;font:12px/1.45 "Cascadia Mono",Consolas,monospace;white-space:pre-wrap;overflow-wrap:anywhere}
            .empty{padding:36px 16px;text-align:center;color:var(--muted)}
            .hidden{display:none}
            .toast{color:var(--accent);min-width:7em}
            @media (max-width:640px){li{grid-template-columns:1fr}details{grid-column:1} }
            </style></head><body><main>
            <h1>Logs</h1>
            <p class="meta">Lilt {{E(info.Version)}} · web engine {{E(info.Engine)}} · Windows {{E(info.Windows)}}</p>
            <p class="summary">{{E(Summary(entries, now))}}</p>
            <div class="bar" role="toolbar" aria-label="Show">
            <button class="chip" data-f="all" aria-pressed="true">All<b>{{entries.Count}}</b></button>
            """);
        foreach (var (level, id, name) in levels)
            b.Append($"<button class=\"chip\" data-f=\"{id}\" aria-pressed=\"false\">{name}<b>{entries.Count(e => e.Level == level)}</b></button>");
        b.Append($$"""
            <span class="actions"><span class="toast" id="toast" role="status"></span>
            <button id="copy" title="Copies the log as text, for a bug report">Copy</button>
            <button id="folder">Open folder</button>
            <button id="clear" title="Clears Lilt's log; Windows' and the engine's crash reports stay">Clear</button></span>
            </div>
            <ol id="log" data-t="{{E(info.Token)}}">
            """);
        if (entries.Count == 0) b.Append("<li class=\"info empty\">Nothing logged yet. Lilt writes here when it starts and when something goes wrong.</li>");
        foreach (var e in entries)
        {
            var id = Id(e.Level);
            b.Append($"<li class=\"{id}\"><time datetime=\"{e.Time:O}\">{When(e.Time)}</time><span class=\"level\">{id}</span>")
                .Append($"<div class=\"msg\"><span class=\"src\">{E(e.Source)}</span>{E(e.Message)}</div>");
            if (e.Detail.Length > 0) b.Append($"<details><summary>Details</summary><pre>{E(e.Detail)}</pre></details>");
            b.Append("</li>");
        }
        b.Append($$"""
            </ol>
            <p class="empty hidden" id="none">Nothing of this kind.</p>
            <textarea id="raw" hidden>{{E(PlainText(info, entries))}}</textarea>
            </main>
            <script nonce="{{E(info.Nonce)}}">
            const log = document.getElementById('log'), chips = [...document.querySelectorAll('.chip')], toast = document.getElementById('toast');
            const show = f => {
              chips.forEach(c => c.setAttribute('aria-pressed', c.dataset.f === f));
              let n = 0;
              for (const li of log.children) { const on = f === 'all' || li.classList.contains(f); li.classList.toggle('hidden', !on && !li.classList.contains('empty')); if (on) n++; }
              document.getElementById('none').classList.toggle('hidden', n > 0 || f === 'all');
            };
            const fromHash = () => show(chips.some(c => c.dataset.f === location.hash.slice(1)) ? location.hash.slice(1) : 'all');
            chips.forEach(c => c.onclick = () => { history.replaceState(null, '', c.dataset.f === 'all' ? location.pathname : '#' + c.dataset.f); show(c.dataset.f); });
            addEventListener('hashchange', fromHash);
            fromHash();
            const say = t => { toast.textContent = t; setTimeout(() => toast.textContent = '', 2500); };
            const act = what => fetch('/logs/' + what + '?t=' + log.dataset.t, { method: 'POST' });
            document.getElementById('copy').onclick = () => navigator.clipboard.writeText(document.getElementById('raw').value).then(() => say('Copied'), () => say('Couldn\'t copy'));
            document.getElementById('folder').onclick = () => act('folder');
            document.getElementById('clear').onclick = () => act('clear').then(() => location.reload());
            </script></body></html>
            """);
        return b.ToString();
    }
}
