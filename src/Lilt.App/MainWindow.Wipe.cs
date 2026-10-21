using System.Globalization;
using Lilt.App.Browser;
using Lilt.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Lilt.App;

// Wipe everything and start fresh (Customize → Privacy, the menu or Ctrl+K). A four-digit code shown in the dialog has
// to be entered, on its keypad or the keyboard, before the button works, and Enter means Cancel, so it can't happen by
// accident. The wipe itself runs on the next start (see DataWipe), when nothing has Lilt's files open.
public sealed partial class MainWindow
{
    ContentDialog? openDialog;
    IReadOnlyList<string>? wipeLeft;

    async Task WipeAsync()
    {
        if (options.InPrivate)
        {
            ShowNotice(new("An InPrivate window keeps nothing to wipe. Open Lilt normally to start over.", NoticeKind.Info));
            return;
        }
        if (openDialog is not null) return;
        var code = Random.Shared.Next(1000, 10000).ToString(CultureInfo.InvariantCulture);
        var entry = new TextBox
        {
            MaxLength = 4, FontSize = 22, Width = 168, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
            PlaceholderText = "····", IsSpellCheckEnabled = false,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(entry, "Confirmation code");
        var keepBookmarks = new CheckBox { Content = "Keep my bookmarks" };
        var keepLook = new CheckBox { Content = "Keep my look (theme and presets)" };

        var keypad = new Grid { ColumnSpacing = 6, RowSpacing = 6, HorizontalAlignment = HorizontalAlignment.Center };
        for (var i = 0; i < 3; i++) keypad.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        for (var i = 0; i < 4; i++) keypad.RowDefinitions.Add(new RowDefinition { Height = new GridLength(44) });
        string[] keys = ["1", "2", "3", "4", "5", "6", "7", "8", "9", "Clear", "0", ""];
        for (var i = 0; i < keys.Length; i++)
        {
            var key = keys[i];
            var button = new Button
            {
                Content = key == "" ? new FontIcon { Glyph = key, FontSize = 14 } : new TextBlock { Text = key, FontSize = key.Length == 1 ? 18 : 12 },
                HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, IsTabStop = false,
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, key == "" ? "Delete a digit" : key);
            button.Click += (_, _) =>
            {
                entry.Text = key switch
                {
                    "Clear" => "",
                    "" => entry.Text.Length > 0 ? entry.Text[..^1] : "",
                    _ when entry.Text.Length < 4 => entry.Text + key,
                    _ => entry.Text,
                };
                entry.SelectionStart = entry.Text.Length;
            };
            Grid.SetRow(button, i / 3);
            Grid.SetColumn(button, i % 3);
            keypad.Children.Add(button);
        }

        var panel = new StackPanel { Spacing = 10, MinWidth = 340 };
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = "This deletes everything Lilt keeps: tabs, workspaces, bookmarks, history, settings, extensions, cookies and sign-ins. " +
                "Lilt then restarts as a fresh install. It can't be undone.",
        });
        panel.Children.Add(keepBookmarks);
        panel.Children.Add(keepLook);
        panel.Children.Add(new TextBlock
        {
            Text = $"To confirm, enter  {string.Join("  ", code.Select(c => c.ToString()))}",
            FontWeight = new Windows.UI.Text.FontWeight(600), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0),
        });
        panel.Children.Add(entry);
        panel.Children.Add(keypad);

        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "Wipe everything and start fresh?",
            Content = panel,
            PrimaryButtonText = "Wipe and restart",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            IsPrimaryButtonEnabled = false,
            RequestedTheme = Root.RequestedTheme,
        };
        entry.TextChanged += (_, _) =>
        {
            var digits = new string(entry.Text.Where(char.IsAsciiDigit).ToArray());
            if (digits != entry.Text)
            {
                entry.Text = digits;
                entry.SelectionStart = digits.Length;
                return;
            }
            dialog.IsPrimaryButtonEnabled = digits == code;
        };
        openDialog = dialog;
        ContentDialogResult result;
        try
        {
            result = await dialog.ShowAsync();
        }
        catch (Exception)
        {
            return;
        }
        finally
        {
            openDialog = null;
        }
        if (result != ContentDialogResult.Primary || entry.Text != code) return;

        var keep = new List<string>();
        if (keepBookmarks.IsChecked == true) keep.Add("bookmarks");
        if (keepLook.IsChecked == true) keep.Add("look");
        try
        {
            DataWipe.Request(paths.Root, keep, Environment.ProcessId);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShowNotice(new($"Couldn't start the wipe: {e.Message}", NoticeKind.Error));
            return;
        }
        report.Key("wipe", "requested" + (keep.Count > 0 ? ", keeping " + string.Join(", ", keep) : ""));
        Restart();
    }

    // Runs before anything opens Lilt's files.
    void WipeIfRequested()
    {
        if (options.InPrivate) return;
        var presets = Directory.Exists(LiltPaths.Bundled) ? Directory.EnumerateFiles(LiltPaths.Bundled, "*.json").Select(f => Path.GetFileName(f)) : Enumerable.Empty<string>();
        wipeLeft = DataWipe.RunIfRequested(paths.Root, presets, TimeSpan.FromSeconds(20));
    }

    void Wipe_Click(object sender, RoutedEventArgs e) => _ = WipeAsync();
}
