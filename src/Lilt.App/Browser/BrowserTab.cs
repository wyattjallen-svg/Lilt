using System.ComponentModel;
using Lilt.App.Shell;
using Lilt.Core;
using Lilt.Core.Navigation;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;

namespace Lilt.App.Browser;

// One tab. View and Core stay null until the tab is first shown, so restored tabs cost nothing.
// States from the design: active (accent outline), loading, playing sound or muted, pinned (icon only), error.
// Shown: on screen without being active (the other half of split view, or popped out), a muted outline.
public sealed class BrowserTab(string url, string title = "", bool pinned = false) : INotifyPropertyChanged
{
    static readonly Brush clear = new SolidColorBrush(Colors.Transparent);
    static readonly Brush error = new SolidColorBrush(ColorHelper.FromArgb(255, 0xE5, 0x48, 0x4D));
    string url = url, title = title;
    ImageSource? icon;
    bool active, loading, audible, muted, pinned = pinned, failed, shown;

    public string Url { get => url; set => Set(ref url, value, nameof(Url), nameof(DisplayTitle), nameof(Glyph)); }
    public string Title { get => title; set => Set(ref title, value, nameof(Title), nameof(DisplayTitle)); }
    public ImageSource? Icon { get => icon; set => Set(ref icon, value, nameof(Icon), nameof(IconVisibility), nameof(GlyphVisibility)); }
    public bool IsActive { get => active; set => Set(ref active, value, nameof(StateBorder)); }
    public bool IsShown { get => shown; set => Set(ref shown, value, nameof(StateBorder)); }
    public bool IsLoading { get => loading; set => Set(ref loading, value, nameof(IsLoading), nameof(IconVisibility), nameof(GlyphVisibility)); }
    public bool IsAudible { get => audible; set => Set(ref audible, value, nameof(AudioVisibility), nameof(AudioGlyph)); }
    public bool IsMuted { get => muted; set => Set(ref muted, value, nameof(AudioVisibility), nameof(AudioGlyph)); }
    public bool IsPinned { get => pinned; set => Set(ref pinned, value, nameof(LabelVisibility)); }
    public bool HasError { get => failed; set => Set(ref failed, value, nameof(Glyph), nameof(GlyphBrush), nameof(StateBorder), nameof(IconVisibility), nameof(GlyphVisibility)); }

    public string DisplayTitle => !string.IsNullOrWhiteSpace(title) ? title : IsHome ? "Home" : UrlPolicy.DisplayHost(url);
    public bool IsHome => Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Host == BrowserEngine.NewTabHost;

    public Brush StateBorder => failed ? error : active ? ThemeResources.Get("LiltAccentBrush") : shown ? ThemeResources.Get("LiltTextMutedBrush") : clear;
    public string Glyph => failed ? "" : IsHome ? "" : "";
    public Brush GlyphBrush => failed ? error : ThemeResources.Get("LiltTextBrush");
    public Visibility IconVisibility => icon is not null && !loading && !failed ? Visibility.Visible : Visibility.Collapsed;
    public Visibility GlyphVisibility => (icon is null || failed) && !loading ? Visibility.Visible : Visibility.Collapsed;
    public Visibility LabelVisibility => pinned ? Visibility.Collapsed : Visibility.Visible;
    public Visibility AudioVisibility => audible || muted ? Visibility.Visible : Visibility.Collapsed;
    public string AudioGlyph => muted ? "" : "";

    internal WebView2? View { get; set; }
    internal CoreWebView2? Core { get; set; }
    internal Task<CoreWebView2>? Loading { get; set; }
    internal long LastActive { get; set; } = Environment.TickCount64;
    // What the page last reported about its media, and when (positions move on from there while playing).
    internal MediaInfo? Media { get; set; }
    internal long MediaAt { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    void Set<T>(ref T field, T value, params string[] names)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        foreach (var name in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
