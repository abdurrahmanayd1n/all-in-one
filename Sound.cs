// What the computer plays, or what the microphone hears, taken straight from Windows (WASAPI) into a WAV file
// of 48 kHz, two channels, 16 bit, while the screen is recorded.
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

class SoundTrack : IDisposable
{
    public const int Rate = 48000, FrameBytes = 4;

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class DeviceList { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        void _EnumAudioEndpoints();
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid id, int context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object made);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct Format
    {
        public ushort tag, channels;
        public uint rate, bytesPerSecond;
        public ushort blockAlign, bits, extra;
    }

    // Declared up to the method that is used: the order is the vtable order.
    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint flags, long duration, long period, ref Format format, IntPtr session);
        void _GetBufferSize();
        void _GetStreamLatency();
        void _GetCurrentPadding();
        void _IsFormatSupported();
        void _GetMixFormat();
        void _GetDevicePeriod();
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        void _Reset();
        void _SetEventHandle();
        [PreserveSig] int GetService(ref Guid id, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong clockPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }

    public readonly string File;
    public DateTime Started { get; private set; } // when listening began (UTC)
    readonly bool microphone;
    readonly ManualResetEvent ready = new ManualResetEvent(false);
    readonly Thread thread;
    volatile bool stopping;
    Exception failure;

    // Starts listening; throws when the device cannot be opened (none there, or Windows refuses the microphone).
    public SoundTrack(bool microphone, string file)
    {
        this.microphone = microphone;
        File = file;
        thread = new Thread(Capture) { IsBackground = true, Priority = ThreadPriority.AboveNormal };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        ready.WaitOne(5000);
        if (failure != null) throw new IOException(failure.Message, failure);
    }

    // Asks it to end, without waiting.
    public void Signal() { stopping = true; }

    // Ends it and completes the file.
    public void Dispose()
    {
        stopping = true;
        thread.Join(5000);
    }

    static void Check(int result)
    {
        if (result < 0) Marshal.ThrowExceptionForHR(result);
    }

    static object Open(IMMDevice device, Type wanted)
    {
        Guid id = wanted.GUID;
        object made;
        Check(device.Activate(ref id, 23 /* CLSCTX_ALL */, IntPtr.Zero, out made));
        return made;
    }

    // The header of a WAV file with so many bytes of sound.
    public static byte[] Header(long bytes)
    {
        var memory = new MemoryStream();
        var w = new BinaryWriter(memory);
        w.Write(new[] { 'R', 'I', 'F', 'F' });
        w.Write((uint)(36 + bytes));
        w.Write(new[] { 'W', 'A', 'V', 'E', 'f', 'm', 't', ' ' });
        w.Write(16);
        w.Write((short)1);
        w.Write((short)2);
        w.Write(Rate);
        w.Write(Rate * FrameBytes);
        w.Write((short)FrameBytes);
        w.Write((short)16);
        w.Write(new[] { 'd', 'a', 't', 'a' });
        w.Write((uint)bytes);
        return memory.ToArray();
    }

    // Windows hands over nothing while nothing plays: how many silent frames keep the file as long as the time.
    public static long Missing(long written, double seconds)
    {
        long due = (long)(seconds * Rate);
        return due - written > Rate / 10 ? due - Rate / 20 - written : 0;
    }

    void Capture()
    {
        try
        {
            var list = (IMMDeviceEnumerator)new DeviceList();
            IMMDevice device;
            // The default device: what is played (loopback) or the microphone.
            Check(list.GetDefaultAudioEndpoint(microphone ? 1 : 0, 0, out device));
            var client = (IAudioClient)Open(device, typeof(IAudioClient));
            var format = new Format { tag = 1, channels = 2, rate = Rate, bytesPerSecond = Rate * FrameBytes,
                blockAlign = FrameBytes, bits = 16 };
            // Windows converts whatever the device uses to this form.
            uint flags = 0x80000000 | 0x08000000 | (microphone ? 0u : 0x20000);
            Check(client.Initialize(0 /* shared */, flags, 2000000 /* 200 ms */, 0, ref format, IntPtr.Zero));
            Guid id = typeof(IAudioCaptureClient).GUID;
            object service;
            Check(client.GetService(ref id, out service));
            var capture = (IAudioCaptureClient)service;
            using (var output = new FileStream(File, FileMode.Create, FileAccess.Write))
            {
                output.Write(Header(0), 0, 44);
                Check(client.Start());
                Started = DateTime.UtcNow;
                var clock = Stopwatch.StartNew();
                long written = 0; // frames
                ready.Set();
                while (!stopping)
                {
                    uint waiting;
                    Check(capture.GetNextPacketSize(out waiting));
                    if (waiting == 0)
                    {
                        long silent = Missing(written, clock.Elapsed.TotalSeconds);
                        if (silent > 0)
                        {
                            output.Write(new byte[silent * FrameBytes], 0, (int)(silent * FrameBytes));
                            written += silent;
                        }
                        Thread.Sleep(5);
                        continue;
                    }
                    IntPtr data;
                    uint frames, state;
                    ulong position, time;
                    Check(capture.GetBuffer(out data, out frames, out state, out position, out time));
                    var sound = new byte[frames * FrameBytes];
                    if ((state & 2 /* silent */) == 0) Marshal.Copy(data, sound, 0, sound.Length);
                    Check(capture.ReleaseBuffer(frames));
                    output.Write(sound, 0, sound.Length);
                    written += frames;
                }
                client.Stop();
                output.Position = 0;
                output.Write(Header(written * FrameBytes), 0, 44);
            }
        }
        catch (Exception e)
        {
            failure = e;
            Log.Error(microphone ? "mikrofon alınamadı" : "bilgisayar sesi alınamadı", e);
        }
        finally { ready.Set(); }
    }
}
