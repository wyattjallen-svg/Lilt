using Lilt.Core.Theming;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

namespace Lilt.App.Shell;

// Maps theme tokens onto the shared brushes declared in App.xaml. Brushes are updated in place,
// so every element using them repaints on live reload without re-templating.
static class ThemeResources
{
    static readonly (string Key, Func<ThemeColors, Rgba> Pick)[] map =
    [
        ("LiltBackgroundBrush", c => c.Background),
        ("LiltSurfaceBrush", c => c.Surface),
        ("LiltSurfaceRaisedBrush", c => c.SurfaceRaised),
        ("LiltBorderBrush", c => c.Border),
        ("LiltTextBrush", c => c.Text),
        ("LiltTextMutedBrush", c => c.TextMuted),
        ("LiltAccentBrush", c => c.Accent),
        ("LiltOnAccentBrush", c => c.OnAccent),
        ("LiltHoverBrush", c => c.Hover),
        ("LiltPressedBrush", c => c.Pressed),
        ("LiltSelectedBrush", c => c.Accent.WithAlpha(0x33)),
        ("LiltOverlayBrush", c => c.Background.Opaque),
        ("TextControlBackground", c => c.SurfaceRaised),
        ("TextControlBackgroundPointerOver", c => c.SurfaceRaised),
        ("TextControlBackgroundFocused", c => c.Background.Opaque),
        ("TextControlForeground", c => c.Text),
        ("TextControlForegroundPointerOver", c => c.Text),
        ("TextControlForegroundFocused", c => c.Text),
        ("TextControlPlaceholderForeground", c => c.TextMuted),
        ("TextControlPlaceholderForegroundPointerOver", c => c.TextMuted),
        ("TextControlPlaceholderForegroundFocused", c => c.TextMuted),
        ("TextControlBorderBrush", c => c.Border),
        ("TextControlBorderBrushPointerOver", c => c.Border),
        ("TextControlBorderBrushFocused", c => c.Accent),
        ("TextControlSelectionHighlightColor", c => c.Accent.WithAlpha(0x66)),
        ("TextControlButtonForeground", c => c.TextMuted),
        ("TextControlButtonForegroundPointerOver", c => c.Text),
        ("ListViewItemBackgroundPointerOver", c => c.Hover),
        ("ListViewItemBackgroundPressed", c => c.Pressed),
        ("ListViewItemBackgroundSelected", c => c.Accent.WithAlpha(0x1F)),
        ("ListViewItemBackgroundSelectedPointerOver", c => c.Accent.WithAlpha(0x2B)),
        ("ListViewItemBackgroundSelectedPressed", c => c.Pressed),
        ("ListViewItemForeground", c => c.Text),
        ("ListViewItemForegroundPointerOver", c => c.Text),
        ("ListViewItemForegroundSelected", c => c.Text),
        ("ListViewItemSelectionIndicatorBrush", c => c.Accent),
        ("FlyoutPresenterBackground", c => c.Background.Opaque),
        ("MenuFlyoutPresenterBackground", c => c.Background.Opaque),
    ];

    public static void Apply(ResourceDictionary resources, ThemeColors colors)
    {
        foreach (var (key, pick) in map)
            if (resources.TryGetValue(key, out var value) && value is SolidColorBrush brush)
                brush.Color = ToColor(pick(colors));
    }

    public static Brush Get(string key) => (Brush)Application.Current.Resources[key];

    public static Windows.UI.Color ToColor(Rgba c) => ColorHelper.FromArgb(c.A, c.R, c.G, c.B);

    // In Windows high contrast the user's system colors win over any theme file.
    public static ThemeColors HighContrast()
    {
        var ui = new UISettings();
        Rgba Get(UIElementType t)
        {
            var c = ui.UIElementColor(t);
            return new Rgba(c.R, c.G, c.B);
        }
        var window = Get(UIElementType.Window);
        var text = Get(UIElementType.WindowText);
        var highlight = Get(UIElementType.Highlight);
        return new ThemeColors(
            Background: window,
            Surface: window,
            SurfaceRaised: window,
            Border: text,
            Text: text,
            TextMuted: Get(UIElementType.GrayText),
            Accent: highlight,
            OnAccent: Get(UIElementType.HighlightText),
            Hover: highlight.WithAlpha(0x40),
            Pressed: highlight.WithAlpha(0x80),
            Page: window);
    }
}
