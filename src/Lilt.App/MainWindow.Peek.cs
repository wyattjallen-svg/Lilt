using Lilt.App.Browser;
using Lilt.App.Shell;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;
using VirtualKey = Windows.System.VirtualKey;

namespace Lilt.App;

// Peek: Shift+click a link to preview it in a panel over the page; open it as a tab or beside this one, or close it
// (Esc, the X, or a click outside). The preview's view is created on demand and closed with the panel.
public sealed partial class MainWindow
{
    WebView2? peekView;
    CoreWebView2? peekCore;
    Task<CoreWebView2>? peekLoading;

    bool PeekOpen => PeekOverlay.Visibility == Visibility.Visible;

    async void ShowPeek(string url)
    {
        if (fullScreen || !Uri.TryCreate(url, UriKind.Absolute, out _)) return;
        PeekOverlay.Visibility = Visibility.Visible;
        PeekAddress.Text = url;
        try
        {
            if (peekLoading is null)
            {
                var view = new WebView2 { DefaultBackgroundColor = ThemeResources.ToColor(theme.Colors.Page.Opaque) };
                peekView = view;
                PeekHost.Children.Add(view);
                peekLoading = engine.AttachAsync(view);
            }
            var loading = peekLoading;
            var c = await loading;
            if (loading != peekLoading) return;
            if (peekCore != c)
            {
                peekCore = c;
                c.SourceChanged += (_, _) => PeekAddress.Text = c.Source;
                c.WindowCloseRequested += (_, _) => ClosePeek();
            }
            c.Navigate(url);
            peekView?.Focus(FocusState.Programmatic);
        }
        catch (Exception e)
        {
            LiltPaths.LogError(e);
            ClosePeek();
            ShowNotice(new($"Couldn't preview that link: {e.Message}", NoticeKind.Error));
        }
    }

    void ClosePeek()
    {
        if (!PeekOpen && peekView is null) return;
        PeekOverlay.Visibility = Visibility.Collapsed;
        engine.Detach(peekCore);
        if (peekView is { } view)
        {
            PeekHost.Children.Remove(view);
            view.Close();
        }
        peekView = null;
        peekCore = null;
        peekLoading = null;
        active?.View?.Focus(FocusState.Programmatic);
    }

    string PeekUrl => peekCore?.Source is { Length: > 0 } s && s != "about:blank" ? s : PeekAddress.Text;

    void PeekOpen_Click(object sender, RoutedEventArgs e)
    {
        var url = PeekUrl;
        ClosePeek();
        AddTab(url);
    }

    void PeekSplit_Click(object sender, RoutedEventArgs e)
    {
        var url = PeekUrl;
        ClosePeek();
        if (active is null) return;
        var tab = new BrowserTab(url);
        tabs.Insert(tabs.IndexOf(active) + 1, tab);
        SplitWith(tab);
    }

    void PeekClose_Click(object sender, RoutedEventArgs e) => ClosePeek();

    void PeekOverlay_Tapped(object sender, TappedRoutedEventArgs e) => ClosePeek();

    // Taps inside the panel don't reach the dimmed background behind it.
    void PeekPanel_Tapped(object sender, TappedRoutedEventArgs e) => e.Handled = true;

    void PeekOverlay_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape) return;
        e.Handled = true;
        ClosePeek();
    }
}
