using System.Runtime.InteropServices;

namespace Lilt.App.Shell;

// Lilt's own sound, for the visualizer: Windows' process loopback (Windows 11) captures what Lilt and its WebView2
// child processes play, whatever the site (DRM music services included). Nothing is recorded or kept; each packet goes
// straight to the callback on a background thread. Stops when disposed.
public sealed class LoopbackCapture : IDisposable
{
    public delegate void Samples(ReadOnlySpan<byte> pcm16Stereo);
    public const int SampleRate = 44100;

    readonly Thread thread;
    readonly AutoResetEvent ready = new(false);
    volatile bool stop;

    LoopbackCapture(IAudioClient client, IAudioCaptureClient capture, Samples onSamples)
    {
        thread = new Thread(() => Run(client, capture, onSamples)) { IsBackground = true, Name = "Lilt visualizer", Priority = ThreadPriority.BelowNormal };
        Check(client.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle()));
        Check(client.Start());
        thread.Start();
    }

    // Throws if process loopback isn't available (Windows 10, no audio service) or activation fails.
    public static LoopbackCapture Start(int processId, Samples onSamples)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348)) throw new PlatformNotSupportedException("Needs Windows 11.");
        // COM calls happen on an MTA thread of our own, so the UI thread never waits on the audio service.
        LoopbackCapture? result = null;
        Exception? error = null;
        var setup = new Thread(() =>
        {
            try { result = Create(processId, onSamples); }
            catch (Exception e) { error = e; }
        });
        setup.SetApartmentState(ApartmentState.MTA);
        setup.Start();
        if (!setup.Join(TimeSpan.FromSeconds(5))) throw new TimeoutException("The audio service didn't answer.");
        return result ?? throw error ?? new InvalidOperationException("No audio client.");
    }

    static LoopbackCapture Create(int processId, Samples onSamples)
    {
        var p = new ActivationParams { Type = 1, ProcessId = (uint)processId, Mode = 0 };
        var blob = Marshal.AllocHGlobal(Marshal.SizeOf<ActivationParams>());
        try
        {
            Marshal.StructureToPtr(p, blob, false);
            var variant = new PropVariant { Type = 65, Size = (uint)Marshal.SizeOf<ActivationParams>(), Data = blob };
            var done = new Completion();
            ActivateAudioInterfaceAsync(@"VAD\Process_Loopback", typeof(IAudioClient).GUID, ref variant, done, out var op);
            if (!done.Event.Wait(TimeSpan.FromSeconds(4))) throw new TimeoutException("Audio activation timed out.");
            op.GetActivateResult(out var hr, out var unknown);
            Check(hr);
            var client = (IAudioClient)unknown;
            var format = new WaveFormat { Tag = 1, Channels = 2, SamplesPerSec = SampleRate, BitsPerSample = 16, BlockAlign = 4, AvgBytesPerSec = SampleRate * 4 };
            const uint loopback = 0x00020000, eventCallback = 0x00040000, autoConvert = 0x80000000;
            Check(client.Initialize(0, loopback | eventCallback | autoConvert, 0, 0, ref format, IntPtr.Zero));
            var iid = typeof(IAudioCaptureClient).GUID;
            Check(client.GetService(ref iid, out var service));
            return new LoopbackCapture(client, (IAudioCaptureClient)service, onSamples);
        }
        finally
        {
            Marshal.FreeHGlobal(blob);
        }
    }

    void Run(IAudioClient client, IAudioCaptureClient capture, Samples onSamples)
    {
        var buffer = new byte[16384];
        try
        {
            while (!stop)
            {
                if (!ready.WaitOne(200)) continue;
                while (!stop && capture.GetNextPacketSize(out var frames) >= 0 && frames > 0)
                {
                    if (capture.GetBuffer(out var data, out var count, out var flags, out _, out _) < 0) break;
                    const uint silent = 0x2;
                    var bytes = (int)Math.Min(count * 4, (uint)buffer.Length);
                    if ((flags & silent) == 0 && bytes > 0)
                    {
                        Marshal.Copy(data, buffer, 0, bytes);
                        onSamples(buffer.AsSpan(0, bytes));
                    }
                    capture.ReleaseBuffer(count);
                }
            }
        }
        catch (Exception e)
        {
            LiltPaths.LogError(e);
        }
        finally
        {
            client.Stop();
            Marshal.ReleaseComObject(capture);
            Marshal.ReleaseComObject(client);
        }
    }

    public void Dispose()
    {
        stop = true;
        ready.Set();
    }

    static void Check(int hr)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    static extern void ActivateAudioInterfaceAsync([MarshalAs(UnmanagedType.LPWStr)] string path, [MarshalAs(UnmanagedType.LPStruct)] Guid iid,
        ref PropVariant parameters, IActivateAudioInterfaceCompletionHandler handler, out IActivateAudioInterfaceAsyncOperation operation);

    [StructLayout(LayoutKind.Sequential)]
    public struct ActivationParams
    {
        public int Type;
        public uint ProcessId;
        public int Mode;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PropVariant
    {
        public ushort Type, Reserved1, Reserved2, Reserved3;
        public uint Size;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    public struct WaveFormat
    {
        public ushort Tag, Channels;
        public int SamplesPerSec, AvgBytesPerSec;
        public ushort BlockAlign, BitsPerSample, Size;
    }

    [ComVisible(true)]
    public sealed class Completion : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public readonly ManualResetEventSlim Event = new();
        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation) => Event.Set();
    }

    [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAgileObject { }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IActivateAudioInterfaceAsyncOperation
    {
        void GetActivateResult(out int result, [MarshalAs(UnmanagedType.IUnknown)] out object activated);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint flags, long bufferDuration, long periodicity, ref WaveFormat format, IntPtr session);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint frames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long period, out long minimum);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }
}
