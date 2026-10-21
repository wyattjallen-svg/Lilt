using System.Diagnostics;
using Lilt.App.Browser;
using Lilt.App.Shell;
using Lilt.Core.Navigation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace Lilt.App;

// Sleeping tabs and what's using memory. A sleeping tab keeps its place, title and icon, but its page is closed, so it
// holds no memory and runs nothing; it reloads when shown. Tabs sleep after power.sleepMinutes unseen, sooner when
// Windows is short of memory, or on request (tab menu, Ctrl+K, Customize → Power).
public sealed partial class MainWindow
{
    // manual: asked for, so a pinned tab may sleep too. Never what's on screen, playing or popped out.
    bool CanSleep(BrowserTab tab, bool manual) =>
        started && tab.View is not null && !OnScreen(tab) && tab != floating && !tab.IsAudible && tab.Media is not { Playing: true }
        && (manual || !tab.IsPinned);

    void SleepTab(BrowserTab tab, bool manual = false)
    {
        if (!CanSleep(tab, manual)) return;
        if (mediaTab == tab) mediaTab = null;
        tab.Media = null;
        DisposeTab(tab);
        tab.Loading = null;
        tab.IsLoading = false;
        tab.IsAsleep = true;
        report.Key("power", $"slept {UrlPolicy.DisplayHost(tab.Url)}");
    }

    int SleepBackgroundTabs()
    {
        var list = AllTabs.Where(t => CanSleep(t, manual: false)).ToList();
        foreach (var t in list) SleepTab(t);
        if (list.Count > 0) Native.TrimMemory();
        ShowNotice(new(list.Count == 0 ? "No background tabs to put to sleep." : $"{Plural(list.Count, "tab")} asleep; each reloads when you open it.", NoticeKind.Success));
        return list.Count;
    }

    void SleepTabs_Click(object sender, RoutedEventArgs e) => SleepBackgroundTabs();

    static long PrivateBytes(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.PrivateMemorySize64;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return 0;
        }
    }

    // One row per engine process, biggest first: the tabs it runs (with a way to put them to sleep) or what it's for.
    async Task FillMemoryListAsync()
    {
        MemoryList.Children.Clear();
        if (engine.Environment is not { } env) return;
        IReadOnlyList<CoreWebView2ProcessExtendedInfo> infos;
        try
        {
            infos = await env.GetProcessExtendedInfosAsync();
        }
        catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return;
        }
        var rows = new List<(string Name, long Bytes, BrowserTab[] Tabs)> { ("Lilt itself", PrivateBytes(Environment.ProcessId), []) };
        foreach (var info in infos)
        {
            var p = info.ProcessInfo;
            var frames = info.AssociatedFrameInfos ?? [];
            var owners = AllTabs.Where(t => t.Core is not null && frames.Any(f => SamePage(f.Source, t.Url))).ToArray();
            var hosts = frames.Select(f => UrlPolicy.DisplayHost(f.Source)).Where(h => h.Length > 0).Distinct().Take(3).ToArray();
            var name = owners.Length > 0 ? string.Join(", ", owners.Select(t => t.DisplayTitle))
                : hosts.Length > 0 ? string.Join(", ", hosts)
                : p.Kind switch
                {
                    CoreWebView2ProcessKind.Browser => "The engine",
                    CoreWebView2ProcessKind.Gpu => "Graphics",
                    CoreWebView2ProcessKind.Utility => "Sound, network and extensions",
                    CoreWebView2ProcessKind.Renderer => "Extensions and background pages",
                    _ => p.Kind.ToString(),
                };
            rows.Add((name, PrivateBytes(p.ProcessId), owners));
        }
        var total = rows.Sum(r => r.Bytes);
        MemoryList.Children.Add(new TextBlock { Text = $"All together {Lilt.Core.StorageUse.Size(total)}", FontSize = 12, Foreground = ThemeResources.Get("LiltTextMutedBrush") });
        foreach (var (name, bytes, owners) in rows.OrderByDescending(r => r.Bytes))
        {
            var row = new Grid { ColumnSpacing = 6 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Text = name, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            var size = new TextBlock { Text = Lilt.Core.StorageUse.Size(bytes), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Foreground = ThemeResources.Get("LiltTextMutedBrush") };
            Grid.SetColumn(size, 1);
            row.Children.Add(size);
            var sleepable = owners.Where(t => CanSleep(t, manual: true)).ToArray();
            if (sleepable.Length > 0)
            {
                var sleep = new Button { Content = "Sleep", FontSize = 12, Padding = new Thickness(8, 2, 8, 2) };
                sleep.Click += async (_, _) =>
                {
                    foreach (var t in sleepable) SleepTab(t, manual: true);
                    await Task.Delay(800);
                    await FillMemoryListAsync();
                };
                Grid.SetColumn(sleep, 2);
                row.Children.Add(sleep);
            }
            MemoryList.Children.Add(row);
        }
    }

    // A frame's address against a tab's, ignoring the #fragment.
    static bool SamePage(string? frame, string tab) =>
        !string.IsNullOrEmpty(frame) && string.Equals(frame.Split('#')[0], tab.Split('#')[0], StringComparison.OrdinalIgnoreCase);

    async void MemoryList_Click(object sender, RoutedEventArgs e) => await FillMemoryListAsync();
}
