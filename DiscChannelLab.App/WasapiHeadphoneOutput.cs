using System.Runtime.InteropServices;

namespace Disc2Flac;

/// <summary>Shared, event-driven Windows stereo output. Orientation is applied immediately before Write.</summary>
public sealed class WasapiHeadphoneOutput : IHeadphoneOutput
{
    private readonly IAudioClient _client = null!;
    private readonly IAudioRenderClient _render = null!;
    private readonly IMMDevice _device = null!;
    private readonly IMMDeviceEnumerator _enumerator = null!;
    private readonly AutoResetEvent _event = new(false);
    private bool _started;
    private bool _disposed;
    public uint BufferFrames { get; }
    public long SubmittedFrames { get; private set; }
    public long PlayedFrames => Math.Max(0, SubmittedFrames - Padding);
    public uint Padding { get { Marshal.ThrowExceptionForHR(_client.GetCurrentPadding(out var padding)); return padding; } }
    public double BufferMilliseconds => BufferFrames * 1000d / HeadphoneRenderer.SampleRate;
    public WasapiHeadphoneOutput()
    {
        try
        {
        _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        Marshal.ThrowExceptionForHR(_enumerator.GetDefaultAudioEndpoint(0, 0, out _device));
        var iid = typeof(IAudioClient).GUID;
        Marshal.ThrowExceptionForHR(_device.Activate(ref iid, 23, IntPtr.Zero, out var client));
        _client = (IAudioClient)client;
        var format = new WaveFormat { Tag = 3, Channels = 2, Rate = 48000, AverageBytes = 384000, BlockAlign = 8, Bits = 32 };
        // 20 ms device buffer, raw floating point stereo. Windows performs any necessary endpoint conversion.
        Marshal.ThrowExceptionForHR(_client.Initialize(0, 0x00040000 | 0x80000000 | 0x08000000, 200000, 0, ref format, IntPtr.Zero));
        Marshal.ThrowExceptionForHR(_client.GetBufferSize(out var size)); BufferFrames = size;
        if (size < HeadphoneRenderer.BlockSize) throw new InvalidOperationException("Audio endpoint buffer is too small.");
        Marshal.ThrowExceptionForHR(_client.SetEventHandle(_event.SafeWaitHandle.DangerousGetHandle()));
        iid = typeof(IAudioRenderClient).GUID;
        Marshal.ThrowExceptionForHR(_client.GetService(ref iid, out var render)); _render = (IAudioRenderClient)render;
        }
        catch { Dispose(); throw; }
    }
    public void WaitForSpace(int frames, CancellationToken token)
    {
        while (BufferFrames - Padding < frames)
        {
            token.ThrowIfCancellationRequested();
            _event.WaitOne(10);
        }
    }
    public void Write(float[] interleaved, int frames)
    {
        Marshal.ThrowExceptionForHR(_render.GetBuffer((uint)frames, out var data));
        try { Marshal.Copy(interleaved, 0, data, frames * 2); }
        finally { Marshal.ThrowExceptionForHR(_render.ReleaseBuffer((uint)frames, 0)); }
        SubmittedFrames += frames;
        if (!_started) { Marshal.ThrowExceptionForHR(_client.Start()); _started = true; }
    }
    public void Drain(CancellationToken token)
    {
        while (Padding > 0) { token.ThrowIfCancellationRequested(); _event.WaitOne(10); }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_started) _client.Stop();
        if (_render is not null) Marshal.ReleaseComObject(_render);
        if (_client is not null) Marshal.ReleaseComObject(_client);
        if (_device is not null) Marshal.ReleaseComObject(_device);
        if (_enumerator is not null) Marshal.ReleaseComObject(_enumerator);
        _event.Dispose();
    }
    [StructLayout(LayoutKind.Sequential, Pack = 2)] private struct WaveFormat
    { public ushort Tag, Channels; public uint Rate, AverageBytes; public ushort BlockAlign, Bits, Extra; }
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] private class MMDeviceEnumerator { }
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint mask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr callback);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr callback);
    }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object result);
        [PreserveSig] int OpenPropertyStore(uint access, out IntPtr properties);
        [PreserveSig] int GetId(out IntPtr id);
        [PreserveSig] int GetState(out uint state);
    }
    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] private interface IAudioClient
    {
        [PreserveSig] int Initialize(int mode, uint flags, long duration, long periodicity, ref WaveFormat format, IntPtr session);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(int mode, IntPtr format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long normal, out long minimum);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }
    [ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] private interface IAudioRenderClient
    {
        [PreserveSig] int GetBuffer(uint frames, out IntPtr data);
        [PreserveSig] int ReleaseBuffer(uint frames, uint flags);
    }
}
