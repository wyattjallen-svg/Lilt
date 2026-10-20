namespace Lilt.Core;

public sealed record SessionTab(string Url, string Title, bool Pinned = false);

// Open tabs, saved on every change and restored on the next start. Split is the index of the tab shown beside the
// active one in split view, or -1.
public sealed record Session(IReadOnlyList<SessionTab> Tabs, int Active, int Split = -1)
{
    public static Session? Load(string path) => Files.ReadJson<Session>(path) is { Tabs.Count: > 0 } s
        ? s with
        {
            Active = Math.Clamp(s.Active, 0, s.Tabs.Count - 1),
            Split = s.Split >= 0 && s.Split < s.Tabs.Count && s.Split != Math.Clamp(s.Active, 0, s.Tabs.Count - 1) ? s.Split : -1,
        }
        : null;

    public void Save(string path) => Files.WriteJson(path, this);
}
