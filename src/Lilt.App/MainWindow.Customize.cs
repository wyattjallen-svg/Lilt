using Lilt.App.Shell;
using Lilt.Core.Layout;
using Lilt.Core.Theming;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace Lilt.App;

// The Customize panel edits the theme live and writes it back to theme.json, so the file and the panel never disagree.
// Every element of the toolbar, sidebar and home tab can be moved (arrows) or removed (check box) here.
public sealed partial class MainWindow
{
    static readonly string[] accents = ["#638B7B", "#8DB5A4", "#687C93", "#E6D6CC", "#C9A86A", "#C98A8A"];
    static readonly string[] fonts = ["Segoe UI Variable Text", "Segoe UI", "Bahnschrift", "Aptos", "Calibri", "Cascadia Code", "Georgia"];
    static readonly string[] colorTokens = ["accent", "background", "surface", "surfaceRaised", "border", "text", "textMuted", "onAccent", "hover", "pressed", "page"];
    static readonly Dictionary<string, string> labels = new()
    {
        ["back"] = "Back", ["forward"] = "Forward", ["reload"] = "Reload", ["home"] = "Home button", ["address"] = "Address bar",
        ["bookmark"] = "Bookmark star", ["downloads"] = "Downloads", ["extensions"] = "Extensions", ["customize"] = "Customize",
        ["sidebar"] = "Sidebar button", ["menu"] = "Menu", ["tabs"] = "Tabs", ["bookmarks"] = "Bookmarks", ["layout"] = "Dock and pin buttons",
        ["logo"] = "Logo", ["clock"] = "Clock and date", ["search"] = "Search box", ["tiles"] = "Bookmark tiles", ["hint"] = "Shortcut hint", ["gear"] = "Customize button",
    };
    DispatcherQueueTimer? applyTimer, themeSaveTimer;
    Theme? pendingTheme;

    // The theme including changes still waiting for the next apply tick.
    Theme Current => pendingTheme ?? theme;
    bool syncing = true, themeLoadFailed;

    void CustomizeFlyout_Opening(object sender, object e) => SyncCustomize();

