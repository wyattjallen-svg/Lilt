using Lilt.App.Browser;
using Lilt.App.Shell;
using Lilt.Core;
using Lilt.Core.Layout;
using Lilt.Core.Theming;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
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
        ["sidebar"] = "Sidebar button", ["menu"] = "Menu", ["media"] = "Now playing (while something plays)", ["split"] = "Split view",
        ["palette"] = "Command palette", ["tabs"] = "Tabs", ["bookmarks"] = "Bookmarks", ["layout"] = "Dock and pin buttons",
        ["panel"] = "Panel button", ["web"] = "Sites you keep in the panel", ["visualizer"] = "Visualizer", ["player"] = "Now playing",
        ["logo"] = "Logo", ["clock"] = "Clock and date", ["search"] = "Search box", ["tiles"] = "Bookmark tiles", ["hint"] = "Shortcut hint", ["gear"] = "Customize button",
    };
    DispatcherQueueTimer? applyTimer, themeSaveTimer;
    Theme? pendingTheme;

    // The theme including changes still waiting for the next apply tick.
    Theme Current => pendingTheme ?? theme;
    bool syncing = true, themeLoadFailed;

    void CustomizeFlyout_Opening(object sender, object e)
    {
        SyncCustomize();
        ScaleNow(CustomizeFlyout);
        FitFlyouts();
    }

    void CustomizeTabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var page = sender.SelectedItem?.Tag as string ?? "look";
        LookPage.Visibility = page == "look" ? Visibility.Visible : Visibility.Collapsed;
        LayoutPage.Visibility = page == "layout" ? Visibility.Visible : Visibility.Collapsed;
        HomePage.Visibility = page == "home" ? Visibility.Visible : Visibility.Collapsed;
        MediaPage.Visibility = page == "media" ? Visibility.Visible : Visibility.Collapsed;
        KeysPage.Visibility = page == "keys" ? Visibility.Visible : Visibility.Collapsed;
        PowerPage.Visibility = page == "power" ? Visibility.Visible : Visibility.Collapsed;
        PrivacyPage.Visibility = page == "privacy" ? Visibility.Visible : Visibility.Collapsed;
        if (page == "keys") BuildKeys();
        if (page == "privacy")
        {
            FillPermissions();
            FillStorage();
        }
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
        Select(ScaleCombo, Math.Round(t.Scale * 100).ToString(System.Globalization.CultureInfo.InvariantCulture));
        SidebarWidthSlider.Value = t.Sidebar.Width;
        // "None" has its own tag: an empty Tag doesn't reliably come back as "" from XAML, which made None sometimes do nothing.
        var bg = t.Home.Background;
        ((ComboBoxItem)BackgroundCombo.Items[4]).Visibility = HomeSpec.Scenes.Contains(bg) || bg.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        Select(BackgroundCombo, bg.Length == 0 ? "none" : HomeSpec.Scenes.Contains(bg) ? bg : "image");
        HomeDimSlider.Value = Math.Round(t.Home.Dim * 100);
        HomeDimSlider.IsEnabled = bg.Length > 0 && !HomeSpec.Scenes.Contains(bg);
        HomeGlassCheck.IsChecked = t.Home.Glass;
        EfficiencySwitch.IsOn = t.Power.Efficiency;
        CoveredSwitch.IsOn = t.Power.HideWhenCovered;
        OccludedSwitch.IsOn = t.Power.HideWhenOccluded;
        LeanSwitch.IsOn = t.Power.Lean;
        SuspendSlider.Value = t.Power.SuspendMinutes;
        Select(SplitOrientationCombo, Camel(t.Split.Orientation.ToString()));
        SplitRatioSlider.Value = Math.Round(t.Split.Ratio * 100);
        SplitGapSlider.Value = t.Split.Gap;
        PeekCheck.IsChecked = t.Pages.Peek;
        HistoryCheck.IsChecked = t.Pages.History;
        GlassPagesSwitch.IsOn = t.Pages.Glass;
        GlassOpacitySlider.Value = Math.Round(t.Pages.GlassOpacity * 100);
        GlassOpacitySlider.IsEnabled = t.Pages.Glass;
        if (GlassOffBox.FocusState == FocusState.Unfocused) GlassOffBox.Text = string.Join("\r", t.Pages.GlassOffSites.Items);
        GesturesCheck.IsChecked = t.Pages.Gestures;
        ForceDarkCheck.IsChecked = t.Pages.ForceDark;
        ClassicScrollbarsCheck.IsChecked = t.Pages.Scrollbars == Scrollbars.Classic;
        Select(TrackingCombo, Camel(t.Pages.Tracking.ToString()));
        AnimationsCheck.IsChecked = t.Motion.Animations;
        WindowDragCheck.IsChecked = t.Motion.WindowDrag;
        WindowDragCheck.IsEnabled = DurationSlider.IsEnabled = t.Motion.Animations;
        DurationSlider.Value = t.Motion.Duration;
        TabWidthSlider.Value = t.Sidebar.TabWidth;
        CloseButtonsCheck.IsChecked = t.Sidebar.CloseButtons;
        AddressWidthSlider.Value = t.Toolbar.AddressWidth;
        SleepSlider.Value = t.Power.SleepMinutes;
        LowMemoryCheck.IsChecked = t.Power.SleepWhenLowMemory;
        FpsSlider.Value = t.Media.Fps;
        RestoreCheck.IsChecked = t.Pages.Restore;
        SmoothScrollingCheck.IsChecked = t.Pages.SmoothScrolling;
        SolidAwaySwitch.IsOn = t.Power.SolidWhenAway;
        ToolbarSiteColorSlider.Value = Math.Round(t.Toolbar.SiteColor * 100);
        SidebarSiteColorSlider.Value = Math.Round(t.Sidebar.SiteColor * 100);
        Select(PanelEdgeCombo, Camel(t.Panel.Edge.ToString()));
        PanelWidthSlider.Value = t.Panel.Width;
        CacheLimitSlider.Value = t.Storage.CacheLimit;
        ForgetSlider.Value = t.Storage.ForgetAfterDays;
        SignOutCheck.IsChecked = t.Storage.SignOut;
        SignOutCheck.IsEnabled = t.Storage.ForgetAfterDays > 0;
        if (KeepBox.FocusState == FocusState.Unfocused) KeepBox.Text = string.Join("\r", t.Storage.Keep.Items);
        // The known engines, plus your own address when theme.json has one.
        SearchCombo.Items.Clear();
        foreach (var (key, engine) in Lilt.Core.Navigation.SearchEngines.Known) SearchCombo.Items.Add(new ComboBoxItem { Content = engine.Name, Tag = key });
        if (!Lilt.Core.Navigation.SearchEngines.Known.ContainsKey(t.Pages.Search.ToLowerInvariant()))
            SearchCombo.Items.Add(new ComboBoxItem { Content = Lilt.Core.Navigation.SearchEngines.For(t.Pages.Search).Name + " (yours)", Tag = t.Pages.Search });
        SearchCombo.SelectedItem = SearchCombo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => string.Equals((string)i.Tag, t.Pages.Search, StringComparison.OrdinalIgnoreCase));
        Select(ColorSchemeCombo, Camel(t.Pages.ColorScheme.ToString()));
        var m = t.Media;
        Select(PlayerShowCombo, Camel(m.Player.Show.ToString()));
        PlayerWidthSlider.Value = m.Player.Width;
        PlayerOpacitySlider.Value = Math.Round(m.Player.Opacity * 100);
        Select(VisualizerShowCombo, Camel(m.Visualizer.Show.ToString()));
        Select(VisualizerStyleCombo, Camel(m.Style.ToString()));
        BarsSlider.Value = m.Bars;
        VisualizerWidthSlider.Value = m.Visualizer.Width;
        VisualizerHeightSlider.Value = m.Visualizer.Height;
        VisualizerOpacitySlider.Value = Math.Round(m.Visualizer.Opacity * 100);
        Select(VisualizerSourceCombo, Camel(m.Source.ToString()));
        Select(PipShowCombo, Camel(m.Pip.Show.ToString()));
        if (MusicSitesBox.FocusState == FocusState.Unfocused) MusicSitesBox.Text = string.Join("\r", m.MusicSites.Items);
        AutoPipCheck.IsChecked = m.AutoPictureInPicture;
        MediaKeysCheck.IsChecked = m.MediaKeys;
        if (KeysPage.Visibility == Visibility.Visible) BuildKeys();
        BuildPresets();
        BuildArrange(ToolbarArrange, ToolbarSpec.All, t.Toolbar.Items, ids => Current with { Toolbar = Current.Toolbar with { Items = ids } });
        BuildArrange(SidebarArrange, SidebarSpec.All, t.Sidebar.Sections, ids => Current with { Sidebar = Current.Sidebar with { Sections = ids } });
        BuildArrange(PanelArrange, PanelSpec.All, t.Panel.Sections, ids => Current with { Panel = Current.Panel with { Sections = ids } });
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
            Toolbar = t.Toolbar with { Height = ToolbarHeightSlider.Value, AddressWidth = AddressWidthSlider.Value, SiteColor = ToolbarSiteColorSlider.Value / 100 },
            Sidebar = t.Sidebar with
            {
                Width = SidebarWidthSlider.Value, TabWidth = TabWidthSlider.Value, CloseButtons = CloseButtonsCheck.IsChecked == true,
                SiteColor = SidebarSiteColorSlider.Value / 100,
            },
            Panel = t.Panel with { Edge = Pick(PanelEdgeCombo, t.Panel.Edge), Width = PanelWidthSlider.Value },
            Storage = t.Storage with
            {
                CacheLimit = (int)CacheLimitSlider.Value, ForgetAfterDays = (int)ForgetSlider.Value, SignOut = SignOutCheck.IsChecked == true,
            },
            Motion = new MotionSpec(AnimationsCheck.IsChecked == true, (int)DurationSlider.Value, WindowDragCheck.IsChecked == true),
            Density = DensityCombo.SelectedItem is ComboBoxItem { Tag: string d } && Enum.TryParse<Density>(d, true, out var density) ? density : t.Density,
            Scale = ScaleCombo.SelectedItem is ComboBoxItem { Tag: string pct }
                && double.TryParse(pct, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var percent) ? percent / 100 : t.Scale,
            Home = t.Home with { Glass = HomeGlassCheck.IsChecked == true, Dim = HomeDimSlider.Value / 100 },
            Pages = t.Pages with
            {
                ColorScheme = Pick(ColorSchemeCombo, t.Pages.ColorScheme), Peek = PeekCheck.IsChecked == true,
                Search = SearchCombo.SelectedItem is ComboBoxItem { Tag: string engine } ? engine : t.Pages.Search, History = HistoryCheck.IsChecked == true,
                Glass = GlassPagesSwitch.IsOn, GlassOpacity = GlassOpacitySlider.Value / 100, Gestures = GesturesCheck.IsChecked == true,
                ForceDark = ForceDarkCheck.IsChecked == true, Scrollbars = ClassicScrollbarsCheck.IsChecked == true ? Scrollbars.Classic : Scrollbars.Overlay,
                Tracking = Pick(TrackingCombo, t.Pages.Tracking),
                Restore = RestoreCheck.IsChecked == true, SmoothScrolling = SmoothScrollingCheck.IsChecked == true,
            },
            Power = t.Power with
            {
                Efficiency = EfficiencySwitch.IsOn, SuspendMinutes = (int)SuspendSlider.Value, HideWhenCovered = CoveredSwitch.IsOn,
                HideWhenOccluded = OccludedSwitch.IsOn, Lean = LeanSwitch.IsOn, SleepMinutes = (int)SleepSlider.Value, SleepWhenLowMemory = LowMemoryCheck.IsChecked == true,
                SolidWhenAway = SolidAwaySwitch.IsOn,
            },
            Split = new SplitSpec(Pick(SplitOrientationCombo, t.Split.Orientation), SplitRatioSlider.Value / 100, SplitGapSlider.Value),
            Media = t.Media with
            {
                Player = t.Media.Player with { Show = Pick(PlayerShowCombo, t.Media.Player.Show), Width = PlayerWidthSlider.Value, Opacity = PlayerOpacitySlider.Value / 100 },
                Visualizer = t.Media.Visualizer with
                {
                    Show = Pick(VisualizerShowCombo, t.Media.Visualizer.Show), Width = VisualizerWidthSlider.Value,
                    Height = VisualizerHeightSlider.Value, Opacity = VisualizerOpacitySlider.Value / 100,
                },
                Pip = t.Media.Pip with { Show = Pick(PipShowCombo, t.Media.Pip.Show) },
                Bars = (int)BarsSlider.Value,
                Fps = (int)FpsSlider.Value,
                Style = Pick(VisualizerStyleCombo, t.Media.Style),
                Source = Pick(VisualizerSourceCombo, t.Media.Source),
                MediaKeys = MediaKeysCheck.IsChecked == true,
                AutoPictureInPicture = AutoPipCheck.IsChecked == true,
            },
        });
        // Changing when the player or visualizer shows starts over from that setting.
        if (ReferenceEquals(sender, PlayerShowCombo)) playerToggle = null;
        if (ReferenceEquals(sender, VisualizerShowCombo)) visualizerToggle = null;
    }

    static T Pick<T>(ComboBox box, T fallback) where T : struct, Enum =>
        box.SelectedItem is ComboBoxItem { Tag: string tag } && Enum.TryParse<T>(tag, true, out var v) ? v : fallback;

    // One site per line; anything that isn't a host name ("open.spotify.com") is left out.
    static string[] Hosts(string text) => text.Split(['\r', '\n', ',', ' '], StringSplitOptions.RemoveEmptyEntries)
        .Select(h => (Uri.TryCreate(h.Contains("://") ? h : "https://" + h, UriKind.Absolute, out var u) ? u.Host : "").ToLowerInvariant())
        .Where(h => Uri.CheckHostName(h) == UriHostNameType.Dns).Distinct().Take(200).ToArray();

    void MusicSites_LostFocus(object sender, RoutedEventArgs e)
    {
        var hosts = Hosts(MusicSitesBox.Text);
        if (hosts.SequenceEqual(Current.Media.MusicSites.Items)) return;
        Queue(Current with { Media = Current.Media with { MusicSites = new Ids(hosts) } });
    }

    void Keep_LostFocus(object sender, RoutedEventArgs e)
    {
        var hosts = Hosts(KeepBox.Text);
        if (hosts.SequenceEqual(Current.Storage.Keep.Items)) return;
        Queue(Current with { Storage = Current.Storage with { Keep = new Ids(hosts) } });
    }

    void GlassOff_LostFocus(object sender, RoutedEventArgs e)
    {
        var hosts = Hosts(GlassOffBox.Text).Select(h => h.StartsWith("www.", StringComparison.Ordinal) ? h[4..] : h).Distinct().ToArray();
        if (hosts.SequenceEqual(Current.Pages.GlassOffSites.Items)) return;
        Queue(Current with { Pages = Current.Pages with { GlassOff = new Ids(hosts) } });
    }

    // Keys: one row per command with its shortcut. Click the box and press keys to change it; Backspace clears it.
    void BuildKeys()
    {
        KeysList.Children.Clear();
        var map = Current.Shortcuts;
        foreach (var (id, label) in Commands.Labels)
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            row.Children.Add(new TextBlock { Text = label + (Commands.Reserved.Contains(id) ? " *" : ""), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
            var box = new TextBox { Text = string.Join(", ", map.For(id).Select(g => g.ToString())), IsReadOnly = true, PlaceholderText = "None", Tag = id };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, $"Shortcut for {label}");
            box.PreviewKeyDown += Shortcut_PreviewKeyDown;
            Grid.SetColumn(box, 1);
            row.Children.Add(box);
            KeysList.Children.Add(row);
        }
    }

    void Shortcut_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not TextBox { Tag: string id } box) return;
        static bool Down(Windows.System.VirtualKey k) => InputKeyboardSource.GetKeyStateForCurrentThread(k).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        bool ctrl = Down(Windows.System.VirtualKey.Control), shift = Down(Windows.System.VirtualKey.Shift), alt = Down(Windows.System.VirtualKey.Menu);
        var key = (int)e.Key;
        // Plain Tab and Escape keep moving focus and closing the panel.
        if (e.Key is Windows.System.VirtualKey.Control or Windows.System.VirtualKey.Shift or Windows.System.VirtualKey.Menu
            or Windows.System.VirtualKey.LeftWindows or Windows.System.VirtualKey.RightWindows) { e.Handled = true; return; }
        if (!ctrl && !alt && e.Key is Windows.System.VirtualKey.Tab or Windows.System.VirtualKey.Escape) return;
        e.Handled = true;
        var map = Current.Shortcuts.Map.ToDictionary(p => p.Key, p => p.Value);
        if (!ctrl && !alt && !shift && e.Key is Windows.System.VirtualKey.Back or Windows.System.VirtualKey.Delete) map[id] = [];
        else
        {
            var name = key switch
            {
                >= 65 and <= 90 or >= 48 and <= 57 => ((char)key).ToString(),
                >= 112 and <= 135 => "F" + (key - 111),
                186 => ";", 187 => "=", 188 => ",", 189 => "-", 190 => ".", 191 => "/", 192 => "`", 219 => "[", 220 => "\\", 221 => "]", 222 => "'",
                _ => e.Key switch
                {
                    Windows.System.VirtualKey.Tab => "Tab", Windows.System.VirtualKey.Space => "Space", Windows.System.VirtualKey.Enter => "Enter",
                    Windows.System.VirtualKey.Left => "Left", Windows.System.VirtualKey.Right => "Right", Windows.System.VirtualKey.Up => "Up",
                    Windows.System.VirtualKey.Down => "Down", Windows.System.VirtualKey.Home => "Home", Windows.System.VirtualKey.End => "End",
                    Windows.System.VirtualKey.PageUp => "PageUp", Windows.System.VirtualKey.PageDown => "PageDown", Windows.System.VirtualKey.Delete => "Delete",
                    Windows.System.VirtualKey.Insert => "Insert", Windows.System.VirtualKey.Back => "Backspace", Windows.System.VirtualKey.Escape => "Escape",
                    _ => null,
                },
            };
            var text = string.Concat(ctrl ? "Ctrl+" : "", shift ? "Shift+" : "", alt ? "Alt+" : "", name);
            if (name is null || !Gesture.TryParse(text, out var g))
            {
                box.Text = name is null ? "That key can't be used" : $"{text} needs Ctrl or Alt";
                return;
            }
            // A combination belongs to one command: taking it here removes it elsewhere.
            foreach (var other in map.Keys.ToArray()) map[other] = map[other].Where(x => x != g).ToArray();
            map[id] = [g];
        }
        Queue(Current with { Shortcuts = new ShortcutMap(map) });
        BuildKeys();
        DispatcherQueue.TryEnqueue(() =>
        {
            foreach (var row in KeysList.Children.OfType<Grid>())
                if (row.Children.OfType<TextBox>().FirstOrDefault(b => (string)b.Tag == id) is { } again) again.Focus(FocusState.Programmatic);
        });
    }

    void ResetKeys_Click(object sender, RoutedEventArgs e)
    {
        Queue(Current with { Shortcuts = ShortcutMap.Default });
        BuildKeys();
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
        if (syncing || BackgroundCombo.SelectedItem is not ComboBoxItem { Tag: string tag } || tag == "image") return;
        if (tag != "pick")
        {
            Queue(Current with { Home = Current.Home with { Background = tag == "none" ? "" : tag } });
            HomeDimSlider.IsEnabled = false;
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
    // Presets: Lilt's (in the app's Themes folder) in a fixed order, then yours (the data folder's "themes").
    static readonly string[] presetOrder = ["minimal", "glass", "light", "midnight", "paper"];

    List<(string Name, string Path, bool Mine)> Presets()
    {
        var list = new List<(string, string, bool)>();
        try
        {
            foreach (var file in Directory.GetFiles(LiltPaths.Bundled, "*.json").Where(f => !f.EndsWith("schema.json", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => Array.IndexOf(presetOrder, System.IO.Path.GetFileNameWithoutExtension(f)) is var i && i < 0 ? 99 : i))
                list.Add((ThemeParser.LoadFile(file).Theme.Name, file, false));
            if (Directory.Exists(paths.Presets))
                foreach (var file in Directory.GetFiles(paths.Presets, "*.json").Order())
                    list.Add((ThemeParser.LoadFile(file).Theme.Name, file, true));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            LiltPaths.LogError(e);
        }
        return list;
    }

    void BuildPresets()
    {
        PresetList.Children.Clear();
        foreach (var (name, path, mine) in Presets())
        {
            var button = new Button { Content = name, Tag = path, HorizontalAlignment = HorizontalAlignment.Stretch };
            if (name == theme.Name) button.BorderBrush = ThemeResources.Get("LiltAccentBrush");
            button.Click += (_, _) => ApplyPreset(path);
            if (mine)
            {
                ToolTipService.SetToolTip(button, "Your preset · right-click to delete");
                var menu = new MenuFlyout();
                var delete = new MenuFlyoutItem { Text = $"Delete “{name}”" };
                delete.Click += (_, _) =>
                {
                    try
                    {
                        File.Delete(path);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        ShowNotice(new($"Couldn't delete it: {ex.Message}", NoticeKind.Error));
                    }
                    BuildPresets();
                };
                menu.Items.Add(delete);
                button.ContextFlyout = menu;
            }
            PresetList.Children.Add(button);
        }
    }

    async void SavePreset_Click(object sender, RoutedEventArgs e)
    {
        var box = new TextBox { Header = "Name", Text = Current.Name, MaxLength = 40 };
        CustomizeFlyout.Hide();
        if (await AskAsync("Save this look as a preset", box, "Save") != ContentDialogResult.Primary) return;
        var name = box.Text.Trim();
        var file = string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' ? c : '_')).Trim();
        if (file.Length == 0)
        {
            ShowNotice(new("A preset needs a name.", NoticeKind.Warning));
            return;
        }
        try
        {
            Directory.CreateDirectory(paths.Presets);
            Files.WriteAtomic(System.IO.Path.Combine(paths.Presets, file + ".json"), ThemeWriter.ToJson(Current with { Name = name }));
            Queue(Current with { Name = name });
            ShowNotice(new($"Saved “{name}”. It's in Customize → Look with the other presets.", NoticeKind.Success, "Show", () => ShowCustomize("look")));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowNotice(new($"Couldn't save the preset: {ex.Message}", NoticeKind.Error));
        }
    }

    // A preset changes the look; where things are (sidebar, toolbar order, home layout), behavior and shortcuts stay yours.
    // Your home background stays too, unless it's one of your own presets.
    void ApplyPreset(string path)
    {
        var result = ThemeParser.LoadFile(path);
        if (result.Failed) return;
        themeLoadFailed = false;
        var p = result.Theme;
        var cur = Current;
        var mine = path.StartsWith(paths.Presets, StringComparison.OrdinalIgnoreCase);
        pendingTheme = null;
        ApplyTheme(p with
        {
            Sidebar = cur.Sidebar,
            Toolbar = cur.Toolbar with { Height = p.Toolbar.Height },
            Home = cur.Home with { Glass = p.Home.Glass, Background = mine ? p.Home.Background : cur.Home.Background, Dim = mine ? p.Home.Dim : cur.Home.Dim },
            Power = cur.Power,
            Pages = cur.Pages with { Glass = p.Pages.Glass, GlassOpacity = p.Pages.GlassOpacity },
            Media = cur.Media,
            Split = cur.Split,
            Shortcuts = cur.Shortcuts,
            Panel = cur.Panel,
            Storage = cur.Storage,
        }, theme);
        SyncCustomize();
        SaveTheme();
    }

    // Layout presets from the design: where the sidebar sits, how wide, and which sections it shows.
    void LayoutPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string name }) ApplyLayout(name);
    }

    void ApplyLayout(string name)
    {
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
