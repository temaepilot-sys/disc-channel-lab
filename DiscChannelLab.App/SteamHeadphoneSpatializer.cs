using System.Numerics;
using System.Runtime.InteropServices;

namespace Disc2Flac;

/// <summary>Steam Audio 4.8.1 C API; one stateful binaural effect per discrete input channel.</summary>
public sealed unsafe class SteamHeadphoneSpatializer : IHeadphoneSpatializer
{
    private IntPtr _context, _hrtf;
    private GCHandle _sofaData;
    private readonly IntPtr[] _effects;
    private static readonly object HrtfCreationGate = new();
    public int TailSamples => _effects.Max(effect => effect == IntPtr.Zero ? 0 : iplBinauralEffectGetTailSize(effect));
    static SteamHeadphoneSpatializer()
    {
        NativeLibrary.SetDllImportResolver(typeof(SteamHeadphoneSpatializer).Assembly, (name, assembly, path) =>
        {
            if (name != "phonon") return IntPtr.Zero;
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, "tools", "steamaudio", "phonon.dll");
                if (File.Exists(candidate)) return NativeLibrary.Load(candidate);
            }
            throw new DllNotFoundException("Steam Audio phonon.dll was not found in tools/steamaudio.");
        });
    }
    public SteamHeadphoneSpatializer(int channels, HeadphoneOptions? options = null)
    {
        _effects = new IntPtr[channels];
        try
        {
            var context = new ContextSettings { Version = 0x00040801, Simd = 0 };
            Check(iplContextCreate(ref context, out _context), "create context");
            var audio = new AudioSettings { Rate = HeadphoneRenderer.SampleRate, Frames = HeadphoneRenderer.BlockSize };
            var sofa = options?.Hrtf == HeadphoneHrtf.Sofa ? SofaHrtf.Read(options.SofaPath) : null;
            // Keep externally owned SOFA bytes pinned until the native HRTF is released.
            // Validation and playback therefore use the same lifetime guarantee.
            if (sofa is not null) _sofaData = GCHandle.Alloc(sofa, GCHandleType.Pinned);
            var hrtf = new HrtfSettings { Type = sofa is null ? 0 : 1, SofaData = _sofaData.IsAllocated ? _sofaData.AddrOfPinnedObject() : IntPtr.Zero,
                SofaSize = sofa?.Length ?? 0, Volume = 1, Normalization = 0 };
            int error;
            // Steam Audio documents HRTF creation as non-thread-safe. UI validation and
            // playback preparation may overlap; never serialize the real-time Apply calls.
            lock (HrtfCreationGate) error = iplHRTFCreate(_context, ref audio, ref hrtf, out _hrtf);
            if (error != 0 && sofa is not null)
                throw new InvalidDataException(LanguageService.T("SOFAを読み込めません。SimpleFreeFieldHRIR・左右2耳・Data.Delayが0の対応ファイルを選択してください。") + $" ({error})");
            Check(error, "create HRTF");
            var effect = new EffectSettings { Hrtf = _hrtf };
            for (int i = 0; i < channels; i++) Check(iplBinauralEffectCreate(_context, ref audio, ref effect, out _effects[i]), "create binaural effect");
        }
        catch { Dispose(); throw; }
    }
    public void Process(int channel, float[] input, float[] left, float[] right, Vector3 direction, HeadphoneOptions options)
    {
        // Steam Audio uses -Z forward. The audio coordinate system uses +Z forward.
        var parameters = new EffectParameters { Direction = new(direction.X, direction.Y, -direction.Z), Interpolation = 1, SpatialBlend = 1, Hrtf = _hrtf };
        fixed (float* source = input, l = left, r = right)
        {
            float** sources = stackalloc float*[1]; sources[0] = source;
            float** targets = stackalloc float*[2]; targets[0] = l; targets[1] = r;
            var inBuffer = new AudioBuffer { Channels = 1, Frames = input.Length, Data = sources };
            var outBuffer = new AudioBuffer { Channels = 2, Frames = input.Length, Data = targets };
            iplBinauralEffectApply(_effects[channel], ref parameters, ref inBuffer, ref outBuffer);
        }
    }
    public void Dispose()
    {
        for (var i = 0; i < _effects.Length; i++) if (_effects[i] != IntPtr.Zero) iplBinauralEffectRelease(ref _effects[i]);
        if (_hrtf != IntPtr.Zero) iplHRTFRelease(ref _hrtf);
        if (_context != IntPtr.Zero) iplContextRelease(ref _context);
        if (_sofaData.IsAllocated) _sofaData.Free();
    }
    private static void Check(int error, string action) { if (error != 0) throw new InvalidOperationException($"Steam Audio: {action} failed ({error})."); }
    [StructLayout(LayoutKind.Sequential)] private struct ContextSettings { public uint Version; public IntPtr Log, Allocate, Free; public int Simd, Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct AudioSettings { public int Rate, Frames; }
    [StructLayout(LayoutKind.Sequential)] private struct HrtfSettings { public int Type; public IntPtr SofaFile, SofaData; public int SofaSize; public float Volume; public int Normalization; }
    [StructLayout(LayoutKind.Sequential)] private struct EffectSettings { public IntPtr Hrtf; }
    [StructLayout(LayoutKind.Sequential)] private struct Vector(float x, float y, float z) { public float X = x, Y = y, Z = z; }
    [StructLayout(LayoutKind.Sequential)] private struct EffectParameters { public Vector Direction; public int Interpolation; public float SpatialBlend; public IntPtr Hrtf, PeakDelays; }
    [StructLayout(LayoutKind.Sequential)] private struct AudioBuffer { public int Channels, Frames; public float** Data; }
    [DllImport("phonon", CallingConvention = CallingConvention.StdCall)] private static extern int iplContextCreate(ref ContextSettings settings, out IntPtr context);
    [DllImport("phonon", CallingConvention = CallingConvention.StdCall)] private static extern void iplContextRelease(ref IntPtr context);
    [DllImport("phonon", CallingConvention = CallingConvention.StdCall)] private static extern int iplHRTFCreate(IntPtr context, ref AudioSettings audio, ref HrtfSettings settings, out IntPtr hrtf);
    [DllImport("phonon", CallingConvention = CallingConvention.StdCall)] private static extern void iplHRTFRelease(ref IntPtr hrtf);
    [DllImport("phonon", CallingConvention = CallingConvention.StdCall)] private static extern int iplBinauralEffectCreate(IntPtr context, ref AudioSettings audio, ref EffectSettings settings, out IntPtr effect);
    [DllImport("phonon", CallingConvention = CallingConvention.StdCall)] private static extern void iplBinauralEffectRelease(ref IntPtr effect);
    [DllImport("phonon", CallingConvention = CallingConvention.StdCall)] private static extern int iplBinauralEffectApply(IntPtr effect, ref EffectParameters parameters, ref AudioBuffer input, ref AudioBuffer output);
    [DllImport("phonon", CallingConvention = CallingConvention.StdCall)] private static extern int iplBinauralEffectGetTailSize(IntPtr effect);
}
