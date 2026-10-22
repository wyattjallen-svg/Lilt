using System.Text.RegularExpressions;

namespace Lilt.Core;

// A key combination like "Ctrl+Shift+T". Key is a letter, digit, F1-F24, a named key, or punctuation.
public sealed partial record Gesture(bool Ctrl, bool Shift, bool Alt, string Key)
{
    static readonly string[] named = ["Tab", "Space", "Enter", "Escape", "Left", "Right", "Up", "Down", "Home", "End", "PageUp", "PageDown", "Delete", "Insert", "Backspace"];
    static readonly string punctuation = ",./\\;'[]-=`";

    public static bool TryParse(string? text, out Gesture gesture)
    {
        gesture = new(false, false, false, "");
        if (string.IsNullOrWhiteSpace(text) || text.Length > 40) return false;
        bool ctrl = false, shift = false, alt = false;
        var parts = text.Trim().Split('+');
        // "Ctrl++" style: an empty last part after a trailing '+' would be the plus key; not supported.
        for (var i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].Trim().ToLowerInvariant())
            {
                case "ctrl" or "control": ctrl = true; break;
                case "shift": shift = true; break;
                case "alt": alt = true; break;
                default: return false;
            }
        }
        var key = parts[^1].Trim();
        if (key.Length == 1 && char.IsAsciiLetterOrDigit(key[0])) key = key.ToUpperInvariant();
        else if (key.Length == 1 && punctuation.Contains(key[0])) { }
        else if (FKey().IsMatch(key)) key = key.ToUpperInvariant();
        else if (named.FirstOrDefault(n => n.Equals(key, StringComparison.OrdinalIgnoreCase)) is { } n) key = n;
        else return false;
        // Plain letters, digits and punctuation without Ctrl or Alt would swallow typing.
        var typing = key.Length == 1 || key is "Space" or "Enter" or "Backspace" or "Delete";
        if (typing && !ctrl && !alt) return false;
        gesture = new(ctrl, shift, alt, key);
        return true;
    }

    public override string ToString() => string.Concat(Ctrl ? "Ctrl+" : "", Shift ? "Shift+" : "", Alt ? "Alt+" : "", Key);

    // KeyboardEvent.code for this key, which doesn't change with Shift (Shift+, is still "Comma").
    public string Code => Key switch
    {
        { Length: 1 } k when char.IsAsciiLetter(k[0]) => "Key" + k,
        { Length: 1 } k when char.IsAsciiDigit(k[0]) => "Digit" + k,
        "," => "Comma", "." => "Period", "/" => "Slash", "\\" => "Backslash", ";" => "Semicolon", "'" => "Quote",
        "[" => "BracketLeft", "]" => "BracketRight", "-" => "Minus", "=" => "Equal", "`" => "Backquote",
        "Left" => "ArrowLeft", "Right" => "ArrowRight", "Up" => "ArrowUp", "Down" => "ArrowDown",
        _ => Key,
    };

    // Windows virtual-key code (US layout for punctuation).
    public int VirtualKey => Key switch
    {
        { Length: 1 } k when char.IsAsciiLetterOrDigit(k[0]) => k[0],
        "," => 188, "." => 190, "/" => 191, "\\" => 220, ";" => 186, "'" => 222, "[" => 219, "]" => 221, "-" => 189, "=" => 187, "`" => 192,
        "Tab" => 9, "Space" => 32, "Enter" => 13, "Escape" => 27, "Left" => 37, "Up" => 38, "Right" => 39, "Down" => 40,
        "Home" => 36, "End" => 35, "PageUp" => 33, "PageDown" => 34, "Delete" => 46, "Insert" => 45, "Backspace" => 8,
        _ when Key.StartsWith('F') => 111 + int.Parse(Key[1..]),
        _ => 0,
    };

    [GeneratedRegex("^[Ff]([1-9]|1[0-9]|2[0-4])$")]
    private static partial Regex FKey();
}

