using System.Text.Json;
using Lilt.App.Browser;
using Lilt.Core;
using Lilt.Core.Bookmarks;
using Lilt.Core.Navigation;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

namespace Lilt.App;

// Bookmarks: a flat list with folder labels, imported from other browsers or files, and shown as tiles on the home tab.
public sealed partial class MainWindow
{
    readonly BookmarkStore bookmarks;
    DispatcherQueueTimer? homeTimer;
    string lastHomeData = "";
    Bookmark? menuBookmark;

    // Chromium-based browsers keep a Bookmarks JSON file per profile under their "User Data" folder.
    static readonly (string Name, string Folder)[] browsers =
    [
        ("Chrome", @"Google\Chrome\User Data"),
        ("Edge", @"Microsoft\Edge\User Data"),
        ("Helium", @"imput\Helium\User Data"),
        ("Brave", @"BraveSoftware\Brave-Browser\User Data"),
        ("Vivaldi", @"Vivaldi\User Data"),
        ("Chromium", @"Chromium\User Data"),
    ];

    void UpdateStar()
    {
        var url = active?.Url;
        var web = Uri.TryCreate(url, UriKind.Absolute, out var u) && UrlPolicy.IsWeb(u) && u.Host != BrowserEngine.NewTabHost;
        var saved = web && bookmarks.Contains(url);
        StarButton.IsEnabled = web;
        StarIcon.Glyph = saved ? "" : "";
        var tip = saved ? "Remove bookmark (Ctrl+D)" : "Bookmark this page (Ctrl+D)";
        ToolTipService.SetToolTip(StarButton, tip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(StarButton, tip);
    }

    void ToggleBookmark()
    {
        if (active is null || !StarButton.IsEnabled) return;
        if (!bookmarks.Remove(active.Url)) bookmarks.Add(active.Title, active.Url);
        SaveBookmarks();
    }

    void SaveBookmarks()
    {
        try
        {
            bookmarks.Save();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            LiltPaths.LogError(e);
            ShowNotice(new($"Bookmarks couldn't be saved: {e.Message}", NoticeKind.Error));
        }
        RefreshBookmarks();
        UpdateStar();
    }

    void RefreshBookmarks()
    {
        ShowBookmarks();
        WriteHomeData();
    }

    void ShowBookmarks()
    {
        var q = BookmarkSearch.Text.Trim();
        BookmarkList.ItemsSource = q.Length == 0
            ? bookmarks.Items.ToList()
            : bookmarks.Items.Where(b => b.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase) || b.Url.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    void Star_Click(object sender, RoutedEventArgs e) => ToggleBookmark();
    void BookmarkSearch_TextChanged(object sender, TextChangedEventArgs e) => ShowBookmarks();

    void BookmarkList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is Bookmark b) Navigate(b.Url);
    }

