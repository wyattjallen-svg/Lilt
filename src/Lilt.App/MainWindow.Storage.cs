using System.Text.Json;
using Lilt.App.Browser;
using Lilt.Core;
using Microsoft.UI.Dispatching;
using Microsoft.Web.WebView2.Core;

namespace Lilt.App;

// Storage cleanup (the theme's "storage"): the engine's cache is capped (an engine switch). A minute after start and
// every six hours, sites' offline copies are emptied once they pass the same cap, and sites you've stopped using lose
// what they stored and their readable cookies. Sign-in cookies stay unless storage.signOut is on, so sites you use (and,
// by default, the ones you don't) keep you signed in. Customize → Privacy shows the numbers and has buttons for now.
public sealed partial class MainWindow
{
    DispatcherQueueTimer? storageTimer;
    StorageUse? storageUse;
    string storageNote = "";
    // Sites already tidied this session (their sign-in cookies stay, so they'd come up again every time).
    readonly HashSet<string> tidied = [];

    void InitStorage()
    {
        if (options.InPrivate) return;
        storageTimer = DispatcherQueue.CreateTimer();
        storageTimer.Interval = TimeSpan.FromMinutes(1);
        storageTimer.Tick += async (_, _) =>
        {
            storageTimer.Interval = TimeSpan.FromHours(6);
            await TidyStorageAsync(emptyCaches: false);
        };
        storageTimer.Start();
    }

    CoreWebView2? AnyCore => AllTabs.Select(t => t.Core).FirstOrDefault(c => c is not null);

    async Task<StorageUse?> MeasureStorageAsync()
    {
        if (engine.Environment?.UserDataFolder is not { } folder) return null;
        return storageUse = await Task.Run(() => StorageUse.Measure(folder));
    }

    // emptyCaches: the "Empty caches" button; cached files and offline copies go, and nothing that signs you in.
    async Task TidyStorageAsync(bool emptyCaches)
    {
        if (closing || options.InPrivate || AnyCore is not { } core) return;
        var before = await MeasureStorageAsync();
        var limit = theme.Storage.CacheLimit * 1048576L;
        var kinds = emptyCaches ? CoreWebView2BrowsingDataKinds.DiskCache | CoreWebView2BrowsingDataKinds.CacheStorage
            : before is { } b && limit > 0 && b.Offline > limit ? CoreWebView2BrowsingDataKinds.CacheStorage : 0;
        try
        {
            if (kinds != 0) await core.Profile.ClearBrowsingDataAsync(kinds);
        }
        catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException) { } // the view closed meanwhile
        var forgot = await ForgetUnusedSitesAsync(core);
        var after = await MeasureStorageAsync();
        var freed = before is { } x && after is { } y ? Math.Max(0, x.Total - y.Total) : 0;
        storageNote = $"Last tidy-up {DateTime.Now:t}: {(forgot > 0 ? $"{Plural(forgot, "unused site")} tidied" : "no unused sites")}"
            + (freed >= 1 << 20 ? $", {StorageUse.Size(freed)} freed." : ".");
        report.Key("storage", $"{(after is { } u ? $"cache {u.Cache} offline {u.Offline} site {u.SiteData} total {u.Total}" : "unmeasured")}; forgot {forgot}; cleared {kinds}");
        if (PrivacyPage.Visibility == Microsoft.UI.Xaml.Visibility.Visible) ShowStorage();
    }

    async Task<int> ForgetUnusedSitesAsync(CoreWebView2 core)
    {
        var spec = theme.Storage;
        // Without history Lilt can't tell which sites you still use.
        if (spec.ForgetAfterDays <= 0 || !theme.Pages.History) return 0;
        static string Host(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : "";
        try
        {
            var cookies = await core.CookieManager.GetCookiesAsync("");
            var keep = spec.Keep.Items.Concat(bookmarks.Items.Select(b => Host(b.Url))).Concat(AllTabs.Select(t => Host(t.Url)));
            var stale = SiteData.Stale(cookies.Select(c => c.Domain), history.All.Select(v => (Host(v.Url), v.Last)), keep, Now, spec.ForgetAfterDays)
                .Where(tidied.Add).ToHashSet();
            if (stale.Count == 0) return 0;
            foreach (var c in cookies)
                if ((spec.SignOut || !c.IsHttpOnly) && stale.Contains(SiteData.Site(c.Domain))) core.CookieManager.DeleteCookie(c);
            var hosts = history.All.Select(v => Host(v.Url)).Concat(cookies.Select(c => c.Domain.Trim('.')))
                .Where(h => stale.Contains(SiteData.Site(h))).Concat(stale).Concat(stale.Select(s => "www." + s)).Distinct();
            // Everything a site stores except cookies (handled above), for each address it was seen at.
            const string types = "file_systems,indexeddb,local_storage,websql,service_workers,cache_storage,shader_cache,storage_buckets";
            foreach (var host in hosts)
                try
                {
                    await core.CallDevToolsProtocolMethodAsync("Storage.clearDataForOrigin", JsonSerializer.Serialize(new { origin = "https://" + host, storageTypes = types }));
                }
                catch (Exception) { } // best effort, one address at a time
            return stale.Count;
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            return 0;
        }
    }

    void ShowStorage()
    {
        var u = storageUse;
        StorageText.Text = u is null ? "Measuring…" :
            $"Cached files {StorageUse.Size(u.Cache)} · offline copies {StorageUse.Size(u.Offline)} · site data {StorageUse.Size(u.SiteData)} · all together {StorageUse.Size(u.Total)}";
        StorageNote.Text = storageNote;
        StorageNote.Visibility = storageNote.Length > 0 ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    }

    async void FillStorage()
    {
        ShowStorage();
        await MeasureStorageAsync();
        ShowStorage();
    }

    async void EmptyCaches_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        StorageText.Text = "Emptying caches…";
        await TidyStorageAsync(emptyCaches: true);
        ShowStorage();
        ShowNotice(new("Caches emptied. You're still signed in everywhere.", NoticeKind.Success));
    }

    // The site of the current tab goes on (or off) storage.keep.
    void ToggleKeepHere()
    {
        if (active is null || !Uri.TryCreate(active.Url, UriKind.Absolute, out var u) || !Lilt.Core.Navigation.UrlPolicy.IsWeb(u)) return;
        var site = SiteData.Site(u.Host);
        var keep = Current.Storage.Keep.Items.ToList();
        var removed = keep.RemoveAll(k => SiteData.Site(k) == site) > 0;
        if (!removed) keep.Add(site);
        Queue(Current with { Storage = Current.Storage with { Keep = new Lilt.Core.Theming.Ids(keep.ToArray()) } });
        ShowNotice(new(removed ? $"{site} can be tidied up again when you stop using it." : $"{site} will never be tidied up.", NoticeKind.Success));
    }

    bool KeptHere => active is not null && Uri.TryCreate(active.Url, UriKind.Absolute, out var u)
        && Current.Storage.Keep.Items.Any(k => SiteData.Site(k) == SiteData.Site(u.Host));
}
