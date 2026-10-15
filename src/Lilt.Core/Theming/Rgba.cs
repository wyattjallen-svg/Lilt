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
