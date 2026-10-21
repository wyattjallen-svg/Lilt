using System.Collections.ObjectModel;
using Lilt.Core.Bookmarks;
using Lilt.Core.Navigation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Lilt.App;

// The bookmark manager (Ctrl+Shift+O): every bookmark in one place to search, sort, move between folders, reorder
// (drag them, in "Your order") and remove in bulk. Every change can be undone from the notice.
public sealed partial class MainWindow
{
    readonly ObservableCollection<Bookmark> managerItems = [];
    // The folder list's "All bookmarks" entry (folders are plain text, so this can't clash with one).
    const string allFolders = "\u0001";
    string managerFolder = allFolders;
    bool keepManager, fillingFolders;

    bool ManagerShown => BookmarksOverlay.Visibility == Visibility.Visible;

    void ShowManager()
    {
        if (ManagerShown)
        {
            CloseManager();
            return;
        }
        ClosePeek();
        BookmarksOverlay.Visibility = Visibility.Visible;
        ManagerList.ItemsSource = managerItems;
        FillManager();
        ManagerSearch.Focus(FocusState.Programmatic);
    }

    void CloseManager()
    {
        BookmarksOverlay.Visibility = Visibility.Collapsed;
        managerItems.Clear();
        active?.View?.Focus(FocusState.Programmatic);
    }

    static string FolderName(string folder) => folder.Length == 0 ? "Bookmarks bar" : folder == "Home" ? "Home (tiles)" : folder;

    IReadOnlyList<Bookmark> ManagerSelection => ManagerList.SelectedItems.Cast<Bookmark>().ToList();

