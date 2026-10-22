using Lilt.App.Browser;
using Lilt.Core.Navigation;
using Lilt.Core.Theming;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace Lilt.App;

// What sites may do (permission questions and the list of answers), mouse gestures, see-through sites, screenshots.
public sealed partial class MainWindow
{
    readonly SemaphoreSlim oneQuestion = new(1);
    TaskCompletionSource<(bool Allow, bool Remember)?>? permissionAnswer;

    // One question at a time; a site waiting for its answer keeps waiting (the page can't continue the request until then).
    async Task<(bool Allow, bool Remember)?> AskPermissionAsync(string site, CoreWebView2PermissionKind kind)
    {
        await oneQuestion.WaitAsync();
        try
        {
            var answer = new TaskCompletionSource<(bool, bool)?>();
            permissionAnswer = answer;
            PermissionBar.Title = site;
            PermissionBar.Message = $"wants to {BrowserEngine.Describe(kind)}.";
            PermissionRemember.IsChecked = true;
            PermissionBar.IsOpen = true;
            report.Key("permission", $"{site} {kind}");
            return await answer.Task;
        }
        finally
        {
            PermissionBar.IsOpen = false;
            permissionAnswer = null;
            oneQuestion.Release();
        }
    }

    void PermissionAllow_Click(object sender, RoutedEventArgs e) => permissionAnswer?.TrySetResult((true, PermissionRemember.IsChecked == true));
    void PermissionBlock_Click(object sender, RoutedEventArgs e) => permissionAnswer?.TrySetResult((false, PermissionRemember.IsChecked == true));
    void PermissionBar_CloseButtonClick(InfoBar sender, object args) => permissionAnswer?.TrySetResult(null);

    // Customize → Privacy: every site permission you've allowed or blocked, each with a way back to "ask".
    async void FillPermissions()
    {
        PermissionList.Children.Clear();
        if (AnyCore is not { } core) return;
        try
        {
            var settings = await core.Profile.GetNonDefaultPermissionSettingsAsync();
            if (settings.Count == 0)
            {
                PermissionList.Children.Add(new TextBlock { Text = "None yet.", Foreground = Shell.ThemeResources.Get("LiltTextMutedBrush") });
                return;
            }
            foreach (var s in settings.OrderBy(s => s.PermissionOrigin))
            {
                var row = new Grid { ColumnSpacing = 6 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(new TextBlock
                {
                    Text = $"{UrlPolicy.DisplayHost(s.PermissionOrigin)}: {(s.PermissionState == CoreWebView2PermissionState.Allow ? "may" : "may not")} {BrowserEngine.Describe(s.PermissionKind)}",
                    TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, FontSize = 12,
                });
                var reset = new Button { Content = "Ask again", FontSize = 12 };
                var (kind, origin) = (s.PermissionKind, s.PermissionOrigin);
                reset.Click += async (_, _) =>
                {
                    await core.Profile.SetPermissionStateAsync(kind, origin, CoreWebView2PermissionState.Default);
                    FillPermissions();
                };
                Grid.SetColumn(reset, 1);
                row.Children.Add(reset);
                PermissionList.Children.Add(row);
            }
            ScaleNow(PermissionList);
        }
        catch (Exception e)
        {
            LiltPaths.LogError(e);
        }
    }

    // Right-button strokes from the page helper.
    static readonly Dictionary<string, string> gestureCommands = new()
    {
        ["L"] = "back", ["R"] = "forward", ["UD"] = "reload", ["DR"] = "closeTab", ["D"] = "newTab",
    };

    void OnGesture(CoreWebView2 sender, string path)
    {
        if (!gestureCommands.TryGetValue(path, out var command)) return;
        if (!tabs.Any(t => t.Core == sender))
        {
            PanelCommand(sender, command);
            return;
        }
        if (sender != core && tabs.FirstOrDefault(t => t.Core == sender) is { } tab) Activate(tab);
        RunCommand(command, "gesture");
    }

    // See-through websites, per site: the host goes on or off pages.glassOff.
    void ToggleGlassHere(BrowserTab? tab = null)
    {
        tab ??= active;
        if (tab is null || !Uri.TryCreate(tab.Url, UriKind.Absolute, out var u) || !UrlPolicy.IsWeb(u)) return;
        var host = u.Host.StartsWith("www.", StringComparison.Ordinal) ? u.Host[4..] : u.Host;
        var off = Current.Pages.GlassOffSites.Items.ToList();
        if (!off.Remove(host)) off.Add(host);
        Queue(Current with { Pages = Current.Pages with { GlassOff = new Ids(off.ToArray()) } });
        ShowNotice(new(off.Contains(host) ? $"{host} stays solid." : $"{host} is see-through again.", NoticeKind.Success));
    }

    bool GlassOffHere(BrowserTab? tab) =>
        tab is not null && Uri.TryCreate(tab.Url, UriKind.Absolute, out var u) && Current.Pages.GlassOffSites.Items.Any(h => u.Host == h || u.Host.EndsWith("." + h, StringComparison.Ordinal));

    // The whole page as a PNG in Pictures\Lilt (and on the clipboard), through the engine's own capture.
    async void Screenshot()
    {
        if (active?.Core is not { } c) return;
        try
        {
            using var metrics = System.Text.Json.JsonDocument.Parse(await c.CallDevToolsProtocolMethodAsync("Page.getLayoutMetrics", "{}"));
            var size = metrics.RootElement.GetProperty("cssContentSize");
            double width = size.GetProperty("width").GetDouble(), height = Math.Min(size.GetProperty("height").GetDouble(), 16000);
            var args = System.Text.Json.JsonSerializer.Serialize(new
            {
                format = "png", captureBeyondViewport = true, clip = new { x = 0, y = 0, width, height, scale = 1 },
            });
            using var shot = System.Text.Json.JsonDocument.Parse(await c.CallDevToolsProtocolMethodAsync("Page.captureScreenshot", args));
            var png = Convert.FromBase64String(shot.RootElement.GetProperty("data").GetString()!);
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Lilt");
            Directory.CreateDirectory(folder);
            var name = $"{UrlPolicy.DisplayHost(c.Source)} {DateTime.Now:yyyy-MM-dd HHmmss}.png";
            var file = Path.Combine(folder, string.Concat(name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch)));
            await File.WriteAllBytesAsync(file, png);
            var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
            data.SetStorageItems([await Windows.Storage.StorageFile.GetFileFromPathAsync(file)]);
            data.SetBitmap(Windows.Storage.Streams.RandomAccessStreamReference.CreateFromFile(await Windows.Storage.StorageFile.GetFileFromPathAsync(file)));
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
            report.Key("screenshot", $"{png.Length} bytes");
            ShowNotice(new($"Saved the whole page to Pictures\\Lilt and copied it.", NoticeKind.Success, "Show", () =>
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{file}\"")));
        }
        catch (Exception e)
        {
            LiltPaths.LogError(e);
            ShowNotice(new($"Couldn't take the screenshot: {e.Message}", NoticeKind.Error));
        }
    }
}
