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
        if (!keepManager) FillManager();
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

    // Middle-click opens a bookmark in a new tab, behind this one.
    void Bookmark_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: Bookmark b } element) return;
        if (e.GetCurrentPoint(element).Properties.PointerUpdateKind != Microsoft.UI.Input.PointerUpdateKind.MiddleButtonPressed) return;
        e.Handled = true;
        AddTab(b.Url, activate: false);
    }

    void BookmarkList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not Bookmark b) return;
        menuBookmark = b;
        var inFolder = bookmarks.Items.Count(x => x.Folder == b.Folder);
        BookmarkRemoveFolder.Text = $"Remove the “{b.Folder}” folder ({inFolder})";
        BookmarkRemoveFolder.Visibility = b.Folder.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        BookmarkMenu.ShowAt(BookmarkList, new FlyoutShowOptions { Position = e.GetPosition(BookmarkList) });
        e.Handled = true;
    }

    void BookmarkNewTab_Click(object sender, RoutedEventArgs e)
    {
        if (menuBookmark is { } b) AddTab(b.Url);
    }

    void BookmarkSplit_Click(object sender, RoutedEventArgs e)
    {
        if (menuBookmark is not { } b || active is null) return;
        var tab = new BrowserTab(b.Url);
        tabs.Insert(tabs.IndexOf(active) + 1, tab);
        SplitWith(tab);
    }

    void BookmarkCopy_Click(object sender, RoutedEventArgs e)
    {
        if (menuBookmark is { } b) CopyText(b.Url);
    }

    void BookmarkRemove_Click(object sender, RoutedEventArgs e)
    {
        if (menuBookmark is { } b) RemoveBookmarks([b], $"Removed “{b.Title}”.");
    }

    void BookmarkRemoveFolder_Click(object sender, RoutedEventArgs e)
    {
        if (menuBookmark is not { Folder.Length: > 0 } b) return;
        var list = bookmarks.Items.Where(x => x.Folder == b.Folder).ToList();
        RemoveBookmarks(list, $"Removed the “{b.Folder}” folder ({list.Count} bookmark{(list.Count == 1 ? "" : "s")}).");
    }

    // Every removal can be undone from the notice.
    void RemoveBookmarks(IReadOnlyList<Bookmark> list, string message)
    {
        var places = list.Select(b => (Index: bookmarks.Items.IndexOf(b), Bookmark: b)).Where(p => p.Index >= 0).OrderBy(p => p.Index).ToList();
        if (places.Count == 0) return;
        foreach (var (_, b) in places) bookmarks.Items.Remove(b);
        SaveBookmarks();
        ShowNotice(new(message, NoticeKind.Success, "Undo", () =>
        {
            foreach (var (i, b) in places)
                if (!bookmarks.Contains(b.Url)) bookmarks.Items.Insert(Math.Min(i, bookmarks.Items.Count), b);
            SaveBookmarks();
        }));
    }

    void BookmarkEdit_Click(object sender, RoutedEventArgs e)
    {
        if (menuBookmark is { } b) _ = EditBookmarkAsync(b);
    }

    async Task EditBookmarkAsync(Bookmark b)
    {
        var title = new TextBox { Header = "Name", Text = b.Title };
        var address = new TextBox { Header = "Address", Text = b.Url, IsSpellCheckEnabled = false };
        var folder = new TextBox { Header = "Folder (Home = home tab tiles; empty = bookmarks bar)", Text = b.Folder };
        var panel = new StackPanel { Spacing = 10, MinWidth = 360 };
        panel.Children.Add(title);
        panel.Children.Add(address);
        panel.Children.Add(folder);
        if (await AskAsync("Edit bookmark", panel, "Save") != ContentDialogResult.Primary) return;
        var target = AddressResolver.Resolve(address.Text, Engine.Template);
        if (target is not { Kind: AddressKind.Navigate, Uri: { } uri } || !bookmarks.Update(b, title.Text, uri.AbsoluteUri, folder.Text))
        {
            ShowNotice(new("That address isn't a web address, or another bookmark already has it.", NoticeKind.Warning));
            return;
        }
        SaveBookmarks();
    }

    // One dialog at a time; a second request while one is open is ignored.
    async Task<ContentDialogResult> AskAsync(string title, object content, string primary)
    {
        ScaleNow(content);
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = title,
            Content = content,
            PrimaryButtonText = primary,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            RequestedTheme = Root.RequestedTheme,
        };
        try
        {
            return await dialog.ShowAsync();
        }
        catch (Exception)
        {
            return ContentDialogResult.None;
        }
    }

    static void CopyText(string text)
    {
        var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
        data.SetText(text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
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
            if (result.Passwords)
            {
                ImportedPasswords(result.PasswordSites ?? []);
                return;
            }
            var addedList = bookmarks.Merge(result.Bookmarks);
            var added = addedList.Count;
            SaveBookmarks();
            var already = result.Bookmarks.Count - added;
            var notes = string.Join(" ", new[]
            {
                already > 0 ? $"{already} were already saved." : null,
                result.Skipped > 0 ? $"{result.Skipped} skipped (duplicates or non-web links)." : null,
            }.Where(s => s is not null));
            ShowNotice(new($"Imported {added} bookmark{(added == 1 ? "" : "s")} from {label} ({result.Format}). {notes}".Trim(),
                result.Bookmarks.Count == 0 ? NoticeKind.Warning : NoticeKind.Success, added > 0 ? "Undo" : null, added > 0 ? () =>
                {
                    foreach (var b in addedList) bookmarks.Items.Remove(b);
                    SaveBookmarks();
                } : null));
        }
        catch (Exception e)
        {
            if (e is not (IOException or UnauthorizedAccessException or InvalidDataException or JsonException)) LiltPaths.LogError(e);
            ShowNotice(new($"Couldn't import {label}: {e.Message}", NoticeKind.Error));
        }
    }

    // A password export (Chrome's "Export passwords" CSV and the like) never turns into bookmarks. Lilt can't put
    // passwords into the engine's own password store (WebView2 has no way to), so they go to the password manager.
    // Bookmarks left by importing such a file in an earlier version are found by their exact name and address.
    async void ImportedPasswords(IReadOnlyList<Bookmark> sites)
    {
        var keys = sites.Select(s => (s.Title, s.Url)).ToHashSet();
        var leftovers = bookmarks.Items.Where(b => b.Folder.Length == 0 && keys.Contains((b.Title, b.Url))).ToList();
        if (leftovers.Count > 0)
        {
            ShowNotice(new($"That's a password export, so nothing was imported. {leftovers.Count} of your bookmarks are its sites, from importing it before.",
                NoticeKind.Warning, $"Remove those {leftovers.Count}", () => RemoveBookmarks(leftovers, $"Removed {leftovers.Count} bookmarks that came from the password file.")));
            return;
        }
        var pass = engine.ExtensionsEnabled && Profile is { } profile
            ? (await engine.ListExtensionsAsync(profile)).FirstOrDefault(x => x.Id == ProtonPassId)
            : null;
        ShowNotice(pass is { Options: { } page }
            ? new("That's a password export, not bookmarks, so nothing was imported. Import it in Proton Pass (Settings → Import → Chrome), then delete the file: it holds your passwords in plain text.",
                NoticeKind.Info, "Open Proton Pass", () => AddTab($"chrome-extension://{pass.Id}/{page}#/import"))
            : new("That's a password export, not bookmarks, so nothing was imported. Passwords belong in a password manager: add Proton Pass from Extensions and import the file there, then delete it (it holds your passwords in plain text).",
                NoticeKind.Info, engine.ExtensionsEnabled ? "Extensions" : null, engine.ExtensionsEnabled ? () => Extensions_Click(this, new RoutedEventArgs()) : null));
    }

    async void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        CustomizeFlyout.Hide();
        MainMenu.Hide();
        var text = new TextBlock
        {
            Text = "Forget the pages you've visited: address-bar suggestions and the engine's history. Bookmarks, passwords, sign-ins and open tabs stay.",
            TextWrapping = TextWrapping.Wrap, MaxWidth = 380,
        };
        if (await AskAsync("Clear history?", text, "Clear") == ContentDialogResult.Primary) ClearHistory();
    }

    // Browsers keep these files open while running, so read without asking for exclusive access.
    static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