    void FillManager()
    {
        if (!ManagerShown) return;
        var cmp = StringComparer.CurrentCultureIgnoreCase;
        var folders = bookmarks.Items.GroupBy(b => b.Folder).OrderBy(g => g.Key.Length > 0).ThenBy(g => g.Key, cmp).ToList();
        if (managerFolder != allFolders && !folders.Any(g => g.Key == managerFolder)) managerFolder = allFolders;
        fillingFolders = true;
        ManagerFolders.Items.Clear();
        ManagerFolders.Items.Add(new ListViewItem { Content = $"All bookmarks  ·  {bookmarks.Items.Count}", Tag = allFolders });
        foreach (var g in folders) ManagerFolders.Items.Add(new ListViewItem { Content = $"{FolderName(g.Key)}  ·  {g.Count()}", Tag = g.Key });
        ManagerFolders.SelectedItem = ManagerFolders.Items.Cast<ListViewItem>().First(i => (string)i.Tag == managerFolder);
        fillingFolders = false;

        var q = ManagerSearch.Text.Trim();
        var list = bookmarks.Items.Where(b => (managerFolder == allFolders || b.Folder == managerFolder)
            && (q.Length == 0 || b.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase) || b.Url.Contains(q, StringComparison.OrdinalIgnoreCase)));
        var sort = (ManagerSort.SelectedItem as ComboBoxItem)?.Tag as string ?? "order";
        list = sort switch
        {
            "name" => list.OrderBy(b => b.Title, cmp),
            "address" => list.OrderBy(b => UrlPolicy.DisplayHost(b.Url), cmp).ThenBy(b => b.Url),
            "folder" => list.OrderBy(b => b.Folder.Length > 0).ThenBy(b => b.Folder, cmp).ThenBy(b => b.Title, cmp),
            _ => list,
        };
        managerItems.Clear();
        foreach (var b in list) managerItems.Add(b);
        // Dragging reorders for good, so it's only on while the list shows your own order, unfiltered.
        ManagerList.CanReorderItems = ManagerList.CanDragItems = sort == "order" && q.Length == 0;
        ManagerSortFolderButton.IsEnabled = managerItems.Count > 1;
        ManagerSelectionChanged();
    }

    void ManagerSelectionChanged()
    {
        var n = ManagerList.SelectedItems.Count;
        ManagerOpenButton.IsEnabled = ManagerMove.IsEnabled = ManagerRemoveButton.IsEnabled = n > 0;
        ManagerEditButton.IsEnabled = n == 1;
        ManagerCount.Text = n > 0 ? $"{n} of {managerItems.Count} selected" : Plural(managerItems.Count, "bookmark");
    }

    void ManagerOpen(IReadOnlyList<Bookmark> list)
    {
        if (list.Count == 0) return;
        CloseManager();
        if (list.Count == 1)
        {
            AddTab(list[0].Url);
            return;
        }
        foreach (var b in list) AddTab(b.Url, activate: false);
        ShowNotice(new($"Opened {Plural(list.Count, "tab")} behind this one.", NoticeKind.Info));
    }

    void MoveBookmarks(IReadOnlyList<Bookmark> list, string folder)
    {
        var before = bookmarks.Items.ToList();
        var moved = list.Count(b => b.Folder != folder && bookmarks.Update(b, b.Title, b.Url, folder));
        if (moved == 0) return;
        SaveBookmarks();
        ShowNotice(new($"Moved {Plural(moved, "bookmark")} to {FolderName(folder)}.", NoticeKind.Success, "Undo", () => RestoreBookmarks(before)));
    }

    void RestoreBookmarks(List<Bookmark> before)
    {
        bookmarks.Items.Clear();
        bookmarks.Items.AddRange(before);
        SaveBookmarks();
    }

    void ManagerSearch_TextChanged(object sender, TextChangedEventArgs e) => FillManager();
    void ManagerSort_SelectionChanged(object sender, SelectionChangedEventArgs e) => FillManager();
    void ManagerList_SelectionChanged(object sender, SelectionChangedEventArgs e) => ManagerSelectionChanged();
    void ManagerClose_Click(object sender, RoutedEventArgs e) => CloseManager();
    void BookmarkManage_Click(object sender, RoutedEventArgs e) => ShowManager();
    void BookmarksOverlay_Tapped(object sender, TappedRoutedEventArgs e) => CloseManager();
    void ManagerOpen_Click(object sender, RoutedEventArgs e) => ManagerOpen(ManagerSelection);

    void ManagerFolders_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (fillingFolders || ManagerFolders.SelectedItem is not ListViewItem { Tag: string folder }) return;
        managerFolder = folder;
        FillManager();
    }

    void ManagerEdit_Click(object sender, RoutedEventArgs e)
    {
        if (ManagerSelection is [var b]) _ = EditBookmarkAsync(b);
    }

    void ManagerRemove_Click(object sender, RoutedEventArgs e)
    {
        var list = ManagerSelection;
        if (list.Count > 0) RemoveBookmarks(list, list.Count == 1 ? $"Removed “{list[0].Title}”." : $"Removed {Plural(list.Count, "bookmark")}.");
    }

    void ManagerList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is Bookmark b) ManagerOpen([b]);
    }

    void ManagerList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Delete: ManagerRemove_Click(sender, e); break;
            case VirtualKey.Enter: ManagerOpen(ManagerSelection); break;
            case VirtualKey.F2: ManagerEdit_Click(sender, e); break;
            default: return;
        }
        e.Handled = true;
    }

    void BookmarksOverlay_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape) return;
        e.Handled = true;
        CloseManager();
    }

    // The list shows a slice of all bookmarks: the slice's new order goes back into the slots its bookmarks held.
    void ManagerList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs e)
    {
        var slots = managerItems.Select(b => bookmarks.Items.IndexOf(b)).Order().ToList();
        if (slots.Contains(-1)) return;
        for (var i = 0; i < slots.Count; i++) bookmarks.Items[slots[i]] = managerItems[i];
        keepManager = true;
        SaveBookmarks();
        keepManager = false;
    }

    void ManagerSortFolder_Click(object sender, RoutedEventArgs e)
    {
        var before = bookmarks.Items.ToList();
        var slots = managerItems.Select(b => bookmarks.Items.IndexOf(b)).Order().ToList();
        var sorted = managerItems.OrderBy(b => b.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
        for (var i = 0; i < slots.Count; i++) bookmarks.Items[slots[i]] = sorted[i];
        SaveBookmarks();
        ShowNotice(new("Sorted by name.", NoticeKind.Success, "Undo", () => RestoreBookmarks(before)));
    }

    void ManagerMoveMenu_Opening(object? sender, object e)
    {
        ManagerMoveMenu.Items.Clear();
        var folders = bookmarks.Items.Select(b => b.Folder).Append("").Append("Home").Distinct()
            .OrderBy(f => f.Length > 0).ThenBy(f => f, StringComparer.CurrentCultureIgnoreCase);
        foreach (var folder in folders)
        {
            var f = folder;
            var item = new MenuFlyoutItem { Text = FolderName(f) };
            item.Click += (_, _) => MoveBookmarks(ManagerSelection, f);
            ManagerMoveMenu.Items.Add(item);
        }
        ManagerMoveMenu.Items.Add(new MenuFlyoutSeparator());
        var add = new MenuFlyoutItem { Text = "New folder…", Icon = new FontIcon { Glyph = "" } };
        add.Click += async (_, _) =>
        {
            var list = ManagerSelection;
            var box = new TextBox { PlaceholderText = "Folder name", MaxLength = 80 };
            if (await AskAsync("New folder", box, "Move here") == ContentDialogResult.Primary && box.Text.Trim().Trim('/') is { Length: > 0 } name)
                MoveBookmarks(list, name);
        };
        ManagerMoveMenu.Items.Add(add);
    }
}
