using Lilt.App.Shell;
using Lilt.Core.Navigation;
using Lilt.Core.Theming;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;

namespace Lilt.App.Browser;

// A window opened by a page (window.open, target=_blank). Shares the engine's profile and is wired up as the
// opener's NewWindow, so sign-in and payment pop-ups can talk back to the page that opened them.
// The read-only address strip is there so a pop-up can't hide where it really is.
sealed class PopupWindow : Window
{
    readonly BrowserEngine engine;
    readonly WebView2 view = new();
    readonly TextBlock address = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, IsTextSelectionEnabled = true };
    readonly FontIcon security = new() { FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
    readonly CoreWebView2WindowFeatures? features;
    readonly double defaultWidth, defaultHeight;

    public PopupWindow(BrowserEngine engine, CoreWebView2WindowFeatures? features, double defaultWidth = 1100, double defaultHeight = 800)
    {
        this.engine = engine;
        this.features = features;
        this.defaultWidth = defaultWidth;
        this.defaultHeight = defaultHeight;
        Title = "Lilt";
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "lilt.ico"));

        var strip = new Grid
        {
            Padding = new Thickness(12, 6, 12, 6),
            ColumnSpacing = 8,
            Background = ThemeResources.Get("LiltSurfaceBrush"),
            BorderBrush = ThemeResources.Get("LiltBorderBrush"),
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
        strip.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        strip.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        security.Foreground = ThemeResources.Get("LiltTextMutedBrush");
        address.Foreground = ThemeResources.Get("LiltTextBrush");
        strip.Children.Add(security);
        Grid.SetColumn(address, 1);
        strip.Children.Add(address);

        var root = new Grid
        {
            Background = ThemeResources.Get("LiltBackgroundBrush"),
            RequestedTheme = engine.ThemeBase == ThemeBase.Light ? ElementTheme.Light : ElementTheme.Dark,
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(strip);
        Grid.SetRow(view, 1);
        root.Children.Add(view);
        Content = root;

        Closed += (_, _) =>
        {
            engine.Detach(view.CoreWebView2);
            view.Close();
        };
    }

    public async Task<CoreWebView2> OpenAsync()
    {
        var f = features;
        var sized = f is { HasSize: true } && f.Width >= 200 && f.Height >= 200;
        WindowSizing.Place(this,
            sized ? f!.Width : defaultWidth,
            sized ? f!.Height + 40 : defaultHeight,
            f is { HasPosition: true } ? (double)f.Left : null,
            f is { HasPosition: true } ? (double)f.Top : null);
        Activate();

        var core = await engine.AttachAsync(view);
        core.WindowCloseRequested += (_, _) => Close();
        core.SourceChanged += (_, _) => Show(core.Source);
        core.DocumentTitleChanged += (_, _) => Title = string.IsNullOrWhiteSpace(core.DocumentTitle) ? "Lilt" : $"{core.DocumentTitle} – Lilt";
        Show(core.Source);
        return core;
    }

    void Show(string? source)
    {
        address.Text = source ?? "";
        var insecure = Uri.TryCreate(source, UriKind.Absolute, out var uri) && UrlPolicy.IsInsecure(uri);
        security.Glyph = insecure ? "" : "";
        ToolTipService.SetToolTip(security, insecure ? "Not secure (http)" : "Address of this pop-up");
    }
}
