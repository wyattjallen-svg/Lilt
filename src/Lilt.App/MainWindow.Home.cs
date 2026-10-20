using System.Text.Json;
using Lilt.App.Browser;
using Lilt.Core;
using Lilt.Core.Bookmarks;
using Lilt.Core.Navigation;
using Lilt.Core.Theming;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace Lilt.App;

// The home tab is a plain local web page with no bridge to Lilt. It reads lilt-data.json for colors, layout and tiles,
// and asks for the few things it can't do itself (customize, add or remove a tile) by changing its own #fragment.
public sealed partial class MainWindow
{
    DispatcherQueueTimer? homeTimer;
    string lastHomeData = "";
    bool homeDataWritten, homeDataStale;

    bool HomeGlass => theme.Home.Glass && SystemBackdrop is not null;

    string IconsFolder => Path.Combine(paths.NewTab, "icons");

    // Tiles come from the "Home" bookmark folder when there is one, otherwise the bookmarks bar.
    string TileFolder => bookmarks.Items.Any(b => b.Folder == "Home") ? "Home" : "";

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
        var t = theme;
        var c = t.Colors;
        var folder = TileFolder;
        var data = new
        {
            items = t.Home.Items.Items,
            glass = HomeGlass,
            background = t.Home.Background,
            @base = t.Base == ThemeBase.Light ? "light" : "dark",
            font = t.Typography.Family,
            radius = t.Shape.Radius,
            search = SearchEngines.For(t.Pages.Search).Name,
            // The shortcut hint shows your own keys.
            hint = string.Join(" · ", new[] { ("palette", "find anything"), ("newTab", "new tab"), ("address", "address"), ("bookmark", "bookmark"), ("split", "split view"), ("customize", "customize") }
                .Select(x => t.Shortcuts.For(x.Item1) is [var g, ..] ? $"{g} {x.Item2}" : null).OfType<string>()),
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
            tiles = bookmarks.Items.Where(b => b.Folder == folder).Take(24).Select(b => new { title = b.Title, url = b.Url, icon = TileIcon(b.Url) }),
        };
        var json = JsonSerializer.Serialize(data, Files.Json);
        if (json == lastHomeData && !homeDataStale) return;
        try
        {
            Files.WriteAtomic(Path.Combine(paths.NewTab, "lilt-data.json"), json);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            LiltPaths.LogError(e);
            return;
        }
        lastHomeData = json;
        homeDataStale = false;
        if (!homeDataWritten)
        {
            homeDataWritten = true;
            return;
        }
        foreach (var tab in tabs)
            if (tab.IsHome && tab.Core is { } core) core.Reload();
    }

    string? TileIcon(string url)
    {
        var host = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.IdnHost : "";
        return host.Length > 0 && File.Exists(Path.Combine(IconsFolder, host + ".png")) ? $"icons/{host}.png" : null;
    }

    // Keeps a site's favicon for its home tile; refreshed weekly at most.
    void SaveTileIcon(string pageUrl, byte[] png)
    {
        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var page) || !UrlPolicy.IsWeb(page) || png.Length is 0 or > 256 * 1024) return;
        var host = page.IdnHost;
        if (!bookmarks.Items.Any(b => b.Folder == TileFolder && Uri.TryCreate(b.Url, UriKind.Absolute, out var u) && u.IdnHost == host)) return;
        try
        {
            Directory.CreateDirectory(IconsFolder);
            var file = Path.Combine(IconsFolder, host + ".png");
            if (File.Exists(file) && File.GetLastWriteTimeUtc(file) > DateTime.UtcNow.AddDays(-7)) return;
            File.WriteAllBytes(file, png);
            WriteHomeData();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            LiltPaths.LogError(e);
        }
    }

    // Same-document fragment changes on the home tab: #customize, #add-tile, #remove-tile=<url>.
    // Only the home page itself can produce these (a site linking here starts a new document, which is ignored).
    void HomeCommand(CoreWebView2 core)
    {
        if (!Uri.TryCreate(core.Source, UriKind.Absolute, out var u) || u.Fragment.Length < 2) return;
        var command = Uri.UnescapeDataString(u.Fragment[1..]);
        if (command == "customize") ShowCustomize("home");
        else if (command.StartsWith("go=", StringComparison.Ordinal))
        {
            // Navigating away replaces this document, so there's no fragment to clean up.
            Go(command[3..]);
            return;
        }
        else if (command == "add-tile") _ = AddTileAsync();
        else if (command.StartsWith("remove-tile=", StringComparison.Ordinal))
        {
            if (bookmarks.Remove(command["remove-tile=".Length..])) SaveBookmarks();
        }
        else return;
        _ = core.ExecuteScriptAsync("history.replaceState(null, '', location.pathname)");
    }

    async Task AddTileAsync()
    {
        var address = new TextBox { Header = "Address", PlaceholderText = "example.com", IsSpellCheckEnabled = false };
        var name = new TextBox { Header = "Name (optional)" };
        var panel = new StackPanel { Spacing = 10, MinWidth = 320 };
        panel.Children.Add(address);
        panel.Children.Add(name);
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "Add a home tile",
            Content = panel,
            PrimaryButtonText = "Add",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            RequestedTheme = Root.RequestedTheme,
        };
        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        }
        catch (Exception)
        {
            return; // another dialog is already open
        }
        var result = AddressResolver.Resolve(address.Text);
        if (result is not { Kind: AddressKind.Navigate, Uri: { } uri } || !UrlPolicy.IsWeb(uri))
        {
            ShowNotice(new("A tile needs a web address, like example.com.", NoticeKind.Warning));
            return;
        }
        var title = string.IsNullOrWhiteSpace(name.Text) ? uri.Host.Replace("www.", "") : name.Text.Trim();
        if (!bookmarks.Add(title, uri.AbsoluteUri, TileFolder))
        {
            ShowNotice(new("That site is already bookmarked.", NoticeKind.Info));
            return;
        }
        SaveBookmarks();
    }
}
