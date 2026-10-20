using System.ComponentModel;
using Lilt.Core.Navigation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;

namespace Lilt.App.Browser;

// One tab. View and Core stay null until the tab is first shown, so restored tabs cost nothing.
public sealed class BrowserTab(string url, string title = "") : INotifyPropertyChanged
{
    string url = url, title = title;
    ImageSource? icon;

    public string Url
    {
        get => url;
        set => Set(ref url, value, nameof(Url), nameof(DisplayTitle));
    }

    public string Title
    {
        get => title;
        set => Set(ref title, value, nameof(Title), nameof(DisplayTitle));
    }

    public ImageSource? Icon
    {
        get => icon;
        set => Set(ref icon, value, nameof(Icon), nameof(GlyphVisibility));
    }

    public Visibility GlyphVisibility => icon is null ? Visibility.Visible : Visibility.Collapsed;

    public string DisplayTitle => !string.IsNullOrWhiteSpace(title) ? title : IsHome ? "Home" : UrlPolicy.DisplayHost(url);

    public bool IsHome => Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Host == BrowserEngine.NewTabHost;

    internal WebView2? View { get; set; }
    internal CoreWebView2? Core { get; set; }
    internal Task<CoreWebView2>? Loading { get; set; }
    internal bool IsLoadingPage { get; set; }
    internal long LastActive { get; set; } = Environment.TickCount64;

    public event PropertyChangedEventHandler? PropertyChanged;

    void Set<T>(ref T field, T value, params string[] names)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        foreach (var name in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
