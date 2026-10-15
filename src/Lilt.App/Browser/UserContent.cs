using Lilt.Core.Customization;
using Microsoft.UI.Dispatching;
using Microsoft.Web.WebView2.Core;

namespace Lilt.App.Browser;

// Per-site user CSS and JS from %LOCALAPPDATA%\Lilt\user. Registered as document-start scripts on every view;
// changes on disk re-register them (they take effect on the next navigation).
sealed class UserContent : IDisposable
{
    readonly string folder;
    readonly DispatcherQueueTimer debounce;
    readonly Dictionary<CoreWebView2, List<string>> registered = [];
    readonly FileSystemWatcher? watcher;

    public UserContent(string folder, DispatcherQueue queue)
    {
        this.folder = folder;
        Last = Load();
        debounce = queue.CreateTimer();
        debounce.Interval = TimeSpan.FromMilliseconds(250);
        debounce.IsRepeating = false;
        debounce.Tick += (_, _) =>
        {
            Last = Load();
            Changed?.Invoke();
        };
        if (!Directory.Exists(folder)) return;
        watcher = new FileSystemWatcher(folder)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };
        FileSystemEventHandler poke = (_, _) => queue.TryEnqueue(() =>
        {
            debounce.Stop();
            debounce.Start();
        });
        watcher.Changed += poke;
        watcher.Created += poke;
        watcher.Deleted += poke;
        watcher.Renamed += (s, e) => poke(s, e);
        watcher.EnableRaisingEvents = true;
    }

    public UserContentBuild Last { get; private set; }
    public string Folder => folder;
    public event Action? Changed;

    UserContentBuild Load()
    {
        var files = new List<UserFile>();
        try
        {
            if (Directory.Exists(folder))
                foreach (var path in Directory.EnumerateFiles(folder))
                {
                    var info = new FileInfo(path);
                    if (info.Extension.ToLowerInvariant() is not (".css" or ".js")) continue;
                    files.Add(info.Length > UserContentScripts.MaxFileBytes
                        ? new UserFile(info.Name, new string(' ', UserContentScripts.MaxFileBytes + 1))
                        : new UserFile(info.Name, File.ReadAllText(path)));
                }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            LiltPaths.LogError(e);
        }
        return UserContentScripts.Build(files);
    }

    public async Task ApplyAsync(CoreWebView2 core)
    {
        if (registered.Remove(core, out var old))
            foreach (var id in old) core.RemoveScriptToExecuteOnDocumentCreated(id);
        var ids = new List<string>();
        registered[core] = ids;
        foreach (var script in Last.Scripts)
            ids.Add(await core.AddScriptToExecuteOnDocumentCreatedAsync(script));
    }

    public void Forget(CoreWebView2 core) => registered.Remove(core);

    public void Dispose()
    {
        watcher?.Dispose();
        debounce.Stop();
    }
}
