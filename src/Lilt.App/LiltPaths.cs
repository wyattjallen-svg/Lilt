using Lilt.Core.Diagnostics;

namespace Lilt.App;

// Everything Lilt writes lives under one per-user folder (default %LOCALAPPDATA%\Lilt).
sealed record LiltPaths(string Root)
{
    static string? root;

    // Lilt's log (about:logs); until the data folder is known it goes to a temporary folder.
    public static LogBook Log { get; private set; } = new(Path.Combine(Path.GetTempPath(), "Lilt", "logs"));

    public string WebView2 => Path.Combine(Root, "WebView2");
    public string Theme => Path.Combine(Root, "theme.json");
    public string NewTab => Path.Combine(Root, "newtab");
    public string UserContent => Path.Combine(Root, "user");
    public string Session => Path.Combine(Root, "session.json");
    public string Bookmarks => Path.Combine(Root, "bookmarks.json");
    public string History => Path.Combine(Root, "history.json");
    // Present while Lilt runs; still there at the next start only if the last run didn't close cleanly.
    public string Running => Path.Combine(Root, "running");
    public string Logs => Path.Combine(Root, "logs");
    // Your own theme presets ("Save as preset" in Customize), one theme file each.
    public string Presets => Path.Combine(Root, "themes");
    public string Extensions => Path.Combine(Root, "Extensions");
    public static string Bundled => Path.Combine(AppContext.BaseDirectory, "Themes");
    static string BundledNewTab => Path.Combine(AppContext.BaseDirectory, "NewTab");

    public static LiltPaths Resolve(string? overrideRoot)
    {
        root = Path.GetFullPath(overrideRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lilt"));
        Directory.CreateDirectory(root);
        Log = new LogBook(Path.Combine(root, "logs"));
        return new LiltPaths(root);
    }

    // Creates theme.json from the bundled default on first run and keeps the schema and samples next to it.
    // Returns null when the folder can't be written; the caller falls back to built-in defaults.
    public string? PrepareTheme(string? overridePath)
    {
        if (overridePath is not null) return Path.GetFullPath(overridePath);
        try
        {
            if (!File.Exists(Theme) || IsOldDefault(Theme)) File.Copy(Path.Combine(Bundled, "minimal.json"), Theme, overwrite: true);
            foreach (var file in Directory.EnumerateFiles(Bundled, "*.json")) CopyIfDifferent(Path.GetFileName(file));
            return Theme;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            LogError(e);
            return File.Exists(Theme) ? Theme : null;
        }
    }

    // The home tab's files are Lilt's and are refreshed on every start; custom.css and custom.js are yours and never overwritten.
    public string PrepareNewTab()
    {
        try
        {
            Directory.CreateDirectory(NewTab);
            foreach (var file in Directory.EnumerateFiles(BundledNewTab))
            {
                var name = Path.GetFileName(file);
                var to = Path.Combine(NewTab, name);
                var mine = name.StartsWith("custom.", StringComparison.OrdinalIgnoreCase);
                if (!File.Exists(to) || (!mine && File.ReadAllText(to) != File.ReadAllText(file))) File.Copy(file, to, overwrite: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            LogError(e);
        }
        return NewTab;
    }

    public string PrepareUserContent()
    {
        try
        {
            Directory.CreateDirectory(UserContent);
            var readme = Path.Combine(UserContent, "README.txt");
            if (!File.Exists(readme))
                File.WriteAllText(readme, """
                    Per-site styles and scripts for Lilt.

                    example.com.css   CSS applied to example.com and its subdomains
                    example.com.js    JavaScript run at document start on example.com and its subdomains
                    all.css / all.js  applied to every http and https page

                    Files are re-read when saved; reload the page to see changes.
                    Scripts run with the page's own permissions and have no access to Lilt or your files.
                    Each file can be up to 512 KB.
                    """);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            LogError(e);
        }
        return UserContent;
    }

    // theme.json files that are still exactly an earlier version's default (never edited) move to the current default look.
    static readonly string[] oldDefaults =
    [
        "b8104c5137dc624a37bc760a32e7e166b789d0ed1c0489a0b4a4160c37eb5154",
        "e416fcf2ff5402e00f5000cd2a78d70271e3e1c8d8959dd2bdfd1e4b5505899e",
        "72c0940ae5a5e0a8ef1f69fc67949777a96ed9911e71d651e028b1660ccc7456",
        "c3b3ba570962834ee00ea6821504a0cc559a6c89af5cc17a289c4fd843b07718",
        "a35ba193afdc14a18de30c5c11440519e1e00499173e30dc8ed5e023005dce76",
        "524cdd2a4162e02737834b62641af1d1fa91205454e473c7e72bc6c5d1dce28b",
        "b5f5fa4d8d7216d7c16e8e38c1513f393c878c200913275d80177886b4022663",
    ];

    static bool IsOldDefault(string path)
    {
        var text = File.ReadAllText(path).Replace("\r\n", "\n");
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
        return oldDefaults.Contains(hash);
    }

    void CopyIfDifferent(string name)
    {
        var from = Path.Combine(Bundled, name);
        var to = Path.Combine(Root, name);
        if (!File.Exists(to) || File.ReadAllText(to) != File.ReadAllText(from)) File.Copy(from, to, overwrite: true);
    }

    public static void LogError(Exception e, string source = "lilt") =>
        Log.Write(LogLevel.Error, source, $"{e.GetType().Name}: {e.Message}", e.ToString());
}
