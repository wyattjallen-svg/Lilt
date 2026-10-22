using Lilt.App.Browser;
using Lilt.App.Shell;
using Lilt.Core;
using Lilt.Core.Navigation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Lilt.App;

// Tab overview (Ctrl+K → Show all tabs): this workspace's tabs as cards, like the tab overview in Safari or Vivaldi's
// tab grid. Click a card to go there. Nothing is loaded or woken up to draw it: a sleeping tab shows its title and icon.
public sealed partial class MainWindow
{
    async Task ShowTabOverviewAsync()
    {
        var all = tabs.ToList();
        BrowserTab? chosen = null;
        var grid = new WrapPanel { Orientation = Orientation.Horizontal, MaximumRowsOrColumns = 4 };
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = workspace.Length == 0 ? $"All tabs ({all.Count})" : $"{Workspace.Display(workspace)} ({all.Count})",
            Content = new ScrollViewer { Content = grid, MaxHeight = 480, MinWidth = 620 },
            CloseButtonText = "Close",
            RequestedTheme = Root.RequestedTheme,
        };
        foreach (var tab in all)
        {
            var t = tab;
            var stack = new StackPanel { Spacing = 6 };
            stack.Children.Add(new Image { Source = t.Icon, Width = 20, Height = 20, HorizontalAlignment = HorizontalAlignment.Left });
            stack.Children.Add(new TextBlock { Text = t.DisplayTitle, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 2, TextWrapping = TextWrapping.Wrap });
            stack.Children.Add(new TextBlock
            {
                Text = t.IsAsleep ? "Asleep" : UrlPolicy.DisplayHost(t.Url), FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = ThemeResources.Get("LiltTextMutedBrush"),
            });
            var card = new Button
            {
                Content = stack, Width = 150, Height = 96, Padding = new Thickness(10), Margin = new Thickness(4), CornerRadius = new CornerRadius(10),
                VerticalAlignment = VerticalAlignment.Top, Opacity = t.ContentOpacity,
                BorderBrush = t == active ? ThemeResources.Get("LiltAccentBrush") : ThemeResources.Get("LiltBorderBrush"),
            };
            card.Click += (_, _) =>
            {
                chosen = t;
                dialog.Hide();
            };
            ToolTipService.SetToolTip(card, t.Tip);
            grid.Children.Add(card);
        }
        await dialog.ShowAsync();
        if (chosen is not null && tabs.Contains(chosen)) Activate(chosen);
    }
}

// Reading list (Safari's Reading List): the current page goes into a bookmarks folder of its own to read later.
public sealed partial class MainWindow
{
    public const string ReadingFolder = "Reading list";

    void AddToReadingList()
    {
        if (active is not { IsHome: false } tab || tab.Url.Length == 0) return;
        if (bookmarks.Contains(tab.Url))
        {
            ShowNotice(new("That page is already in your bookmarks.", NoticeKind.Info));
            return;
        }
        bookmarks.Add(tab.Title, tab.Url, ReadingFolder);
        SaveBookmarks();
        ShowNotice(new($"Added to {ReadingFolder}.", NoticeKind.Success));
    }
}
