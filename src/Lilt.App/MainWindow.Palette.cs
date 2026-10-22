using Lilt.App.Shell;
using Lilt.Core;
using Lilt.Core.Layout;
using Lilt.Core.Navigation;
using Lilt.Core.Theming;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using VirtualKey = Windows.System.VirtualKey;

namespace Lilt.App;

// Command palette (Ctrl+K): every command, open tab, bookmark, preset and setting, found by typing a few letters.
public sealed partial class MainWindow
{
    sealed record PaletteItem(string Title, string Hint, string Glyph, Action Run, bool Listed = false);

    static readonly Dictionary<string, string> commandGlyphs = new()
    {
        ["newTab"] = "\uE710", ["reopenTab"] = "\uE7A7", ["closeTab"] = "\uE711", ["nextTab"] = "\uE72A", ["prevTab"] = "\uE72B",
        ["address"] = "\uE721", ["back"] = "\uE72B", ["forward"] = "\uE72A", ["reload"] = "\uE72C", ["home"] = "\uE80F",
        ["bookmark"] = "\uE734", ["sidebar"] = "\uE8A0", ["menu"] = "\uE712", ["customize"] = "\uE790", ["palette"] = "\uE945",
        ["split"] = "\uE8A9", ["media"] = "\uE8D6", ["visualizer"] = "\uE9D9", ["pip"] = "\uE8A7",
        ["bookmarks"] = "\uE8F1", ["screenshot"] = "\uE722", ["workspace"] = "\uE8B7",
        ["panel"] = "\uE89F", ["toPanel"] = "\uE89F", ["sleepTabs"] = "\uE708",
    };

    void ShowPalette()
    {
        PaletteBox.Text = "";
        FillPalette("");
        ShowFlyout(PaletteFlyout, AddressHost, PaletteHost);
    }

    void PaletteButton_Click(object sender, RoutedEventArgs e) => ShowPalette();
    void PaletteFlyout_Opened(object sender, object e) => PaletteBox.Focus(FocusState.Programmatic);
    void PaletteFlyout_Closed(object sender, object e) => PaletteList.Items.Clear();
    void PaletteBox_TextChanged(object sender, TextChangedEventArgs e) => FillPalette(PaletteBox.Text);

