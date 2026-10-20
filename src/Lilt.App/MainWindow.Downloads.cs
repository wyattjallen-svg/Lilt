using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Lilt.App.Browser;
using Lilt.App.Shell;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace Lilt.App;

// Lilt's own downloads list (WebView2's built-in dialog is turned off), shown from the toolbar once something downloads.
public sealed partial class MainWindow
{
    void OnDownloadStarted(CoreWebView2DownloadOperation op)
    {
        var body = (Style)Application.Current.Resources["BodyText"];
        var name = new TextBlock { Style = body, Text = Path.GetFileName(op.ResultFilePath), FontSize = theme.Typography.Size, TextTrimming = TextTrimming.CharacterEllipsis };
        var status = new TextBlock { Style = body, Foreground = ThemeResources.Get("LiltTextMutedBrush"), FontSize = Math.Max(9, theme.Typography.Size - 1) };
        var icon = new FontIcon { Glyph = "" };
        var action = new Button { Style = (Style)Application.Current.Resources["ChromeButton"], Content = icon, VerticalAlignment = VerticalAlignment.Center };
        StyleButton(action);
        var row = new Grid { ColumnSpacing = 6 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(name);
        text.Children.Add(status);
        row.Children.Add(text);
        Grid.SetColumn(action, 1);
        row.Children.Add(action);
        DownloadList.Children.Insert(0, row);
        NoDownloads.Visibility = Visibility.Collapsed;
        if (!hasDownloads)
        {
            hasDownloads = true;
            ArrangeToolbar(theme.Toolbar.Items);
        }

        long lastUpdate = 0;
        void Update()
        {
            status.Text = DownloadStatus(op);
            var (glyph, tip) = op.State switch
            {
                CoreWebView2DownloadState.InProgress => ("", "Cancel download"),
                CoreWebView2DownloadState.Completed => ("", "Show in folder"),
                _ when op.CanResume => ("", "Resume download"),
                _ => ("", "Remove from list"),
            };
            icon.Glyph = glyph;
            ToolTipService.SetToolTip(action, tip);
            AutomationProperties.SetName(action, tip);
        }

        action.Click += (_, _) =>
        {
            switch (op.State)
            {
                case CoreWebView2DownloadState.InProgress:
                    op.Cancel();
                    break;
                case CoreWebView2DownloadState.Completed:
                    ShowInFolder(op.ResultFilePath);
                    break;
                case CoreWebView2DownloadState.Interrupted when op.CanResume:
                    op.Resume();
                    break;
                default:
                    DownloadList.Children.Remove(row);
                    if (DownloadList.Children.Count == 0) NoDownloads.Visibility = Visibility.Visible;
                    break;
            }
        };
        op.BytesReceivedChanged += (_, _) =>
        {
            var now = Environment.TickCount64;
            if (now - lastUpdate < 250) return;
            lastUpdate = now;
            Update();
        };
        op.StateChanged += (_, _) =>
        {
            Update();
            if (op.State == CoreWebView2DownloadState.Completed)
                ShowNotice(new($"Downloaded {name.Text}", NoticeKind.Success, "Show in folder", () => ShowInFolder(op.ResultFilePath)));
            else if (op.State == CoreWebView2DownloadState.Interrupted)
                ShowNotice(new($"{name.Text} didn't finish downloading: {Humanize(op.InterruptReason.ToString())}.", NoticeKind.Warning));
        };
        Update();
        ShowNotice(new($"Downloading {name.Text} to {Path.GetDirectoryName(op.ResultFilePath)}", NoticeKind.Info, "Show", () => ShowFlyout(DownloadsFlyout, DownloadsButton, FlyoutHost)));
    }

    static string DownloadStatus(CoreWebView2DownloadOperation op)
    {
        double received = op.BytesReceived;
        double total = op.TotalBytesToReceive is { } t ? t : 0;
        return op.State switch
        {
            CoreWebView2DownloadState.Completed => $"Done · {Size(received)}",
            CoreWebView2DownloadState.Interrupted => $"Stopped · {Humanize(op.InterruptReason.ToString())}",
            _ => total > 0 ? $"{received / total:P0} · {Size(received)} of {Size(total)}" : Size(received),
        };
    }

    static string Size(double bytes) => bytes switch
    {
        >= 1 << 30 => $"{bytes / (1 << 30):N1} GB",
        >= 1 << 20 => $"{bytes / (1 << 20):N1} MB",
        >= 1 << 10 => $"{bytes / (1 << 10):N0} KB",
        _ => $"{bytes:N0} B",
    };

    static string Humanize(string pascal) => Regex.Replace(pascal, "(?<=[a-z])(?=[A-Z])", " ").ToLowerInvariant();

    static void ShowInFolder(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\""));
        }
        catch (Win32Exception e)
        {
            LiltPaths.LogError(e);
        }
    }
}
