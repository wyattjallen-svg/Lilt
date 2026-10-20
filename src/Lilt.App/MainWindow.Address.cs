using Lilt.App.Browser;
using Lilt.App.Shell;
using Lilt.Core.Navigation;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using VirtualKey = Windows.System.VirtualKey;

namespace Lilt.App;

// The address bar: shows pages without https://, completes sites you visit as you type ("you" → "youtube.com") and
// lists matching tabs, bookmarks and history below. Up/Down pick, Enter goes, Shift+Delete forgets a history entry.
// History is Lilt's own (pages.history; never in InPrivate), kept in history.json.
public sealed partial class MainWindow
{
    History history = null!;
    DispatcherQueueTimer historyTimer = null!;
    string typed = "";
    bool completing;
    int suggestIndex;
    List<(string Title, string Url, string Glyph, string Hint, Action Run, Suggestion? Item)> suggestRows = [];

    bool KeepHistory => !options.InPrivate && theme.Pages.History;
    (string Name, string Template) Engine => SearchEngines.For(theme.Pages.Search);

    void InitHistory()
    {
        history = new History(paths.History);
        if (!options.InPrivate) history.Load();
        historyTimer = DispatcherQueue.CreateTimer();
        historyTimer.Interval = TimeSpan.FromSeconds(5);
        historyTimer.IsRepeating = false;
        historyTimer.Tick += (_, _) => SaveHistory();
    }

    void RecordVisit(string url, string title)
    {
        if (!KeepHistory || BrowserTab.IsHomeUrl(url)) return;
        history.Record(url, title, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        historyTimer.Stop();
        historyTimer.Start();
    }

    void RetitleVisit(string url, string title)
    {
        if (!KeepHistory) return;
        history.Retitle(url, title);
        historyTimer.Stop();
        historyTimer.Start();
    }

    void SaveHistory()
    {
        if (options.InPrivate || !history.Dirty) return;
        try
        {
            history.Save();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            LiltPaths.LogError(e);
        }
    }

    // Lilt's list and the engine's own browsing history both.
    async void ClearHistory()
    {
        history.Clear();
        SaveHistory();
        try
        {
            if (tabs.Select(t => t.Core).FirstOrDefault(c => c is not null) is { } c)
                await c.Profile.ClearBrowsingDataAsync(Microsoft.Web.WebView2.Core.CoreWebView2BrowsingDataKinds.BrowsingHistory);
            ShowNotice(new("History cleared.", NoticeKind.Success));
        }
        catch (Exception e)
        {
            LiltPaths.LogError(e);
            ShowNotice(new($"Lilt's history is cleared, but the engine's couldn't be: {e.Message}", NoticeKind.Warning));
        }
    }

    string CurrentFull => active?.Url is { } u && Uri.TryCreate(u, UriKind.Absolute, out var uri) ? Full(uri) : "";

    static string Full(Uri? uri) => uri is null || UrlPolicy.IsBlank(uri) || uri.Host == BrowserEngine.NewTabHost ? "" : uri.AbsoluteUri;

    // While you're not editing: the tidy form.
    static string Display(Uri? uri) => Full(uri) is { Length: > 0 } ? UrlPolicy.Pretty(uri!) : "";

    void AddressBox_GotFocus(object sender, RoutedEventArgs e)
    {
        // Editing shows the whole address; a lambda, not a method group: CsWinRT can't marshal a delegate whose target is a projected control.
        if (AddressBox.Text == Display(Uri.TryCreate(active?.Url, UriKind.Absolute, out var u) ? u : null) && CurrentFull.Length > 0) SetAddress(CurrentFull);
        typed = AddressBox.Text;
        DispatcherQueue.TryEnqueue(() => AddressBox.SelectAll());
    }

    void AddressBox_LostFocus(object sender, RoutedEventArgs e)
    {
        HideSuggestions();
        if (AddressBox.Text == CurrentFull || AddressBox.Text.Length == 0)
            SetAddress(Display(Uri.TryCreate(active?.Url, UriKind.Absolute, out var u) ? u : null));
    }

    // Text set by Lilt rather than typed: no completion, no suggestions.
    void SetAddress(string text)
    {
        if (AddressBox.Text == text) return;
        completing = true;
        AddressBox.Text = text;
        typed = text;
    }

    void AddressBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var text = AddressBox.Text;
        if (completing)
        {
            completing = false;
            return;
        }
        if (AddressBox.FocusState == FocusState.Unfocused) return;
        var grew = text.Length > typed.Length && text.StartsWith(typed, StringComparison.OrdinalIgnoreCase);
        typed = text;
        if (grew && AddressBox.SelectionStart >= text.Length - 1
            && Suggestions.Complete(text, bookmarks.Items, history.All, Now) is { } full && full.Length > text.Length)
        {
            completing = true;
            AddressBox.Text = full;
            AddressBox.Select(text.Length, full.Length - text.Length);
        }
        ShowSuggestions(text);
    }

