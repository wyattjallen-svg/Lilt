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
        foreach (var tab in tabs)
            yield return new(tab.DisplayTitle, tab == active ? "This tab" : "Switch to tab", "", () => Activate(tab), Listed: true);
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
        yield return new("Open user styles and scripts folder", "", "", () => Open("explorer.exe", engine.UserContent.Folder));
        yield return new("Put the player and visualizer back", "Media", "", () => ResetWidgets_Click(this, new RoutedEventArgs()));
        foreach (var page in new[] { "look", "layout", "home", "media", "keys", "power" })
            yield return new($"Customize: {char.ToUpperInvariant(page[0])}{page[1..]}", "Settings", "", () => ShowCustomize(page));

        foreach (var name in new[] { "Minimal", "Glass", "Light" })
            yield return new($"Theme: {name}", "Preset", "", () => ApplyPreset(name.ToLowerInvariant()));
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