// Commands and their default shortcuts. "Reserved" commands are Lilt's even inside pages (like Chrome's Ctrl+T);
// the rest go to the page first and only reach Lilt if the page doesn't use the key itself.
public static class Commands
{
    public static readonly IReadOnlyDictionary<string, string[]> Defaults = new Dictionary<string, string[]>
    {
        ["newTab"] = ["Ctrl+T"],
        ["reopenTab"] = ["Ctrl+Shift+T"],
        ["closeTab"] = ["Ctrl+W"],
        ["nextTab"] = ["Ctrl+Tab"],
        ["prevTab"] = ["Ctrl+Shift+Tab"],
        ["address"] = ["Ctrl+L", "Alt+D", "F6"],
        ["back"] = ["Alt+Left"],
        ["forward"] = ["Alt+Right"],
        ["reload"] = ["F5", "Ctrl+R"],
        ["home"] = ["Alt+Home"],
        ["bookmark"] = ["Ctrl+D"],
        ["sidebar"] = ["Ctrl+Shift+B"],
        ["menu"] = ["Alt+F"],
        ["customize"] = ["Ctrl+Shift+,"],
        ["palette"] = ["Ctrl+K", "Ctrl+Shift+P"],
        ["split"] = ["Ctrl+Shift+S"],
        ["media"] = ["Ctrl+Shift+M"],
        ["visualizer"] = ["Ctrl+Shift+U"],
        ["pip"] = ["Alt+P"],
        ["bookmarks"] = ["Ctrl+Shift+O"],
        ["screenshot"] = ["Ctrl+Shift+X"],
        ["workspace"] = ["Ctrl+Shift+E"],
        ["panel"] = ["F4"],
        ["toPanel"] = [],
        ["sleepTabs"] = [],
        // Interface size has no keys by default: Ctrl+Alt is AltGr on many keyboards, which types characters.
        ["biggerUi"] = [],
        ["smallerUi"] = [],
        ["resetUi"] = [],
        ["logs"] = [],
    };

    public static readonly HashSet<string> Reserved = ["newTab", "reopenTab", "closeTab", "nextTab", "prevTab", "address", "palette"];

    public static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>
    {
        ["newTab"] = "New tab", ["reopenTab"] = "Reopen closed tab", ["closeTab"] = "Close tab", ["nextTab"] = "Next tab",
        ["prevTab"] = "Previous tab", ["address"] = "Go to the address bar", ["back"] = "Back", ["forward"] = "Forward",
        ["reload"] = "Reload", ["home"] = "Home", ["bookmark"] = "Bookmark this page", ["sidebar"] = "Show or hide the sidebar",
        ["menu"] = "Open the menu", ["customize"] = "Customize", ["palette"] = "Command palette", ["split"] = "Split view",
        ["media"] = "Show or hide the media player", ["visualizer"] = "Show or hide the visualizer", ["pip"] = "Picture in picture",
        ["bookmarks"] = "Manage bookmarks", ["screenshot"] = "Screenshot of the whole page", ["workspace"] = "Next workspace",
        ["panel"] = "Show or hide the panel", ["toPanel"] = "Move this tab's video to the panel",
        ["sleepTabs"] = "Put background tabs to sleep", ["biggerUi"] = "Make Lilt bigger", ["smallerUi"] = "Make Lilt smaller",
        ["resetUi"] = "Lilt at 100%", ["logs"] = "Logs and crashes",
    };
}

// command -> gestures. Equality is by content so themes compare by value.
public sealed record ShortcutMap(IReadOnlyDictionary<string, Gesture[]> Map)
{
    public static ShortcutMap Default { get; } = new(Commands.Defaults.ToDictionary(p => p.Key, p => p.Value.Select(Parse).ToArray()));

    static Gesture Parse(string s) => Gesture.TryParse(s, out var g) ? g : throw new FormatException(s);

    public Gesture[] For(string command) => Map.TryGetValue(command, out var g) ? g : [];

    // Which command a key press means, if any.
    public string? Find(bool ctrl, bool shift, bool alt, int virtualKey) =>
        Map.FirstOrDefault(p => p.Value.Any(g => g.Ctrl == ctrl && g.Shift == shift && g.Alt == alt && g.VirtualKey == virtualKey)).Key;

    public bool Equals(ShortcutMap? other) =>
        other is not null && Map.Count == other.Map.Count && Map.All(p => other.Map.TryGetValue(p.Key, out var g) && g.SequenceEqual(p.Value));

    public override int GetHashCode() => Map.Count;
}
