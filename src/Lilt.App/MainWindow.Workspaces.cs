using Lilt.App.Browser;
using Lilt.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace Lilt.App;

// Workspaces: separate sets of tabs (work, home, a project). Only the current one's tabs are listed; the others keep
// their pages (suspended once idle, like any background tab) and keep playing until you switch back.
public sealed partial class MainWindow
{
    readonly List<string> workspaces = [""];
    readonly Dictionary<string, List<BrowserTab>> stashed = [];
    string workspace = "";

    IEnumerable<BrowserTab> AllTabs => tabs.Concat(stashed.Values.SelectMany(l => l)).Concat(panelTabs);

    string? WorkspaceOf(BrowserTab tab) => tabs.Contains(tab) ? workspace : stashed.FirstOrDefault(p => p.Value.Contains(tab)).Key;

    void SwitchWorkspace(string name, BrowserTab? show = null)
    {
        if (!workspaces.Contains(name)) workspaces.Add(name);
        if (name != workspace)
        {
            if (floating is not null) DockFloating(activate: false);
            split = null;
            var leaving = tabs.ToList();
            active = null;
            tabs.Clear();
            foreach (var t in leaving) Stash(t, workspace);
            workspace = name;
            if (stashed.Remove(name, out var back))
                foreach (var t in back) tabs.Add(t);
            report.Key("workspace", Workspace.Display(name));
        }
        if (tabs.Count == 0) AddTab(BrowserEngine.NewTabUri.AbsoluteUri);
        else Activate(show ?? tabs.MaxBy(t => t.LastActive)!);
        UpdateWorkspaceButton();
        ScheduleSave();
    }

    void Stash(BrowserTab tab, string name)
    {
        tab.IsActive = tab.IsShown = false;
        tab.Workspace = name;
        if (tab.View is { } v) v.Visibility = Visibility.Collapsed;
        if (tab.Core is { } c) c.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
        (stashed.TryGetValue(name, out var list) ? list : stashed[name] = []).Add(tab);
    }

    void NextWorkspace()
    {
        if (workspaces.Count < 2)
        {
            _ = NewWorkspaceAsync(null);
            return;
        }
        SwitchWorkspace(workspaces[(workspaces.IndexOf(workspace) + 1) % workspaces.Count]);
    }

    void MoveToWorkspace(BrowserTab tab, string name)
    {
        if (name == workspace || !tabs.Contains(tab)) return;
        if (!workspaces.Contains(name)) workspaces.Add(name);
        TakeOut(tab);
        Stash(tab, name);
        LayoutPages();
        UpdateWorkspaceButton();
        ShowNotice(new($"Moved to {Workspace.Display(name)}.", NoticeKind.Info, "Go there", () => SwitchWorkspace(name, tab)));
        ScheduleSave();
    }

    async Task<string?> AskWorkspaceNameAsync(string title, string text, string button)
    {
        var box = new TextBox { Text = text, PlaceholderText = "Work, Home, a project…", MaxLength = 40 };
        box.Loaded += (_, _) => box.SelectAll();
        if (await AskAsync(title, box, button) != ContentDialogResult.Primary) return null;
        var name = Workspace.Clean(box.Text);
        return name.Length == 0 && box.Text.Trim() != Workspace.MainName ? null : name;
    }

    // tab: move that tab into the new workspace instead of switching to it.
    async Task NewWorkspaceAsync(BrowserTab? tab)
    {
        if (await AskWorkspaceNameAsync("New workspace", "", "Create") is not { } name) return;
        if (tab is null) SwitchWorkspace(name);
        else MoveToWorkspace(tab, name);
    }

    async Task RenameWorkspaceAsync()
    {
        if (workspace.Length == 0 || await AskWorkspaceNameAsync("Rename workspace", workspace, "Rename") is not { } name || name == workspace) return;
        if (workspaces.Contains(name))
        {
            ShowNotice(new($"There's already a workspace called {Workspace.Display(name)}.", NoticeKind.Info));
            return;
        }
        workspaces[workspaces.IndexOf(workspace)] = name;
        workspace = name;
        foreach (var t in tabs) t.Workspace = name;
        UpdateWorkspaceButton();
        ScheduleSave();
    }

    // Its tabs join the main workspace, so nothing is lost.
    void RemoveWorkspace()
    {
        if (workspace.Length == 0) return;
        var gone = workspace;
        var moved = tabs.Where(t => !t.IsHome).ToList();
        SwitchWorkspace("");
        foreach (var t in moved) tabs.Add(t);
        foreach (var t in tabs) t.Workspace = "";
        workspaces.Remove(gone);
        // The removed workspace's views stay with their tabs; its home tabs close.
        if (stashed.Remove(gone, out var rest))
            foreach (var t in rest.Where(t => !moved.Contains(t))) DisposeTab(t);
        UpdateWorkspaceButton();
        if (moved.Count > 0) ShowNotice(new($"{Workspace.Display(gone)} removed; its {Plural(moved.Count, "tab")} moved to {Workspace.MainName}.", NoticeKind.Info));
        ScheduleSave();
    }

    void DisposeTab(BrowserTab tab)
    {
        engine.Detach(tab.Core);
        if (tab.View is { } view)
        {
            PageHost.Children.Remove(view);
            PanelPages.Children.Remove(view);
            view.Close();
        }
        tab.View = null;
        tab.Core = null;
    }

    static string Plural(int n, string word) => n == 1 ? $"1 {word}" : $"{n} {word}s";

    void UpdateWorkspaceButton()
    {
        TabsTitle.Text = workspaces.Count < 2 ? "Tabs" : Workspace.Display(workspace);
        // A tab strip has little room: the menu shows there once there's more than one workspace.
        WorkspaceButton.Visibility = verticalTabs || workspaces.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    void WorkspaceMenu_Opening(object? sender, object e)
    {
        var menu = WorkspaceMenu;
        menu.Items.Clear();
        foreach (var name in workspaces)
        {
            var n = name;
            var count = n == workspace ? tabs.Count : stashed.GetValueOrDefault(n)?.Count ?? 0;
            var item = new ToggleMenuFlyoutItem { Text = $"{Workspace.Display(n)}  ·  {count}", IsChecked = n == workspace };
            item.Click += (_, _) => SwitchWorkspace(n);
            menu.Items.Add(item);
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        void Add(string text, string glyph, Action action)
        {
            var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        Add("New workspace…", "", () => _ = NewWorkspaceAsync(null));
        if (workspace.Length > 0)
        {
            Add("Rename this workspace…", "", () => _ = RenameWorkspaceAsync());
            Add("Remove this workspace", "", RemoveWorkspace);
        }
    }

    // For a tab's context menu.
    MenuFlyoutSubItem WorkspaceSubMenu(BrowserTab tab)
    {
        var sub = new MenuFlyoutSubItem { Text = "Move to workspace", Icon = new FontIcon { Glyph = "" } };
        foreach (var name in workspaces.Where(n => n != workspace))
        {
            var n = name;
            var item = new MenuFlyoutItem { Text = Workspace.Display(n) };
            item.Click += (_, _) => MoveToWorkspace(tab, n);
            sub.Items.Add(item);
        }
        var add = new MenuFlyoutItem { Text = "New workspace…" };
        add.Click += (_, _) => _ = NewWorkspaceAsync(tab);
        sub.Items.Add(add);
        return sub;
    }
}
