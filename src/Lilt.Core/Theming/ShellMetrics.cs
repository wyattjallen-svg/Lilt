namespace Lilt.Core.Theming;

// The sizes Lilt's chrome is drawn at. Lilt's XAML is designed at 100% with 13 px body text; Scale is the interface size
// (theme "scale") and Text multiplies the XAML's text sizes for the text size and interface size together. Unit is the
// spacing step (density); the rest are pixels. Text never outgrows its box: controls and rows grow with it.
public readonly record struct ShellMetrics(double Scale, double Text, double Unit, double ToolbarHeight, double ControlHeight, double IconSize,
    double RowHeight, bool TallCaption)
{
    public const double TallCaptionHeight = 48;
    public const double BaseText = 13;

    public double Font(double design) => Math.Round(design * Text, 1);
    public double Px(double design) => Math.Round(design * Scale);
    public double Body => Font(BaseText);

    public static ShellMetrics From(Theme theme)
    {
        var s = Math.Clamp(theme.Scale, 0.75, 1.5);
        var text = theme.Typography.Size / BaseText * s;
        var body = BaseText * text;
        var unit = theme.Density switch
        {
            Density.Compact => 4,
            Density.Comfortable => 8,
            _ => 6,
        } * s;
        var bar = Math.Round(theme.Toolbar.Height * s);
        var control = Math.Max(Math.Clamp(Math.Round(bar * 0.75), Math.Round(24 * s), bar - Math.Round(4 * s)), Math.Round(body * 1.85));
        bar = Math.Max(bar, control + Math.Round(4 * s));
        var icon = Math.Clamp(Math.Round(control * 0.5), Math.Round(12 * s), Math.Round(20 * s));
        var row = Math.Max(Math.Round(30 * s), Math.Round(body * 2.3));
        return new(s, text, unit, bar, control, icon, row, bar >= TallCaptionHeight);
    }
}
