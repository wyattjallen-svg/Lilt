using System.Text.Json;

namespace Lilt.Core;

// What a tab reports about its media (from Lilt's isolated page script). Everything is page-supplied text,
// so it's length-limited and only shown as text; Artwork is kept only if it's an http(s) URL.
public sealed record MediaInfo(bool Playing, string Title, string Artist, string Album, string? Artwork, bool Video,
    double Position, double Duration, double Rate, bool Muted, bool PictureInPicture)
{
    public static MediaInfo? Parse(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object || e.TryGetProperty("none", out _)) return null;
        string Text(string name, int max) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? Clip(v.GetString()!, max) : "";
        double Num(string name, double max) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d) ? Math.Clamp(d, 0, max) : 0;
        bool Flag(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
        var art = Text("art", 2000);
        var artwork = Uri.TryCreate(art, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp) ? u.AbsoluteUri : null;
        return new MediaInfo(Flag("playing"), Text("title", 300), Text("artist", 200), Text("album", 200), artwork, Flag("video"),
            Num("pos", 1e7), Num("dur", 1e7), Num("rate", 16) is var r && r > 0 ? r : 1, Flag("muted"), Flag("pip"));
    }

    static string Clip(string s, int max)
    {
        s = string.Concat(s.Where(c => !char.IsControl(c))).Trim();
        return s.Length <= max ? s : s[..max];
    }

    public static string Clock(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }
}