    static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    void ShowSuggestions(string text)
    {
        var q = text.Trim();
        if (q.Length == 0 || q == CurrentFull)
        {
            HideSuggestions();
            return;
        }
        var shown = AddressBox.Text.Trim();
        var go = AddressResolver.Resolve(shown, Engine.Template);
        suggestRows = [];
        if (go.Kind == AddressKind.Navigate) suggestRows.Add((shown, go.Uri!.AbsoluteUri, "", "Go", () => Go(shown), null));
        // Search for what was typed, not the inline completion.
        suggestRows.Add((q, $"Search with {Engine.Name}", "", "", () => Navigate(AddressResolver.SearchUri(q, Engine.Template).AbsoluteUri), null));
        var found = Suggestions.Find(q, tabs.Where(t => t != active && !t.IsHome).Select(t => (t.DisplayTitle, t.Url)), bookmarks.Items, history.All, Now, 7);
        foreach (var s in found)
        {
            var tab = s.Kind == SuggestionKind.Tab ? tabs.FirstOrDefault(t => t.Url == s.Url) : null;
            var (glyph, hint) = s.Kind switch
            {
                SuggestionKind.Tab => ("", "Switch to tab"),
                SuggestionKind.Bookmark => ("", UrlPolicy.Pretty(new Uri(s.Url))),
                _ => ("", UrlPolicy.Pretty(new Uri(s.Url))),
            };
            var url = s.Url;
            suggestRows.Add((s.Title.Length > 0 ? s.Title : UrlPolicy.Pretty(new Uri(url)), url, glyph, hint, tab is not null ? () => Activate(tab) : () => Navigate(url), s));
        }
        suggestIndex = 0;
        DrawSuggestions();
    }

    void DrawSuggestions()
    {
        SuggestList.Children.Clear();
        for (var i = 0; i < suggestRows.Count; i++)
        {
            var (title, url, glyph, hint, _, item) = suggestRows[i];
            var index = i;
            var row = new Grid { ColumnSpacing = 10, Padding = new Thickness(10, 6, 10, 6), CornerRadius = new CornerRadius(Math.Max(2, theme.Shape.ControlRadius - 2)) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new FontIcon { Glyph = glyph, FontSize = 13, Foreground = ThemeResources.Get("LiltTextMutedBrush") });
            var text = new TextBlock { Text = title, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            var note = new TextBlock
            {
                Text = item is null ? url : hint, FontSize = 12, MaxWidth = 320, TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = ThemeResources.Get("LiltTextMutedBrush"), VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(note, 2);
            row.Children.Add(note);
            row.Background = index == suggestIndex ? ThemeResources.Get("LiltHoverBrush") : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            row.PointerEntered += (_, _) => Highlight(index);
            // Acts on press: the rows can't take focus, so the address box keeps it until the page opens.
            row.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(row).Properties.IsLeftButtonPressed) return;
                e.Handled = true;
                RunSuggestion(index);
            };
            SuggestList.Children.Add(row);
        }
        var at = AddressBox.TransformToVisual(ContentArea).TransformPoint(default);
        SuggestPanel.Width = Math.Min(Math.Max(320, AddressBox.ActualWidth), Math.Max(200, ContentArea.ActualWidth - 8));
        SuggestPanel.Margin = new Thickness(Math.Clamp(at.X, 4, Math.Max(4, ContentArea.ActualWidth - SuggestPanel.Width - 4)), Math.Max(4, at.Y + AddressBox.ActualHeight + 4), 0, 0);
        SuggestPanel.Visibility = suggestRows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void Highlight(int index)
    {
        suggestIndex = index;
        for (var i = 0; i < SuggestList.Children.Count; i++)
            ((Grid)SuggestList.Children[i]).Background = i == index ? ThemeResources.Get("LiltHoverBrush") : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    void RunSuggestion(int index)
    {
        if (index < 0 || index >= suggestRows.Count) return;
        var run = suggestRows[index].Run;
        HideSuggestions();
        run();
        active?.View?.Focus(FocusState.Programmatic);
    }

    void HideSuggestions()
    {
        SuggestPanel.Visibility = Visibility.Collapsed;
        suggestRows = [];
    }

    void AddressBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var open = SuggestPanel.Visibility == Visibility.Visible && suggestRows.Count > 0;
        static bool Down(VirtualKey k) => InputKeyboardSource.GetKeyStateForCurrentThread(k).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        switch (e.Key)
        {
            case VirtualKey.Down when open:
                Highlight((suggestIndex + 1) % suggestRows.Count);
                break;
            case VirtualKey.Up when open:
                Highlight((suggestIndex - 1 + suggestRows.Count) % suggestRows.Count);
                break;
            // Shift+Delete forgets the highlighted history entry, like other browsers.
            case VirtualKey.Delete when open && Down(VirtualKey.Shift) && suggestRows[suggestIndex].Item is { Kind: SuggestionKind.History } h:
                history.Remove(h.Url);
                historyTimer.Stop();
                historyTimer.Start();
                ShowSuggestions(typed);
                break;
            case VirtualKey.Enter:
                var text = AddressBox.Text.Trim();
                // Ctrl+Enter turns a word into www.<word>.com.
                if (Down(VirtualKey.Control) && text.Length > 0 && !text.Contains('.') && !text.Contains(' ') && !text.Contains(':'))
                {
                    HideSuggestions();
                    Go($"www.{text}.com");
                }
                else if (open && suggestIndex > 0) RunSuggestion(suggestIndex);
                else
                {
                    HideSuggestions();
                    Go(AddressBox.Text);
                }
                break;
            case VirtualKey.Escape when open:
                HideSuggestions();
                break;
            case VirtualKey.Escape:
                SetAddress(Display(Uri.TryCreate(active?.Url, UriKind.Absolute, out var u) ? u : null));
                active?.View?.Focus(FocusState.Programmatic);
                break;
            default:
                return;
        }
        e.Handled = true;
    }
}
