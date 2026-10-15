using Lilt.Core.Theming;
using Microsoft.UI.Dispatching;

namespace Lilt.App.Shell;

// Loads the theme file and reloads it when it changes on disk. No polling: FileSystemWatcher plus a short debounce,
// because editors often save in several writes or by renaming a temp file over the original.
sealed class ThemeService : IDisposable
{
    readonly DispatcherQueue queue;
    readonly DispatcherQueueTimer debounce;
    FileSystemWatcher? watcher;
    int retries;

    public ThemeService(string? filePath, DispatcherQueue queue)
    {
        FilePath = filePath;
        this.queue = queue;
        debounce = queue.CreateTimer();
        debounce.Interval = TimeSpan.FromMilliseconds(200);
        debounce.IsRepeating = false;
        debounce.Tick += (_, _) => Reload();
    }

    public string? FilePath { get; }
    public Theme Current { get; private set; } = Theme.Default;
    public event Action<ThemeLoadResult>? Reloaded;

    public ThemeLoadResult Load()
    {
        if (FilePath is null)
            return new ThemeLoadResult(Theme.Default, [new ThemeIssue("", "Lilt's data folder isn't writable; using the built-in theme.", IsError: true)], true);
        var result = ThemeParser.LoadFile(FilePath);
        if (!result.Failed) Current = result.Theme;
        return result;
    }

    public void Watch()
    {
        if (FilePath is null || Path.GetDirectoryName(FilePath) is not { } dir || !Directory.Exists(dir)) return;
        watcher = new FileSystemWatcher(dir, Path.GetFileName(FilePath))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
        };
        watcher.Changed += Poke;
        watcher.Created += Poke;
        watcher.Renamed += Poke;
        watcher.EnableRaisingEvents = true;
    }

    public void Reload()
    {
        var result = Load();
        if (result.ReadError && retries++ < 3)
        {
            debounce.Start();
            return;
        }
        retries = 0;
        Reloaded?.Invoke(result);
    }

    void Poke(object sender, FileSystemEventArgs e) => queue.TryEnqueue(() =>
    {
        debounce.Stop();
        debounce.Start();
    });

    public void Dispose()
    {
        watcher?.Dispose();
        debounce.Stop();
    }
}