    void CustomizeTabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var page = sender.SelectedItem?.Tag as string ?? "look";
        LookPage.Visibility = page == "look" ? Visibility.Visible : Visibility.Collapsed;
        LayoutPage.Visibility = page == "layout" ? Visibility.Visible : Visibility.Collapsed;
        HomePage.Visibility = page == "home" ? Visibility.Visible : Visibility.Collapsed;
        PowerPage.Visibility = page == "power" ? Visibility.Visible : Visibility.Collapsed;
    }

    void SyncCustomize()
    {
        syncing = true;
        var t = theme;
        Select(MaterialCombo, Camel(t.Backdrop.Material.ToString()));
        TintSlider.Value = Math.Round((t.Backdrop.TintOpacity ?? 0.5) * 100);
        TintSlider.IsEnabled = t.Backdrop.Material != Backdrop.None;
        ChromeOpacitySlider.Value = Math.Round(t.Colors.Surface.A / 2.55);
        if (ColorTokenCombo.Items.Count == 0)
        {
            foreach (var token in colorTokens) ColorTokenCombo.Items.Add(token);
            ColorTokenCombo.SelectedIndex = 0;
        }
        TokenPicker.Color = ThemeResources.ToColor(Token(t.Colors, (string)ColorTokenCombo.SelectedItem));
        if (FontCombo.Items.Count == 0) foreach (var f in fonts) FontCombo.Items.Add(f);
        FontCombo.Text = t.Typography.Family;
        FontSizeSlider.Value = t.Typography.Size;
        RadiusSlider.Value = t.Shape.Radius;
        ToolbarHeightSlider.Value = t.Toolbar.Height;
        Select(DensityCombo, Camel(t.Density.ToString()));
        SidebarWidthSlider.Value = t.Sidebar.Width;
        Select(BackgroundCombo, t.Home.Background is "" or "hills" ? t.Home.Background : "pick");
        HomeGlassCheck.IsChecked = t.Home.Glass;
        EfficiencySwitch.IsOn = t.Power.Efficiency;
        CoveredSwitch.IsOn = t.Power.HideWhenCovered;
        SuspendSlider.Value = t.Power.SuspendMinutes;
        BuildArrange(ToolbarArrange, ToolbarSpec.All, t.Toolbar.Items, ids => Current with { Toolbar = Current.Toolbar with { Items = ids } });
        BuildArrange(SidebarArrange, SidebarSpec.All, t.Sidebar.Sections, ids => Current with { Sidebar = Current.Sidebar with { Sections = ids } });
        BuildArrange(HomeArrange, HomeSpec.All, t.Home.Items, ids => Current with { Home = Current.Home with { Items = ids } });
        syncing = false;

        static void Select(ComboBox box, string tag) => box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == tag);
        static string Camel(string s) => char.ToLowerInvariant(s[0]) + s[1..];
    }

    // One row per element: a check box to show or hide it, arrows to move it. Shown elements come first, in order.
    void BuildArrange(StackPanel panel, string[] all, Ids current, Func<Ids, Theme> with)
    {
        panel.Children.Clear();
        var order = current.Items.Concat(all.Where(id => !current.Has(id))).ToList();
        for (var i = 0; i < order.Count; i++)
        {
            var id = order[i];
            var index = i;
            var row = new Grid { ColumnSpacing = 2 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var check = new CheckBox { Content = labels.GetValueOrDefault(id, id), IsChecked = current.Has(id), MinWidth = 0 };
            check.Click += (_, _) => Commit(order.Where(x => x == id ? check.IsChecked == true : current.Has(x)).ToArray());
            row.Children.Add(check);
            foreach (var (glyph, step, column) in new[] { ("", -1, 1), ("", 1, 2) })
            {
                var move = new Button
                {
                    Style = (Style)Application.Current.Resources["ChromeButton"],
                    Width = 28, Height = 28,
                    Content = new FontIcon { Glyph = glyph, FontSize = 11 },
                    IsEnabled = index + step >= 0 && index + step < order.Count,
                };
                ToolTipService.SetToolTip(move, step < 0 ? "Move up" : "Move down");
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(move, $"Move {labels.GetValueOrDefault(id, id)} {(step < 0 ? "up" : "down")}");
                move.Click += (_, _) =>
                {
                    var moved = order.ToList();
                    (moved[index], moved[index + step]) = (moved[index + step], moved[index]);
                    Commit(moved.Where(current.Has).ToArray());
                };
                Grid.SetColumn(move, column);
                row.Children.Add(move);
            }
            panel.Children.Add(row);
        }

        void Commit(string[] ids)
        {
            Queue(with(new Ids(ids)));
            BuildArrange(panel, all, new Ids(ids), with);
        }
    }

    // Wired to sliders, combo boxes, check boxes and switches alike, hence the loose signature.
    void Customize_Changed(object sender, object e)
    {
        if (syncing) return;
        var t = Current;
        var material = MaterialCombo.SelectedItem is ComboBoxItem { Tag: string m } && Enum.TryParse<Backdrop>(m, true, out var b) ? b : t.Backdrop.Material;
        var backdrop = t.Backdrop with { Material = material };
        if (ReferenceEquals(sender, TintSlider))
            backdrop = backdrop with { Tint = backdrop.Tint ?? t.Colors.Background.Opaque, TintOpacity = TintSlider.Value / 100 };
        TintSlider.IsEnabled = material != Backdrop.None;
        var radius = RadiusSlider.Value;
        var family = FontCombo.SelectedItem as string ?? t.Typography.Family;
        Queue(t with
        {
            Backdrop = backdrop,
            Colors = t.Colors with { Surface = t.Colors.Surface.WithAlpha((byte)Math.Round(ChromeOpacitySlider.Value * 2.55)) },
            Shape = t.Shape with { Radius = radius, ControlRadius = Math.Round(radius * 0.75) },
            Typography = t.Typography with { Size = FontSizeSlider.Value, Family = family },
            Toolbar = t.Toolbar with { Height = ToolbarHeightSlider.Value },
            Sidebar = t.Sidebar with { Width = SidebarWidthSlider.Value },
            Density = DensityCombo.SelectedItem is ComboBoxItem { Tag: string d } && Enum.TryParse<Density>(d, true, out var density) ? density : t.Density,
            Home = t.Home with { Glass = HomeGlassCheck.IsChecked == true },
            Power = new PowerSpec(EfficiencySwitch.IsOn, (int)SuspendSlider.Value, CoveredSwitch.IsOn),
        });
    }

    void FontCombo_TextSubmitted(ComboBox sender, ComboBoxTextSubmittedEventArgs args)
    {
        if (string.IsNullOrWhiteSpace(args.Text) || args.Text.Length > 100) return;
        args.Handled = true;
        Queue(Current with { Typography = Current.Typography with { Family = args.Text.Trim() } });
    }

    void ColorToken_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ColorTokenCombo.SelectedItem is not string token) return;
        var was = syncing;
        syncing = true;
        TokenPicker.Color = ThemeResources.ToColor(Token(theme.Colors, token));
        syncing = was;
    }

    void TokenPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (syncing || ColorTokenCombo.SelectedItem is not string token) return;
        var c = args.NewColor;
        Queue(Current with { Colors = SetToken(Current.Colors, token, new Rgba(c.R, c.G, c.B, c.A)) });
    }

    static Rgba Token(ThemeColors c, string token) => token switch
    {
        "background" => c.Background, "surface" => c.Surface, "surfaceRaised" => c.SurfaceRaised, "border" => c.Border,
        "text" => c.Text, "textMuted" => c.TextMuted, "onAccent" => c.OnAccent, "hover" => c.Hover, "pressed" => c.Pressed,
        "page" => c.Page, _ => c.Accent,
    };

    static ThemeColors SetToken(ThemeColors c, string token, Rgba v) => token switch
    {
        "background" => c with { Background = v }, "surface" => c with { Surface = v }, "surfaceRaised" => c with { SurfaceRaised = v },
        "border" => c with { Border = v }, "text" => c with { Text = v }, "textMuted" => c with { TextMuted = v },
        "onAccent" => c with { OnAccent = v }, "hover" => c with { Hover = v }, "pressed" => c with { Pressed = v },
        "page" => c with { Page = v }, _ => c with { Accent = v },
    };

    async void Background_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (syncing || BackgroundCombo.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
        if (tag != "pick")
        {
            Queue(Current with { Home = Current.Home with { Background = tag } });
            return;
        }
        // The picked image is copied into the newtab folder, which is all the home page can read.
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            foreach (var type in new[] { ".jpg", ".jpeg", ".png", ".webp", ".gif" }) picker.FileTypeFilter.Add(type);
            var file = await picker.PickSingleFileAsync();
            if (file is null)
            {
                SyncCustomize();
                return;
            }
            if (new FileInfo(file.Path).Length > 25 * 1024 * 1024) throw new InvalidDataException("That image is over 25 MB.");
            var name = "background" + System.IO.Path.GetExtension(file.Path).ToLowerInvariant();
            foreach (var old in Directory.GetFiles(paths.NewTab, "background.*")) File.Delete(old);
            File.Copy(file.Path, System.IO.Path.Combine(paths.NewTab, name), overwrite: true);
            homeDataStale = true;
            Queue(Current with { Home = Current.Home with { Background = name } });
        }
        catch (Exception ex)
        {
            ShowNotice(new($"Couldn't use that image: {ex.Message}", NoticeKind.Error));
            SyncCustomize();
        }
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

    // A theme preset changes the look; where things are (sidebar, toolbar order, home layout, power) stays yours.
    void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string name }) return;
        var result = ThemeParser.LoadFile(System.IO.Path.Combine(LiltPaths.Bundled, name + ".json"));
        if (result.Failed) return;
        themeLoadFailed = false;
        var p = result.Theme;
        var cur = Current;
        pendingTheme = null;
        ApplyTheme(p with
        {
            Sidebar = cur.Sidebar,
            Toolbar = cur.Toolbar with { Height = p.Toolbar.Height },
            Home = cur.Home with { Glass = p.Home.Glass, Background = cur.Home.Background is "" or "hills" ? p.Home.Background : cur.Home.Background },
            Power = cur.Power,
        }, theme);
        SyncCustomize();
        SaveTheme();
    }

    // Layout presets from the design: where the sidebar sits, how wide, and which sections it shows.
    void LayoutPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string name }) return;
        var cur = Current;
        pendingTheme = null;
        var s = cur.Sidebar;
        var all = new Ids(SidebarSpec.All);
        var next = name switch
        {
            "research" => s with { Edge = DockEdge.Left, Width = 320, AutoHide = false, Sections = all },
            "vertical" => s with { Edge = DockEdge.Left, Width = 240, AutoHide = false, Sections = new Ids("tabs", "layout") },
            "top" => s with { Edge = DockEdge.Top, Height = 44, AutoHide = false, Sections = new Ids("tabs", "layout") },
            "bottom" => s with { Edge = DockEdge.Bottom, Height = 44, AutoHide = false, Sections = all },
            _ => s with { Edge = DockEdge.Left, Width = 220, AutoHide = false, Sections = all },
        };
        sidebar.Edge = next.Edge;
        sidebar.SetAutoHide(false);
        ApplyTheme(cur with { Sidebar = next }, theme);
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
                Queue(Current with { Colors = Current.Colors with { Accent = color, OnAccent = light ? Rgba.Parse("#171A1E") : Rgba.Parse("#F3F4F1") } });
            };
            AccentSwatches.Children.Add(button);
        }
    }
}
