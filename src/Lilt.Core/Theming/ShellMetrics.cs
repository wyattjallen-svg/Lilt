namespace Lilt.Core.Theming;

public readonly record struct ShellMetrics(double Unit, double ToolbarHeight, double ControlHeight, double IconSize, bool TallCaption)
{
    public const double TallCaptionHeight = 48;

    public static ShellMetrics From(Theme theme)
    {
        var unit = theme.Density switch
        {
            Density.Compact => 4,
            Density.Comfortable => 8,
            _ => 6,
        };
        var bar = theme.Toolbar.Height;
        var control = Math.Clamp(Math.Round(bar * 0.75), 24, bar - 4);
        var icon = Math.Clamp(Math.Round(control * 0.5), 12, 20);
        return new(unit, bar, control, icon, bar >= TallCaptionHeight);
    }
}