    void PaletteBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var n = PaletteList.Items.Count;
        switch (e.Key)
        {
            case VirtualKey.Down when n > 0:
                PaletteList.SelectedIndex = (PaletteList.SelectedIndex + 1) % n;
                break;
            case VirtualKey.Up when n > 0:
                PaletteList.SelectedIndex = (PaletteList.SelectedIndex - 1 + n) % n;
                break;
            case VirtualKey.Enter when n > 0:
                RunPaletteItem(PaletteList.Items[Math.Max(0, PaletteList.SelectedIndex)]);
                break;
            default:
                return;
        }
        e.Handled = true;
        if (PaletteList.SelectedItem is { } selected) PaletteList.ScrollIntoView(selected);
    }

    void PaletteList_ItemClick(object sender, ItemClickEventArgs e) => RunPaletteItem(e.ClickedItem);

    void RunPaletteItem(object? row)
    {
        if (row is not FrameworkElement { Tag: PaletteItem item }) return;
        PaletteFlyout.Hide();
        report.Key("palette", item.Title);
        // After the flyout closes, so anything the item focuses keeps its focus.
        DispatcherQueue.TryEnqueue(() => item.Run());
    }

    void FillPalette(string query)
    {
        var q = query.Trim();
        var found = PaletteItems().Select(i => (Item: i, Score: Score(i, q))).Where(x => x.Score > 0).OrderByDescending(x => x.Score).Take(40).Select(x => x.Item).ToList();
        if (q.Length > 0 && AddressResolver.Resolve(q, Engine.Template) is { Kind: AddressKind.Navigate or AddressKind.Search } go)
        {
            var item = new PaletteItem(go.Kind == AddressKind.Navigate ? $"Go to {go.Uri!.Host}" : $"Search for “{q}”", "Enter", "", () => Go(q));
            // An address goes first; a search goes after real matches.
            found.Insert(go.Kind == AddressKind.Navigate || found.Count == 0 ? 0 : Math.Min(found.Count, 5), item);
        }
        PaletteList.Items.Clear();
        foreach (var item in found) PaletteList.Items.Add(Row(item));
        PaletteList.SelectedIndex = PaletteList.Items.Count > 0 ? 0 : -1;
    }

    FrameworkElement Row(PaletteItem item)
    {
        var grid = new Grid { Tag = item, ColumnSpacing = 10, Padding = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new FontIcon { Glyph = item.Glyph, FontSize = 14 });
        var title = new TextBlock { Text = item.Title, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(title, 1);
        grid.Children.Add(title);
        var hint = new TextBlock { Text = item.Hint, FontSize = 12, Foreground = ThemeResources.Get("LiltTextMutedBrush"), MaxWidth = 220, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(hint, 2);
        grid.Children.Add(hint);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(grid, $"{item.Title}, {item.Hint}");
        return grid;
    }

    // Prefix matches first, then every word somewhere in the title, then in the hint, then letters in order.
    static double Score(PaletteItem item, string q)
    {
        if (q.Length == 0) return item.Listed ? 1 : 0;
        var t = item.Title;
        if (t.StartsWith(q, StringComparison.OrdinalIgnoreCase)) return 5;
        var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.All(w => t.Contains(w, StringComparison.OrdinalIgnoreCase))) return 4;
        if (words.All(w => t.Contains(w, StringComparison.OrdinalIgnoreCase) || item.Hint.Contains(w, StringComparison.OrdinalIgnoreCase))) return 3;
        var i = 0;
        foreach (var ch in t)
            if (i < q.Length && char.ToLowerInvariant(ch) == char.ToLowerInvariant(q[i])) i++;
        return i == q.Length && q.Length >= 2 ? 1 : 0;
    }

    IEnumerable<PaletteItem> PaletteItems()
    {
        foreach (var (id, label) in Commands.Labels)
            yield return new(label, Shortcut(id), commandGlyphs.GetValueOrDefault(id, "\uE756"), () => RunCommand(id, keyboard: true), Listed: true);
        foreach (var name in workspaces.Where(n => n != workspace))
            yield return new($"Workspace: {Workspace.Display(name)}", "Switch workspace", "\uE8B7", () => SwitchWorkspace(name), Listed: true);
        foreach (var tab in AllTabs)
            yield return new(tab.DisplayTitle, tab == active ? "This tab" : tabs.Contains(tab) ? "Switch to tab" : panelTabs.Contains(tab) ? "In the panel" : $"Tab in {Workspace.Display(tab.Workspace)}", "", () => Activate(tab), Listed: true);
        if (active is { } a && Partner(a) is not null)
        {
            yield return new("Swap split panes", "Split view", "", SwapSplit, Listed: true);
            yield return new("Close split view", "Split view", "", ToggleSplit, Listed: true);
        }
        yield return new("Print", "Ctrl+P", "", () => core?.ShowPrintUI(Microsoft.Web.WebView2.Core.CoreWebView2PrintDialogKind.Browser));
        yield return new("Developer tools", "F12", "", () => core?.OpenDevToolsWindow());
        yield return new("Downloads", "", "", () => ShowFlyout(DownloadsFlyout, DownloadsButton, FlyoutHost));
        if (engine.ExtensionsEnabled) yield return new("Extensions", "", "", () => Extensions_Click(this, new RoutedEventArgs()));
        yield return new("Clear history", "Address-bar suggestions and the engine's history", "\uE74D", () => ClearHistory_Click(this, new RoutedEventArgs()));
        yield return new("Import bookmarks", "From another browser or a file", "\uE8B5", () => ShowFlyout(ImportFlyout, BookmarkImportButton, FlyoutHost));
        yield return new("Edit theme file", "theme.json", "", OpenThemeFile);
        yield return new("Reload theme", "theme.json", "", () => themes.Reload());
        yield return new("Open data folder", "", "", () => Open("explorer.exe", paths.Root));
        yield return new("Show all tabs", "", "", () => _ = ShowTabOverviewAsync());
        yield return new("Logs and crashes", "about:logs", "\uEBE8", OpenLogs);
        yield return new("Add this page to the reading list", "", "", AddToReadingList);
        yield return new("Open user styles and scripts folder", "", "", () => Open("explorer.exe", engine.UserContent.Folder));
        yield return new("Put the player and visualizer back", "Media", "", () => ResetWidgets_Click(this, new RoutedEventArgs()));
        foreach (var page in new[] { "look", "layout", "home", "media", "keys", "power", "privacy" })
            yield return new($"Customize: {char.ToUpperInvariant(page[0])}{page[1..]}", "Settings", "", () => ShowCustomize(page));

        foreach (var (name, path, mine) in Presets())
            yield return new($"Theme: {name}", mine ? "Your preset" : "Preset", "", () => ApplyPreset(path));
        foreach (var (name, tag) in new[] { ("Compact", "compact"), ("Research", "research"), ("Vertical tabs", "vertical"), ("Top tabs", "top"), ("Bottom bar", "bottom") })
            yield return new($"Layout: {name}", "Preset", "", () => ApplyLayout(tag));
        foreach (var edge in Enum.GetValues<DockEdge>())
            yield return new($"Dock the sidebar {edge.ToString().ToLowerInvariant()}", "Sidebar", "", () =>
            {
                sidebar.Edge = edge;
                LayoutSidebar();
            });

        var t = Current;
        PaletteItem Toggle(string name, bool on, Func<Theme, Theme> flip) => new(name, on ? "On" : "Off", "", () => Queue(flip(Current)));
        yield return new("Auto-hide the sidebar", sidebar.AutoHide ? "On" : "Off", "", () =>
        {
            sidebar.SetAutoHide(!sidebar.AutoHide);
            LayoutSidebar();
        });
        yield return Toggle("Efficiency mode", t.Power.Efficiency, x => x with { Power = x.Power with { Efficiency = !x.Power.Efficiency } });
        yield return Toggle("Pause pages behind full-screen games", t.Power.HideWhenCovered, x => x with { Power = x.Power with { HideWhenCovered = !x.Power.HideWhenCovered } });
        yield return Toggle("Hide pages behind other windows", t.Power.HideWhenOccluded, x => x with { Power = x.Power with { HideWhenOccluded = !x.Power.HideWhenOccluded } });
        yield return Toggle("Lean mode", t.Power.Lean, x => x with { Power = x.Power with { Lean = !x.Power.Lean } });
        yield return Toggle("Animations", t.Motion.Animations, x => x with { Motion = x.Motion with { Animations = !x.Motion.Animations } });
        yield return Toggle("Lift the window while dragging it", t.Motion.WindowDrag, x => x with { Motion = x.Motion with { WindowDrag = !x.Motion.WindowDrag } });
        yield return Toggle("See-through websites", t.Pages.Glass, x => x with { Pages = x.Pages with { Glass = !x.Pages.Glass } });
        if (t.Pages.Glass && active is { } here && Uri.TryCreate(here.Url, UriKind.Absolute, out var hu) && UrlPolicy.IsWeb(hu))
            yield return new(GlassOffHere(here) ? "See-through on this site" : "Keep this site solid", UrlPolicy.DisplayHost(here.Url), "\uE7B3", () => ToggleGlassHere(here));
        yield return Toggle("Mouse gestures", t.Pages.Gestures, x => x with { Pages = x.Pages with { Gestures = !x.Pages.Gestures } });
        yield return Toggle("Dark mode for every website", t.Pages.ForceDark, x => x with { Pages = x.Pages with { ForceDark = !x.Pages.ForceDark } });
        yield return Toggle("Close button on every tab", t.Sidebar.CloseButtons, x => x with { Sidebar = x.Sidebar with { CloseButtons = !x.Sidebar.CloseButtons } });
        foreach (var level in Enum.GetValues<TrackingLevel>())
            yield return new($"Block trackers: {level.ToString().ToLowerInvariant()}", t.Pages.Tracking == level ? "Current" : "Privacy", "\uE72E",
                () => Queue(Current with { Pages = Current.Pages with { Tracking = level } }));
        yield return new("New workspace", "Workspaces", "\uE710", () => _ = NewWorkspaceAsync(null));
        yield return new(t.Panel.Edge == DockEdge.Left ? "Move the panel to the right" : "Move the panel to the left", "Panel", "\uE8AB",
            () => Queue(Current with { Panel = Current.Panel with { Edge = Current.Panel.Edge == DockEdge.Left ? DockEdge.Right : DockEdge.Left } }));
        yield return Toggle("Visualizer docked in the panel", t.Panel.Sections.Has("visualizer"), x => x with { Panel = x.Panel with { Sections = ToggleId(x.Panel.Sections, "visualizer") } });
        yield return Toggle("Player docked in the panel", t.Panel.Sections.Has("player"), x => x with { Panel = x.Panel with { Sections = ToggleId(x.Panel.Sections, "player") } });
        yield return Toggle("Toolbar takes the site's color", t.Toolbar.SiteColor > 0, x => x with { Toolbar = x.Toolbar with { SiteColor = x.Toolbar.SiteColor > 0 ? 0 : 0.45 } });
        yield return Toggle("Sidebar takes the site's color", t.Sidebar.SiteColor > 0, x => x with { Sidebar = x.Sidebar with { SiteColor = x.Sidebar.SiteColor > 0 ? 0 : 0.2 } });
        yield return new("Empty caches now", "Storage: you stay signed in", "\uE74D", () => EmptyCaches_Click(this, new RoutedEventArgs()));
        yield return new("Wipe everything and start fresh…", "Asks for a code first", "\uE894", () => _ = WipeAsync());
        yield return new("What's using memory", "Customize → Power", "\uE950", () =>
        {
            ShowCustomize("power");
            _ = FillMemoryListAsync();
        });
        yield return new("Mute other tabs", "Sound", "\uE74F", () =>
        {
            foreach (var x in AllTabs) if (x != active && x.Core is { } c) c.IsMuted = true;
        });
        yield return new("Unmute every tab", "Sound", "\uE767", () =>
        {
            foreach (var x in AllTabs) if (x.Core is { } c) c.IsMuted = false;
        });
        yield return Toggle("Smooth scrolling", t.Pages.SmoothScrolling, x => x with { Pages = x.Pages with { SmoothScrolling = !x.Pages.SmoothScrolling } });
        yield return Toggle("Universal font for pages", t.Pages.UniversalFont, x => x with { Pages = x.Pages with { UniversalFont = !x.Pages.UniversalFont } });
        yield return Toggle("Solid window material while I'm away", t.Power.SolidWhenAway, x => x with { Power = x.Power with { SolidWhenAway = !x.Power.SolidWhenAway } });
        yield return Toggle("Open last time's tabs on start", t.Pages.Restore, x => x with { Pages = x.Pages with { Restore = !x.Pages.Restore } });
        yield return Toggle("Sleep tabs sooner when memory runs low", t.Power.SleepWhenLowMemory, x => x with { Power = x.Power with { SleepWhenLowMemory = !x.Power.SleepWhenLowMemory } });
        foreach (var minutes in new[] { 15, 30, 60, 120, 0 })
            yield return new(minutes == 0 ? "Never put tabs to sleep" : $"Put unused tabs to sleep after {minutes} minutes", t.Power.SleepMinutes == minutes ? "Current" : "Sleeping tabs", "\uE708",
                () => Queue(Current with { Power = Current.Power with { SleepMinutes = minutes } }));
        if (active is { IsHome: false } kt && Uri.TryCreate(kt.Url, UriKind.Absolute, out var ku) && UrlPolicy.IsWeb(ku))
            yield return new(KeptHere ? "Let this site be tidied up" : "Never tidy up this site", UrlPolicy.DisplayHost(kt.Url), "\uE72E", ToggleKeepHere);
        yield return Toggle("Tidy up sites I stop using", t.Storage.ForgetAfterDays > 0,
            x => x with { Storage = x.Storage with { ForgetAfterDays = x.Storage.ForgetAfterDays > 0 ? 0 : 90 } });
        if (active is { } mt && workspaces.Count > 1)
            foreach (var name in workspaces.Where(n => n != workspace))
                yield return new($"Move this tab to {Workspace.Display(name)}", "Workspaces", "\uE8B7", () => MoveToWorkspace(mt, name));
        yield return Toggle("Peek with Shift+click", t.Pages.Peek, x => x with { Pages = x.Pages with { Peek = !x.Pages.Peek } });
        yield return Toggle("See-through home tab", t.Home.Glass, x => x with { Home = x.Home with { Glass = !x.Home.Glass } });
        yield return Toggle("Pop videos out when switching tabs", t.Media.AutoPictureInPicture, x => x with { Media = x.Media with { AutoPictureInPicture = !x.Media.AutoPictureInPicture } });
        yield return Toggle("Stacked split view", t.Split.Orientation == SplitOrientation.Vertical,
            x => x with { Split = x.Split with { Orientation = x.Split.Orientation == SplitOrientation.Vertical ? SplitOrientation.Horizontal : SplitOrientation.Vertical } });
        foreach (var style in Enum.GetValues<VisualizerStyle>())
            yield return new($"Visualizer style: {style.ToString().ToLowerInvariant()}", t.Media.Style == style ? "Current" : "", "",
                () => Queue(Current with { Media = Current.Media with { Style = style } }));
        foreach (var show in Enum.GetValues<WidgetShow>())
        {
            yield return new($"Show the player: {show.ToString().ToLowerInvariant()}", t.Media.Player.Show == show ? "Current" : "", "",
                () => Queue(Current with { Media = Current.Media with { Player = Current.Media.Player with { Show = show } } }));
            yield return new($"Show the visualizer: {show.ToString().ToLowerInvariant()}", t.Media.Visualizer.Show == show ? "Current" : "", "",
                () => Queue(Current with { Media = Current.Media with { Visualizer = Current.Media.Visualizer with { Show = show } } }));
        }
        foreach (var material in Enum.GetValues<Backdrop>())
            yield return new($"Window material: {material}", t.Backdrop.Material == material ? "Current" : "", "",
                () => Queue(Current with { Backdrop = Current.Backdrop with { Material = material } }));
        foreach (var density in Enum.GetValues<Density>())
            yield return new($"Spacing: {density.ToString().ToLowerInvariant()}", t.Density == density ? "Current" : "", "",
                () => Queue(Current with { Density = density }));
        foreach (var (key, engine) in SearchEngines.Known)
            yield return new($"Search with {engine.Name}", string.Equals(t.Pages.Search, key, StringComparison.OrdinalIgnoreCase) ? "Current" : "Search engine", "\uE721",
                () => Queue(Current with { Pages = Current.Pages with { Search = key } }));
        yield return Toggle("Address-bar suggestions from history", t.Pages.History, x => x with { Pages = x.Pages with { History = !x.Pages.History } });
        foreach (var scheme in Enum.GetValues<PageColorScheme>())
            yield return new($"Websites see: {scheme.ToString().ToLowerInvariant()}", t.Pages.ColorScheme == scheme ? "Current" : "", "",
                () => Queue(Current with { Pages = Current.Pages with { ColorScheme = scheme } }));

        foreach (var b in bookmarks.Items.Take(2000))
            yield return new(b.Title, UrlPolicy.DisplayHost(b.Url), "", () => Navigate(b.Url));
    }
}
