using Lilt.App.Browser;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace Lilt.App;

// Extensions: uBlock Origin Lite ships built in; others install from the Chrome Web Store after a signature check.
public sealed partial class MainWindow
{
    const string ProtonPassId = "ghmbeldphafepmbegfdlkpapadhbakde";

    CoreWebView2Profile? Profile => tabs.Select(t => t.Core).FirstOrDefault(c => c is not null)?.Profile;

    async void Extensions_Click(object sender, RoutedEventArgs e)
    {
        await RefreshExtensionsAsync();
        ShowFlyout(ExtensionsFlyout, ExtensionsButton, FlyoutHost);
    }

    async void AdBlockSettings_Click(object sender, RoutedEventArgs e)
    {
        if (Profile is not { } profile) return;
        var ubol = (await engine.ListExtensionsAsync(profile)).FirstOrDefault(x => x.Id == BrowserEngine.UbolId);
        if (ubol?.Options is { } page) AddTab($"chrome-extension://{ubol.Id}/{page}");
        else ShowNotice(new("uBlock Origin Lite isn't installed in this copy of Lilt.", NoticeKind.Warning));
    }

    async Task RefreshExtensionsAsync()
    {
        ExtensionList.Children.Clear();
        if (Profile is not { } profile) return;
        IReadOnlyList<ExtensionInfo> list;
        try
        {
            list = await engine.ListExtensionsAsync(profile);
        }
        catch (Exception e)
        {
            LiltPaths.LogError(e);
            return;
        }
        var body = (Style)Application.Current.Resources["BodyText"];
        foreach (var x in list)
        {
            var row = new Grid { ColumnSpacing = 6 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Style = body, Text = x.Bundled ? $"{x.Name} (built in)" : x.Name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            Grid.SetColumn(actions, 1);
            row.Children.Add(actions);

            var toggle = new ToggleSwitch { IsOn = x.Enabled, OnContent = "", OffContent = "", MinWidth = 0 };
            toggle.Toggled += async (_, _) =>
            {
                try
                {
                    await BrowserEngine.SetEnabledAsync(profile, x.Id, toggle.IsOn);
                }
                catch (Exception e)
                {
                    ShowNotice(new($"Couldn't change {x.Name}: {e.Message}", NoticeKind.Error));
                }
            };
            actions.Children.Add(toggle);
            if (x.Popup is { } popup) actions.Children.Add(SmallButton("Open", () => _ = engine.OpenPopupPageAsync(new Uri($"chrome-extension://{x.Id}/{popup}"))));
            if (x.Options is { } options) actions.Children.Add(SmallButton("Settings", () =>
            {
                ExtensionsFlyout.Hide();
                AddTab($"chrome-extension://{x.Id}/{options}");
            }));
            if (!x.Bundled) actions.Children.Add(SmallButton("Remove", async () =>
            {
                try
                {
                    await engine.RemoveAsync(profile, x.Id);
                }
                catch (Exception e)
                {
                    ShowNotice(new($"Couldn't remove {x.Name}: {e.Message}", NoticeKind.Error));
                }
                await RefreshExtensionsAsync();
            }));
            ExtensionList.Children.Add(row);
        }
        // Google Password Manager can't run outside Chrome; Proton Pass is the suggested replacement.
        if (list.All(x => x.Id != ProtonPassId))
            ExtensionList.Children.Add(SmallButton("Add Proton Pass (password manager)", () => _ = InstallAsync(ProtonPassId)));
    }

    static Button SmallButton(string text, Action click)
    {
        var button = new Button { Content = text, Padding = new Thickness(8, 2, 8, 3), MinHeight = 0 };
        button.Click += (_, _) => click();
        return button;
    }

    void InstallExtension_Click(object sender, RoutedEventArgs e) => _ = InstallAsync(WebStoreBox.Text);

    async Task InstallAsync(string input)
    {
        if (Profile is not { } profile) return;
        InstallExtensionButton.IsEnabled = false;
        InstallExtensionButton.Content = "Installing…";
        try
        {
            var name = await engine.InstallFromWebStoreAsync(profile, input);
            WebStoreBox.Text = "";
            ShowNotice(new($"Installed {name}. Its pages open from the Extensions menu.", NoticeKind.Success));
        }
        catch (Exception e)
        {
            if (e is not (InvalidDataException or HttpRequestException or TaskCanceledException)) LiltPaths.LogError(e);
            ShowNotice(new($"Couldn't install that extension: {e.Message}", NoticeKind.Error));
        }
        finally
        {
            InstallExtensionButton.IsEnabled = true;
            InstallExtensionButton.Content = "Install";
        }
        await RefreshExtensionsAsync();
    }
}
