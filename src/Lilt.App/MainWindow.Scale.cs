using Lilt.App.Browser;
using Lilt.App.Shell;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Lilt.App;

// Interface size (theme "scale") and text size, applied to Lilt's own UI with Shell/Scaler: the parts drawn in XAML,
// list rows as they appear, and what code builds (built at 100%, then scaled). The toolbar, the sidebar's size and the
// panel's width come from the metrics directly. The player's contents also follow its width (designed at 300), so a
// wider player, floating or docked in a wider panel, has bigger art, text and buttons.
public sealed partial class MainWindow
{
    static readonly double[] interfaceSizes = [0.75, 0.8, 0.9, 1, 1.1, 1.25, 1.5];
    HashSet<object>? scaleSkip;
    (double, double, double) playerScaled;

    // Sized by Lilt's code from the metrics already: the scaler leaves them, and what's in them, alone.
    HashSet<object> ScaleSkip() => scaleSkip ??= [.. ChromeButtons(), AddressBox, BookmarkSearch, BookmarkList, TabList, TabsTitle, PanelSiteList, VisualizerCanvas];

    void ScaleNow(object? root) => Scaler.Apply(root, metrics.Text, metrics.Scale, ScaleSkip());

    void ScaleChrome()
    {
        foreach (var root in new object?[]
        {
            CustomizeFlyout, DownloadsFlyout, PaletteFlyout, MainMenu, BookmarkMenu, TabsHeader, BookmarksPanel, PanelHeader, PanelEmpty, PanelGrip,
            PanelVizGrip, PanelDropZone.Child, PeekBar, PermissionBar, NoticeBar, BookmarksOverlay, PipHeader, VisualizerWidget.Child,
        })
            ScaleNow(root);
        VisualizerWidget.Padding = new Thickness(metrics.Px(8));
        foreach (var list in new ListViewBase[] { BookmarkList, PanelSiteList })
            for (var i = 0; i < list.Items.Count; i++)
                if (list.ContainerFromIndex(i) is SelectorItem item) ScaleNow(item.ContentTemplateRoot);
        playerScaled = default;
        ScalePlayer();
        FitFlyouts();
    }

    // Customize and the palette grow with the interface size but never past the window (a flyout too tall to fit isn't shown).
    void FitFlyouts()
    {
        var room = Root.ActualHeight > 0 ? Root.ActualHeight : 700;
        CustomizeScroll.MaxHeight = Math.Max(metrics.Px(200), Math.Min(metrics.Px(560), room - metrics.Px(140)));
        PaletteList.MaxHeight = Math.Max(metrics.Px(160), Math.Min(metrics.Px(420), room - metrics.Px(170)));
    }

    void InitScale()
    {
        foreach (var list in new ListViewBase[] { BookmarkList, PanelSiteList })
            list.ContainerContentChanging += (_, e) =>
            {
                if (!e.InRecycleQueue && e.ItemContainer is SelectorItem item) ScaleNow(item.ContentTemplateRoot);
            };
    }

    void ScalePlayer()
    {
        var docked = PanelHost.Children.Contains(MediaWidget);
        var width = docked ? PanelHost.Width - metrics.Px(12) : MediaWidget.Width;
        if (double.IsNaN(width) || width <= 0) width = theme.Media.Player.Width * metrics.Scale;
        var f = Math.Round(Math.Clamp(width / (300 * metrics.Scale), 0.85, 1.6), 2);
        var key = (f, metrics.Text, metrics.Scale);
        if (key == playerScaled) return;
        playerScaled = key;
        MediaWidget.Padding = new Thickness(Math.Round(10 * metrics.Scale * f));
        Scaler.Apply(MediaWidget.Child, metrics.Text * f, metrics.Scale * f);
    }

    // Ctrl+K or your own keys: one step bigger or smaller, or back to 100%.
    void StepInterfaceSize(int step)
    {
        var now = Current.Scale;
        var next = step == 0 ? 1 : step > 0 ? interfaceSizes.FirstOrDefault(x => x > now + 0.001, now) : interfaceSizes.LastOrDefault(x => x < now - 0.001, now);
        Queue(Current with { Scale = next });
        ShowNotice(new($"Interface size {Math.Round(next * 100)}%", NoticeKind.Info));
    }
}
