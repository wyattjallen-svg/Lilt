using Lilt.Core.Theming;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Lilt.App.Shell;

// Lilt's XAML is drawn at 100% with 13 px body text (ShellMetrics). The scaler sizes a part of it for the interface size
// and the text size: the first time it meets an element it remembers the sizes the XAML gave it (font size, fixed width
// and height, minimum and maximum sizes, padding, spacing, a grid's fixed rows and columns, a wrap grid's cells), and from
// then on sets them to those sizes times the factors, so applying again never compounds. Text takes the text factor;
// icons and everything else the box factor. Elements in `skip` are sized by Lilt's code and left alone with their insides.
static class Scaler
{
    // Remembered on the element itself (an attached property survives the managed wrapper being collected and made anew,
    // which a table keyed by the wrapper wouldn't): font, width, height, min width, min height, max width, max height,
    // spacing, row spacing, column spacing, item width, item height, padding left, top, right, bottom. NaN = not set.
    static readonly DependencyProperty DesignProperty =
        DependencyProperty.RegisterAttached("LiltDesign", typeof(string), typeof(Scaler), new PropertyMetadata(null));
    // A grid row's or column's own fixed size ("NaN" when it's Auto or *).
    static readonly DependencyProperty LengthProperty =
        DependencyProperty.RegisterAttached("LiltLength", typeof(string), typeof(Scaler), new PropertyMetadata(null));
    const int Font = 0, Width = 1, Height = 2, MinWidth = 3, MinHeight = 4, MaxWidth = 5, MaxHeight = 6, Spacing = 7, RowSpacing = 8,
        ColumnSpacing = 9, ItemWidth = 10, ItemHeight = 11, Pad = 12;
    static readonly System.Globalization.CultureInfo inv = System.Globalization.CultureInfo.InvariantCulture;

    public static void Apply(object? root, double text, double box, IReadOnlySet<object>? skip = null)
    {
        if (root is null || skip?.Contains(root) == true) return;
        if (root is FrameworkElement e) Size(e, text, box);
        foreach (var child in Children(root)) Apply(child, text, box, skip);
    }

    static IEnumerable<object> Children(object o)
    {
        switch (o)
        {
            case Panel p:
                foreach (var c in p.Children) yield return c;
                break;
            case Border { Child: { } c }:
                yield return c;
                break;
            case ContentControl { Content: UIElement c }:
                yield return c;
                break;
            case InfoBar { Content: UIElement c }:
                yield return c;
                break;
            // Items written in the XAML (ComboBoxItem, MenuFlyoutItem), not bound data.
            case ItemsControl ic:
                foreach (var i in ic.Items) if (i is UIElement u) yield return u;
                break;
            case SelectorBar bar:
                foreach (var i in bar.Items) yield return i;
                break;
            case MenuFlyoutSubItem sub:
                foreach (var i in sub.Items) yield return i;
                break;
            case FlyoutBase f:
                foreach (var i in FlyoutChildren(f)) yield return i;
                break;
        }
        if (o is Button { Flyout: { } flyout }) foreach (var i in FlyoutChildren(flyout)) yield return i;
        if (o is FrameworkElement fe && FlyoutBase.GetAttachedFlyout(fe) is { } attached) foreach (var i in FlyoutChildren(attached)) yield return i;
    }

    static IEnumerable<object> FlyoutChildren(FlyoutBase f) => f switch
    {
        Flyout { Content: UIElement c } => [c],
        MenuFlyout m => m.Items.Cast<object>(),
        _ => [],
    };

