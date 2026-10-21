using System.Globalization;

namespace Lilt.Core.Theming;

public readonly record struct Rgba(byte R, byte G, byte B, byte A = 255)
{
    public static bool TryParse(string? s, out Rgba color)
    {
        color = default;
        if (s is not { Length: 4 or 7 or 9 } || s[0] != '#') return false;
        var hex = s.AsSpan(1);
        foreach (var c in hex)
            if (!char.IsAsciiHexDigit(c)) return false;
        if (hex.Length == 3)
        {
            color = new(Dup(hex[0]), Dup(hex[1]), Dup(hex[2]));
            return true;
        }
        color = new(Byte(hex, 0), Byte(hex, 2), Byte(hex, 4), hex.Length == 8 ? Byte(hex, 6) : (byte)255);
        return true;

        static byte Dup(char c) => byte.Parse([c, c], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        static byte Byte(ReadOnlySpan<char> h, int i) => byte.Parse(h.Slice(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    public static Rgba Parse(string s) => TryParse(s, out var c) ? c : throw new FormatException($"Invalid color '{s}'.");

    public Rgba WithAlpha(byte a) => this with { A = a };

    public Rgba Opaque => this with { A = 255 };

    public override string ToString() => A == 255 ? $"#{R:X2}{G:X2}{B:X2}" : $"#{R:X2}{G:X2}{B:X2}{A:X2}";
}

// The toolbar and sidebar take on the site's color as a tint over the theme's surface, as strong as asked but never so
// strong that the theme's text stops being readable on it (WCAG contrast 4.5:1).
public static class SiteTint
{
    public static Rgba For(Rgba site, Rgba surface, Rgba text, double strength)
    {
        for (var a = Math.Clamp(strength, 0, 1); a > 0.001; a -= 0.05)
        {
            var mixed = new Rgba(Mix(surface.R, site.R, a), Mix(surface.G, site.G, a), Mix(surface.B, site.B, a));
            if (Contrast(mixed, text) >= 4.5) return site.Opaque.WithAlpha((byte)Math.Round(a * 255));
        }
        return site.WithAlpha(0);
    }

    static byte Mix(byte from, byte to, double a) => (byte)Math.Round(from + (to - from) * a);

    public static double Contrast(Rgba x, Rgba y)
    {
        double lx = Luminance(x), ly = Luminance(y);
        return (Math.Max(lx, ly) + 0.05) / (Math.Min(lx, ly) + 0.05);
    }

    static double Luminance(Rgba c)
    {
        static double Lin(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
    }
}
