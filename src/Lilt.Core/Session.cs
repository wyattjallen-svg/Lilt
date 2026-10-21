namespace Lilt.Core;

// Workspace "" is the first one ("Main").
public sealed record SessionTab(string Url, string Title, bool Pinned = false, string Workspace = "");

// Open tabs, saved on every change and restored on the next start. Active is the shown tab's index; Split is the
// index of the tab beside it in split view, or -1. Workspaces lists their names in order (tabs name theirs).
public sealed record Session(IReadOnlyList<SessionTab> Tabs, int Active, int Split = -1, IReadOnlyList<string>? Workspaces = null)
{
    public static Session? Load(string path)
    {
        if (Files.ReadJson<Session>(path) is not { Tabs.Count: > 0 } s) return null;
        var active = Math.Clamp(s.Active, 0, s.Tabs.Count - 1);
        var tabs = s.Tabs.Select(t => t with { Workspace = Workspace.Clean(t.Workspace) }).ToList();
        var names = (s.Workspaces ?? []).Select(Workspace.Clean).Concat(tabs.Select(t => t.Workspace)).Prepend("").Distinct().ToList();
        return s with
        {
            Tabs = tabs,
            Active = active,
            Split = s.Split >= 0 && s.Split < s.Tabs.Count && s.Split != active && tabs[s.Split].Workspace == tabs[active].Workspace ? s.Split : -1,
            Workspaces = names,
        };
    }

    public void Save(string path) => Files.WriteJson(path, this);
}

public static class Workspace
{
    public const string MainName = "Main";

    // Names are short plain text; anything else becomes the main workspace.
    public static string Clean(string? name)
    {
        var n = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return n.Length > 40 ? n[..40] : n == MainName ? "" : n;
    }

    public static string Display(string name) => name.Length == 0 ? MainName : name;
}
