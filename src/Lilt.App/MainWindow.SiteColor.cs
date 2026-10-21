using Lilt.App.Shell;
using Lilt.Core.Theming;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;

namespace Lilt.App;

// Site colors: the toolbar and sidebar take on the color of the site you're on (its theme-color, or the color along the
// top of the page), faded in over the theme's surface as strongly as toolbar.siteColor and sidebar.siteColor ask, but
// never so much that the theme's text gets hard to read. The home tab keeps the theme's own color.
public sealed partial class MainWindow
{
    (Rgba? Site, double Toolbar, double Sidebar, Rgba Surface, Rgba Text, int Duration)? tinted;

    void OnSiteColor(CoreWebView2 sender, string color)
    {
        if (AllTabs.FirstOrDefault(t => t.Core == sender) is not { } tab) return;
        tab.SiteColor = Rgba.TryParse(color, out var c) ? c : null;
        if (tab == active) ApplySiteTint();
    }

    void ApplySiteTint()
    {
        var site = active is { IsHome: false } a ? a.SiteColor : null;
        var t = theme;
        var key = (site, t.Toolbar.SiteColor, t.Sidebar.SiteColor, t.Colors.Surface, t.Colors.Text, AnimationsOn ? t.Motion.Duration * 2 : 0);
        if (tinted == key) return;
        tinted = key;
        var fade = new BrushTransition { Duration = TimeSpan.FromMilliseconds(key.Item6) };
        ToolbarTint.BackgroundTransition = fade;
        SidebarGrid.BackgroundTransition = new BrushTransition { Duration = fade.Duration };
        ToolbarTint.Background = Tint(site, t.Toolbar.SiteColor);
        SidebarGrid.Background = Tint(site, t.Sidebar.SiteColor);
    }

    Brush Tint(Rgba? site, double strength) =>
        new SolidColorBrush(site is { } s && strength > 0 ? ThemeResources.ToColor(SiteTint.For(s, theme.Colors.Surface, theme.Colors.Text, strength)) : Colors.Transparent);
}