    void BookmarkList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not Bookmark b) return;
        menuBookmark = b;
        BookmarkMenu.ShowAt(BookmarkList, new FlyoutShowOptions { Position = e.GetPosition(BookmarkList) });
        e.Handled = true;
    }

    void BookmarkNewTab_Click(object sender, RoutedEventArgs e)
    {
        if (menuBookmark is { } b) AddTab(b.Url);
    }

    void BookmarkRemove_Click(object sender, RoutedEventArgs e)
    {
        if (menuBookmark is { } b && bookmarks.Remove(b.Url)) SaveBookmarks();
    }

    // ---- Import ----

    void ImportFlyout_Opening(object sender, object e) => FillImportMenu(ImportFlyout.Items);

    void FillImportMenu(IList<MenuFlyoutItemBase> items)
    {
        items.Clear();
        var file = new MenuFlyoutItem { Text = "From a file (HTML, CSV or Bookmarks)…" };
        file.Click += (_, _) => PickAndImport();
        items.Add(file);
        var found = FindBrowserProfiles();
        if (found.Count > 0) items.Add(new MenuFlyoutSeparator());
        foreach (var (label, path) in found)
        {
            var item = new MenuFlyoutItem { Text = label };
            item.Click += (_, _) => Import(path, label);
            items.Add(item);
        }
    }

    static List<(string Label, string Path)> FindBrowserProfiles()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var found = new List<(string, string)>();
        foreach (var (name, folder) in browsers)
        {
            var root = Path.Combine(local, folder);
            if (!Directory.Exists(root)) continue;
            var names = ProfileNames(Path.Combine(root, "Local State"));
            try
            {
                foreach (var dir in Directory.GetDirectories(root).Order())
                {
                    var id = Path.GetFileName(dir);
                    if (id != "Default" && !id.StartsWith("Profile ", StringComparison.Ordinal)) continue;
                    var file = Path.Combine(dir, "Bookmarks");
                    if (File.Exists(file)) found.Add(($"{name} · {names.GetValueOrDefault(id, id)}", file));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return found;
    }

    // Profile display names live in "Local State" under profile.info_cache.<folder>.name.
    static Dictionary<string, string> ProfileNames(string localState)
    {
        var names = new Dictionary<string, string>();
        try
        {
            using var doc = JsonDocument.Parse(ReadShared(localState));
            foreach (var p in doc.RootElement.GetProperty("profile").GetProperty("info_cache").EnumerateObject())
                if (p.Value.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } s) names[p.Name] = s;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException) { }
        return names;
    }

    async void PickAndImport()
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            foreach (var type in new[] { ".html", ".htm", ".csv", ".json", "*" }) picker.FileTypeFilter.Add(type);
            if (await picker.PickSingleFileAsync() is { } file) Import(file.Path, file.Name);
        }
        catch (Exception e)
        {
            LiltPaths.LogError(e);
            ShowNotice(new($"The file picker couldn't open: {e.Message}", NoticeKind.Error));
        }
    }

    async void Import(string path, string label)
    {
        try
        {
            if (new FileInfo(path).Length > BookmarkImport.MaxBytes) throw new InvalidDataException("That file is over 20 MB, which is too big for a bookmarks file.");
            var result = await Task.Run(() => BookmarkImport.Parse(ReadShared(path)));
            var added = bookmarks.Merge(result.Bookmarks);
            SaveBookmarks();
            var already = result.Bookmarks.Count - added;
            var notes = string.Join(" ", new[]
            {
                already > 0 ? $"{already} were already saved." : null,
                result.Skipped > 0 ? $"{result.Skipped} skipped (duplicates or non-web links)." : null,
            }.Where(s => s is not null));
            ShowNotice(new($"Imported {added} bookmark{(added == 1 ? "" : "s")} from {label} ({result.Format}). {notes}".Trim(),
                result.Bookmarks.Count == 0 ? NoticeKind.Warning : NoticeKind.Success));
        }
        catch (Exception e)
        {
            if (e is not (IOException or UnauthorizedAccessException or InvalidDataException or JsonException)) LiltPaths.LogError(e);
            ShowNotice(new($"Couldn't import {label}: {e.Message}", NoticeKind.Error));
        }
    }

    // Browsers keep these files open while running, so read without asking for exclusive access.
    static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    // ---- Home tab data ----
    // The home page is a plain local web page with no bridge to Lilt; it reads this file for colors, layout and tiles.

    bool HomeGlass => theme.Home.Glass && SystemBackdrop is not null;

    void WriteHomeData()
    {
        if (homeTimer is null)
        {
            homeTimer = DispatcherQueue.CreateTimer();
            homeTimer.Interval = TimeSpan.FromMilliseconds(300);
            homeTimer.IsRepeating = false;
            homeTimer.Tick += (_, _) => FlushHomeData();
        }
        homeTimer.Stop();
        homeTimer.Start();
    }

    void FlushHomeData()
    {
        var c = theme.Colors;
        var folder = bookmarks.Items.Any(b => b.Folder == "Home") ? "Home" : "";
        var data = new
        {
            home = new { clock = theme.Home.Clock, search = theme.Home.Search, tiles = theme.Home.Tiles, glass = HomeGlass },
            colors = new Dictionary<string, string>
            {
                ["background"] = c.Background.Opaque.ToString(),
                ["text"] = c.Text.ToString(),
                ["textMuted"] = c.TextMuted.ToString(),
                ["surfaceRaised"] = c.SurfaceRaised.ToString(),
                ["border"] = c.Border.ToString(),
                ["accent"] = c.Accent.ToString(),
                ["onAccent"] = c.OnAccent.ToString(),
                ["hover"] = c.Hover.ToString(),
            },
            tiles = bookmarks.Items.Where(b => b.Folder == folder).Take(24).Select(b => new { title = b.Title, url = b.Url }),
        };
        var json = JsonSerializer.Serialize(data, Files.Json);
        if (json == lastHomeData) return;
        try
        {
            Files.WriteAtomic(Path.Combine(paths.NewTab, "lilt-data.json"), json);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            LiltPaths.LogError(e);
            return;
        }
        var first = lastHomeData.Length == 0;
        lastHomeData = json;
        if (first) return;
        foreach (var tab in tabs)
            if (tab.IsHome && tab.Core is { } core) core.Reload();
    }
}
