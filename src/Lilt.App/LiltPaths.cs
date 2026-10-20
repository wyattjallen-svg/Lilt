namespace Lilt.App;

// Everything Lilt writes lives under one per-user folder (default %LOCALAPPDATA%\Lilt).
sealed record LiltPaths(string Root)
{
    static string? root;

    public string WebView2 => Path.Combine(Root, "WebView2");
    public string Theme => Path.Combine(Root, "theme.json");
    public string NewTab => Path.Combine(Root, "newtab");
    public string UserContent => Path.Combine(Root, "user");
    public string Session => Path.Combine(Root, "session.json");
    public string Bookmarks => Path.Combine(Root, "bookmarks.json");
    public string History => Path.Combine(Root, "history.json");
    public string Extensions => Path.Combine(Root, "Extensions");
    public static string Bundled => Path.Combine(AppContext.BaseDirectory, "Themes");
    static string BundledNewTab => Path.Combine(AppContext.BaseDirectory, "NewTab");

    public static LiltPaths Resolve(string? overrideRoot)
    {
        root = Path.GetFullPath(overrideRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lilt"));
        Directory.CreateDirectory(root);
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
            foreach (var name in new[] { "theme.schema.json", "minimal.json", "glass.json", "light.json" }) CopyIfDifferent(name);
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

    public static void LogError(Exception e)
    {
        try
        {
            var dir = Path.Combine(root ?? Path.GetTempPath(), "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "error.log"), $"{DateTimeOffset.Now:O} {e}\n\n");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