    static void Size(FrameworkElement e, double text, double box)
    {
        var was = Design(e);
        if (!double.IsNaN(was[Font]))
        {
            var size = Math.Round(was[Font] * (e is IconElement ? box : text), 1);
            switch (e)
            {
                case TextBlock t: t.FontSize = size; break;
                case Control c: c.FontSize = size; break;
                case FontIcon i: i.FontSize = size; break;
            }
        }
        double S(int i) => Math.Round(was[i] * box);
        if (!double.IsNaN(was[Width])) e.Width = S(Width);
        if (!double.IsNaN(was[Height])) e.Height = S(Height);
        if (!double.IsNaN(was[MinWidth])) e.MinWidth = S(MinWidth);
        if (!double.IsNaN(was[MinHeight])) e.MinHeight = S(MinHeight);
        if (!double.IsNaN(was[MaxWidth])) e.MaxWidth = S(MaxWidth);
        if (!double.IsNaN(was[MaxHeight])) e.MaxHeight = S(MaxHeight);
        if (!double.IsNaN(was[Pad]))
        {
            var p = new Thickness(S(Pad), S(Pad + 1), S(Pad + 2), S(Pad + 3));
            switch (e)
            {
                case Control c: c.Padding = p; break;
                case Border b: b.Padding = p; break;
                case StackPanel sp: sp.Padding = p; break;
                case Grid g: g.Padding = p; break;
                case TextBlock t: t.Padding = p; break;
            }
        }
        switch (e)
        {
            case StackPanel sp when !double.IsNaN(was[Spacing]):
                sp.Spacing = S(Spacing);
                break;
            case Grid g:
                if (!double.IsNaN(was[RowSpacing])) g.RowSpacing = S(RowSpacing);
                if (!double.IsNaN(was[ColumnSpacing])) g.ColumnSpacing = S(ColumnSpacing);
                foreach (var r in g.RowDefinitions)
                    if (Fixed(r, r.Height) is { } h) r.Height = new GridLength(Math.Round(h * box));
                foreach (var c in g.ColumnDefinitions)
                    if (Fixed(c, c.Width) is { } w) c.Width = new GridLength(Math.Round(w * box));
                break;
            case VariableSizedWrapGrid v:
                if (!double.IsNaN(was[ItemWidth])) v.ItemWidth = S(ItemWidth);
                if (!double.IsNaN(was[ItemHeight])) v.ItemHeight = S(ItemHeight);
                break;
        }
    }

    static double[] Design(FrameworkElement e)
    {
        if (e.GetValue(DesignProperty) is string kept) return kept.Split(';').Select(x => double.Parse(x, inv)).ToArray();
        var was = Remember(e);
        e.SetValue(DesignProperty, string.Join(';', was.Select(x => x.ToString("R", inv))));
        return was;
    }

    static double? Fixed(DependencyObject definition, GridLength length)
    {
        var was = definition.GetValue(LengthProperty) is string kept ? double.Parse(kept, inv) : double.NaN;
        if (definition.GetValue(LengthProperty) is null)
        {
            was = length.IsAbsolute ? length.Value : double.NaN;
            definition.SetValue(LengthProperty, was.ToString("R", inv));
        }
        return double.IsNaN(was) ? null : was;
    }

    // Text with no size of its own in the XAML is body text (it would otherwise be 14 px, or whatever a style set).
    static double[] Remember(FrameworkElement e)
    {
        var w = Enumerable.Repeat(double.NaN, Pad + 4).ToArray();
        w[Font] = e switch
        {
            TextBlock t => Local(t, TextBlock.FontSizeProperty) ? t.FontSize : ShellMetrics.BaseText,
            Control c => Local(c, Control.FontSizeProperty) ? c.FontSize : ShellMetrics.BaseText,
            FontIcon i => Local(i, FontIcon.FontSizeProperty) ? i.FontSize : double.NaN,
            _ => double.NaN,
        };
        w[Width] = e.Width;
        w[Height] = e.Height;
        if (e.MinWidth > 0) w[MinWidth] = e.MinWidth;
        if (e.MinHeight > 0) w[MinHeight] = e.MinHeight;
        if (!double.IsInfinity(e.MaxWidth)) w[MaxWidth] = e.MaxWidth;
        if (!double.IsInfinity(e.MaxHeight)) w[MaxHeight] = e.MaxHeight;
        Thickness? padding = e switch
        {
            Control c => c.Padding,
            Border b => b.Padding,
            StackPanel sp => sp.Padding,
            Grid g => g.Padding,
            TextBlock t => t.Padding,
            _ => null,
        };
        if (padding is { } p && (p.Left != 0 || p.Top != 0 || p.Right != 0 || p.Bottom != 0))
            (w[Pad], w[Pad + 1], w[Pad + 2], w[Pad + 3]) = (p.Left, p.Top, p.Right, p.Bottom);
        switch (e)
        {
            case StackPanel sp when sp.Spacing > 0:
                w[Spacing] = sp.Spacing;
                break;
            case Grid g:
                if (g.RowSpacing > 0) w[RowSpacing] = g.RowSpacing;
                if (g.ColumnSpacing > 0) w[ColumnSpacing] = g.ColumnSpacing;
                break;
            case VariableSizedWrapGrid v:
                w[ItemWidth] = v.ItemWidth;
                w[ItemHeight] = v.ItemHeight;
                break;
        }
        return w;
    }

    static bool Local(DependencyObject d, DependencyProperty p) => d.ReadLocalValue(p) != DependencyProperty.UnsetValue;
}
