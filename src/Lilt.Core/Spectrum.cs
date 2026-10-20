namespace Lilt.Core;

// Turns audio samples into visualizer bar heights (0-1): a Hann-windowed FFT, bands spaced logarithmically from
// 40 Hz to 16 kHz, levels in decibels, and quick-rise / slow-fall smoothing so bars look natural.
public sealed class Spectrum
{
    public const int Size = 1024;
    readonly float[] window = new float[Size], re = new float[Size], im = new float[Size], ring = new float[Size];
    readonly float[] bars;
    readonly int[] edges;
    int pos, filled;

    public Spectrum(int barCount, int sampleRate)
    {
        bars = new float[barCount];
        for (var i = 0; i < Size; i++) window[i] = 0.5f - 0.5f * MathF.Cos(2 * MathF.PI * i / (Size - 1));
        // Band edges as FFT bin indexes, at least one bin per band.
        edges = new int[barCount + 1];
        double lo = 40, hi = Math.Min(16000, sampleRate / 2.0);
        for (var b = 0; b <= barCount; b++)
        {
            var f = lo * Math.Pow(hi / lo, (double)b / barCount);
            edges[b] = Math.Clamp((int)Math.Round(f * Size / sampleRate), 1, Size / 2 - 1);
            if (b > 0 && edges[b] <= edges[b - 1]) edges[b] = Math.Min(edges[b - 1] + 1, Size / 2);
        }
    }

    public IReadOnlyList<float> Bars => bars;

    // Mono samples in -1..1, any count; keeps the latest Size samples.
    public void Add(ReadOnlySpan<float> samples)
    {
        foreach (var s in samples)
        {
            ring[pos] = s;
            pos = (pos + 1) % Size;
            if (filled < Size) filled++;
        }
    }

    // Interleaved 16-bit PCM, mixed down to mono.
    public void AddPcm16(ReadOnlySpan<byte> data, int channels)
    {
        var frame = 2 * channels;
        Span<float> mono = stackalloc float[256];
        var n = 0;
        for (var i = 0; i + frame <= data.Length; i += frame)
        {
            float sum = 0;
            for (var c = 0; c < channels; c++) sum += (short)(data[i + 2 * c] | (data[i + 2 * c + 1] << 8)) / 32768f;
            mono[n++] = sum / channels;
            if (n == mono.Length)
            {
                Add(mono);
                n = 0;
            }
        }
        Add(mono[..n]);
    }

    // Recomputes the bars from the latest samples. Returns true if anything is audible.
    public bool Update()
    {
        // Oldest sample first; before the ring fills, the missing start is silence.
        for (var i = 0; i < Size; i++)
        {
            var age = Size - i;
            re[i] = age <= filled ? ring[(pos - age + Size) % Size] * window[i] : 0;
            im[i] = 0;
        }
        Fft(re, im);
        var any = false;
        for (var b = 0; b < bars.Length; b++)
        {
            float peak = 0;
            for (var k = edges[b]; k < Math.Max(edges[b + 1], edges[b] + 1); k++)
                peak = MathF.Max(peak, MathF.Sqrt(re[k] * re[k] + im[k] * im[k]) / (Size / 4f));
            // -60 dB..0 dB mapped to 0..1.
            var level = Math.Clamp((20 * MathF.Log10(peak + 1e-9f) + 60) / 60, 0, 1);
            bars[b] = level > bars[b] ? level : bars[b] * 0.82f + level * 0.18f;
            any |= level > 0.05f;
        }
        return any;
    }

    public void Clear()
    {
        Array.Clear(ring);
        Array.Clear(bars);
        pos = filled = 0;
    }

    static void Fft(float[] re, float[] im)
    {
        var n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }
        for (var len = 2; len <= n; len <<= 1)
        {
            var angle = -2 * MathF.PI / len;
            float wr = MathF.Cos(angle), wi = MathF.Sin(angle);
            for (var i = 0; i < n; i += len)
            {
                float cr = 1, ci = 0;
                for (var k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = a + len / 2;
                    var tr = re[b] * cr - im[b] * ci;
                    var ti = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - tr;
                    im[b] = im[a] - ti;
                    re[a] += tr;
                    im[a] += ti;
                    (cr, ci) = (cr * wr - ci * wi, cr * wi + ci * wr);
                }
            }
        }
    }
}
