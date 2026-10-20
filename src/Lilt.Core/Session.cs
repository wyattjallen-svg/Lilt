namespace Lilt.Core;

public sealed record SessionTab(string Url, string Title);

// Open tabs, saved on every change and restored on the next start.
public sealed record Session(IReadOnlyList<SessionTab> Tabs, int Active)
{
    public static Session? Load(string path) => Files.ReadJson<Session>(path) is { Tabs.Count: > 0 } s
        ? s with { Active = Math.Clamp(s.Active, 0, s.Tabs.Count - 1) }
        : null;

    public void Save(string path) => Files.WriteJson(path, this);
}
