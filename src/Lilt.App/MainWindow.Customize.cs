using Lilt.App.Shell;
using Lilt.Core.Theming;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace Lilt.App;

// The Customize panel edits the theme live and writes it back to theme.json, so the file and the panel never disagree.
public sealed partial class MainWindow
{
    static readonly string[] accents = ["#C4A7FF", "#9CC9FF", "#7FD1AE", "#FFB86B", "#FF8FA3", "#E8E6E3"];
    DispatcherQueueTimer? applyTimer, themeSaveTimer;
    Theme? pendingTheme;
    bool syncing = true, themeLoadFailed;

    void CustomizeFlyout_Opening(object sender, object e) => SyncCustomize();

    void SyncCustomize()
    {
        syncing = true;
        var t = theme;
        Select(MaterialCombo, Camel(t.Backdrop.Material.ToString()));
        TintSlider.Value = Math.Round((t.Backdrop.TintOpacity ?? 0.5) * 100);
        ChromeOpacitySlider.Value = Math.Round(t.Colors.Surface.A / 2.55);
        RadiusSlider.Value = t.Shape.Radius;
        FontSizeSlider.Value = t.Typography.Size;
        SidebarWidthSlider.Value = t.Sidebar.Width;
        Select(DensityCombo, Camel(t.Density.ToString()));
        HomeClockCheck.IsChecked = t.Home.Clock;
        HomeSearchCheck.IsChecked = t.Home.Search;
        HomeTilesCheck.IsChecked = t.Home.Tiles;
        HomeGlassCheck.IsChecked = t.Home.Glass;
        TintSlider.IsEnabled = t.Backdrop.Material != Backdrop.None;
        syncing = false;

        static void Select(ComboBox box, string tag) => box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == tag);
        static string Camel(string s) => char.ToLowerInvariant(s[0]) + s[1..];
    }

    // Wired to sliders, combo boxes and check boxes alike, hence the loose signature.
    void Customize_Changed(object sender, object e)
    {
        if (syncing) return;
        var t = theme;
        var material = MaterialCombo.SelectedItem is ComboBoxItem { Tag: string m } && Enum.TryParse<Backdrop>(m, true, out var b) ? b : t.Backdrop.Material;
        var backdrop = t.Backdrop with { Material = material };
        if (ReferenceEquals(sender, TintSlider))
            backdrop = backdrop with { Tint = backdrop.Tint ?? t.Colors.Background.Opaque, TintOpacity = TintSlider.Value / 100 };
        TintSlider.IsEnabled = material != Backdrop.None;
        var radius = RadiusSlider.Value;
        Queue(t with
        {
            Backdrop = backdrop,
            Colors = t.Colors with { Surface = t.Colors.Surface.WithAlpha((byte)Math.Round(ChromeOpacitySlider.Value * 2.55)) },
            Shape = t.Shape with { Radius = radius, ControlRadius = Math.Round(radius * 0.75) },
            Typography = t.Typography with { Size = FontSizeSlider.Value },
            Sidebar = t.Sidebar with { Width = SidebarWidthSlider.Value },
            Density = DensityCombo.SelectedItem is ComboBoxItem { Tag: string d } && Enum.TryParse<Density>(d, true, out var density) ? density : t.Density,
            Home = new HomeSpec(HomeClockCheck.IsChecked == true, HomeSearchCheck.IsChecked == true, HomeTilesCheck.IsChecked == true, HomeGlassCheck.IsChecked == true),
        });
    }

    // Applies at most every 50 ms while a slider is dragged, and saves once things settle.
    void Queue(Theme t)
    {
        pendingTheme = t;
        if (applyTimer is null)
        {
            applyTimer = DispatcherQueue.CreateTimer();
            applyTimer.Interval = TimeSpan.FromMilliseconds(50);
            applyTimer.IsRepeating = false;
            applyTimer.Tick += (_, _) =>
            {
                if (pendingTheme is { } next) ApplyTheme(next, theme);
                pendingTheme = null;
            };
            themeSaveTimer = DispatcherQueue.CreateTimer();
            themeSaveTimer.Interval = TimeSpan.FromMilliseconds(600);
            themeSaveTimer.IsRepeating = false;
            themeSaveTimer.Tick += (_, _) => SaveTheme();
        }
        if (!applyTimer.IsRunning) applyTimer.Start();
        themeSaveTimer!.Stop();
        themeSaveTimer.Start();
    }

    void SaveTheme()
    {
        if (pendingTheme is { } next)
        {
            ApplyTheme(next, theme);
            pendingTheme = null;
        }
        // Never overwrite a theme file that failed to load; the person is probably mid-edit.
        if (themeLoadFailed)
        {
            ThemeStatus.Text = "Not saved: fix the errors in theme.json first";
            return;
        }
        try
        {
            themes.Save(theme);
            ThemeStatus.Text = $"Saved {DateTime.Now:t}{backdropNote}";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            LiltPaths.LogError(e);
            ThemeStatus.Text = $"Couldn't save: {e.Message}";
        }
    }

    void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string name }) return;
        var result = ThemeParser.LoadFile(System.IO.Path.Combine(LiltPaths.Bundled, name + ".json"));
        if (result.Failed) return;
        // A preset changes the look; where the sidebar sits stays yours.
        themeLoadFailed = false;
        ApplyTheme(result.Theme with { Sidebar = theme.Sidebar }, theme);
        SyncCustomize();
        SaveTheme();
    }

    void BuildAccentSwatches()
    {
        foreach (var hex in accents)
        {
            var color = Rgba.Parse(hex);
            var button = new Button
            {
                Style = (Style)Application.Current.Resources["ChromeButton"],
                Width = 28,
                Height = 28,
                Content = new Ellipse { Width = 18, Height = 18, Fill = new SolidColorBrush(ThemeResources.ToColor(color)) },
            };
            ToolTipService.SetToolTip(button, hex);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, $"Accent {hex}");
            button.Click += (_, _) =>
            {
                var light = 0.299 * color.R + 0.587 * color.G + 0.114 * color.B > 150;
                Queue(theme with { Colors = theme.Colors with { Accent = color, OnAccent = light ? Rgba.Parse("#1A1325") : Rgba.Parse("#FFFFFF") } });
            };
            AccentSwatches.Children.Add(button);
        }
    }
}
