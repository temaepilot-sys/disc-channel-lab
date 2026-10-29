using Disc2Flac;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

internal static class SpatialLabTests
{
    static readonly PreviewMixState Unity = new(StereoMixSettings.Default, null);
    public static async Task Run(string output, string fixtures)
    {
        string root = Path.GetFullPath(output); Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("DISCCHANNEL_DATA_HOME", root);
        var checks = new List<string>(); var measured = new List<object>();
        void Check(bool condition, string name) { if (!condition) throw new InvalidDataException(name); checks.Add(name); Console.WriteLine("PASS " + name); }
        using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(4)); var ct = stop.Token;
        var paths = new ToolPaths();
        var options = new HeadphoneOptions { Enabled = true, Method = HeadphoneMethod.SteamAudio, HeadroomDb = -12, SmoothingMs = 0 };
        var snapshot = new SpatialRenderSnapshot(options, new(0,0,0), Unity, 1, "builtin-4.8.1");
        string kemar = Path.Combine(fixtures, "MIT_KEMAR_large_pinna.sofa"), cipic = Path.Combine(fixtures, "CIPIC_subject_003_hrir_final.sofa");
        var library = new SofaLibrary();
        var hk = await library.RegisterAsync(kemar, ct); var hc = await library.RegisterAsync(cipic, ct);
        Check(hk.Status == "Compatible" && hc.Status == "Compatible" && library.Entries.Count == 3, "P1: native-validated built-in + two SOFAs registered");
        Check(hk.Directions > 1 && hk.Receivers == 2 && hk.SampleRate > 0 && hk.Hash.Length == 64, "P1: HDF5 metadata and SHA256 read without guessing");
        await library.RegisterAsync(kemar, ct); Check(library.Entries.Count == 3, "P1: identical SHA256 is not duplicated");
        foreach (var name in new[] { "CIPIC_subject_003_hrir_final_itdInDelayField.sofa", "D1_48K_24bit_0.3s_FIR_SOFA.sofa" })
        { var h = await library.RegisterAsync(Path.Combine(fixtures, name), ct); Check(h.Status == "Unsupported" && h.Diagnostic.Length > 0, "P1: unsupported SOFA rejected with reason: " + name); }
        Check(new SofaLibrary().Entries.Count == 5, "P1: library persists compatible and unsupported diagnostics");
        bool missing = false; try { SofaLibrary.Apply(hk with { Path = Path.Combine(root, "missing.sofa") }, options); } catch (InvalidDataException) { missing = true; }
        Check(missing, "P1: missing SOFA cannot silently change current HRTF");
        string pcm = Path.Combine(root, "input-51.f32");
        const int frames = 48000; string[] codes = ["FL", "FR", "FC", "LFE", "SL", "SR"];
        using (var file = File.Create(pcm)) using (var writer = new BinaryWriter(file))
            for (int f = 0; f < frames; f++) for (int c = 0; c < 6; c++) writer.Write((float)(.035 * Math.Sin(2 * Math.PI * (100 + c * 137) * f / 48000)));
        var input = new SpatialInput(pcm, 6, SpatialScene.ChannelBed(codes, frames), 0, frames, "original synthesized six-channel test", "test");
        foreach (var method in new[] { HeadphoneMethod.Direct, HeadphoneMethod.SteamAudio })
        {
            var settings = snapshot with { Options = options with { Method = method } };
            using var legacy = new HeadphoneRenderer(codes, settings.Options, clampOutput: false); using var shared = new SpatialBlockRenderer(input, settings);
            var source = new float[6][]; for (int c=0;c<6;c++) source[c] = new float[256]; var expected = new float[512]; var bytes = new byte[6144];
            using var file = File.OpenRead(pcm); double error = 0;
            for (int b=0;b<20;b++)
            {
                file.ReadExactly(bytes);
                for (int f=0;f<256;f++) for(int c=0;c<6;c++) source[c][f]=BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan((f*6+c)*4)));
                legacy.Render(source, expected, settings.Pose, settings.Options, Unity, 1); shared.Render(bytes);
                for(int i=0;i<512;i++) error=Math.Max(error, Math.Abs(expected[i]-shared.Output[i]));
            }
            Check(error <= 1e-6, $"P2: {method} shared scene renderer equals existing playback DSP (max {error:R})");
        }
        var rendered = await SpatialRendering.RenderAsync(input, snapshot, true, ct);
        var capture = new List<float>(); using (var control = new PreviewPlaybackControl())
        {
            var stats = await SpatialPcmPlayer.PlayAsync(input, snapshot, control, ct, outputFactory: () => new SilentDevice(), rawDspCapture: (a,n) => capture.AddRange(a.Take(n*2)));
            var offline = FloatFile(rendered.PcmPath); double error = 0;
            Check(capture.Count == offline.Length, "P2: real-time and offline include identical partial-block/tail frame count");
            for(int i=0;i<offline.Length;i++) error=Math.Max(error,Math.Abs(offline[i]-capture[i]));
            Check(error <= 1e-6 && stats.EmptyBuffers == 0, $"P2: actual WASAPI pre-device DSP PCM equals offline (max {error:R}); no underrun");
            measured.Add(new { mode="5.1 Pure HRTF", stats });
        }
        string wav = Path.Combine(root, "binaural.wav"), flac = Path.Combine(root, "binaural.flac");
        await SpatialRendering.ExportAsync(rendered, wav, paths.Ffmpeg, false, ct, writeRenderJson: true); await SpatialRendering.ExportAsync(rendered, flac, paths.Ffmpeg, true, ct, writeRenderJson: true);
        Check(File.Exists(wav + ".render.json") && File.Exists(flac + ".render.json"), "P2: both exports retain reproducibility sidecars");
        await SpatialRendering.RunAsync(paths.Ffmpeg, ["-v","error","-i",wav,"-f","f32le",Path.Combine(root,"wav-decoded.f32")],ct);
        Check(File.ReadAllBytes(rendered.PcmPath).SequenceEqual(File.ReadAllBytes(Path.Combine(root,"wav-decoded.f32"))), "P2: float WAV round-trips exact shared DSP PCM");
        await SpatialRendering.RunAsync(paths.Ffmpeg, ["-v","error","-i",flac,"-f","f32le",Path.Combine(root,"flac-decoded.f32")],ct);
        var quantized=FloatFile(Path.Combine(root,"flac-decoded.f32")); var raw=FloatFile(rendered.PcmPath);
        Check(quantized.Length==raw.Length && quantized.Zip(raw).Max(x=>Math.Abs(x.First-x.Second))<5e-7, "P2: 24-bit FLAC has only bounded quantization/dither error");
        var comparable = input with { Frames = 48000 };
        var ab=await SpatialRendering.CompareAsync(comparable,snapshot,snapshot with { Options=options with { SteamTrimDb=-3 } },paths.Ffmpeg,true,ct);
        Check(ab.Matched && Math.Abs((ab.MetricsA.IntegratedLufs!.Value+ab.GainADb)-(ab.MetricsB.IntegratedLufs!.Value+ab.GainBDb))<.02 && ab.GainADb<=0 && ab.GainBDb<=0,
            "P1: same samples match LUFS using scalar attenuation only");
        var ma=await SpatialRendering.MeasureAsync(ab.A,paths.Ffmpeg,ct,ab.GainADb);var mb=await SpatialRendering.MeasureAsync(ab.B,paths.Ffmpeg,ct,ab.GainBDb);
        Check(Math.Abs(ma.IntegratedLufs!.Value-mb.IntegratedLufs!.Value)<=.1,"P1: corrected PCM remeasurement within 0.1 LU");
        var room = new SpatialRoomOptions { Mode="Studio", LowAbsorption=0,MidAbsorption=0,HighAbsorption=0 };
        var left=EarlyReflections.Path(new(0,0,1.2f),room,0);var right=EarlyReflections.Path(new(0,0,1.2f),room,1);
        Check(Math.Abs(left.DelaySamples-right.DelaySamples)<1e-6 && left.Direction.X == -right.Direction.X && left.DelaySamples>0,"P3: image-source delays and directions have left-right symmetry");
        Check(Math.Abs(left.DelaySamples - ((left.Image.Length()-1.2)/343*48000))<.001,"P3: reflection timing is excess distance / speed of sound");
        var roomSnapshot=snapshot with { Options=options with { Room=room } };
        var wet=await SpatialRendering.RenderAsync(input,roomSnapshot,true,ct);
        Check(!FloatFile(wet.PcmPath).Take(frames*2).SequenceEqual(FloatFile(rendered.PcmPath).Take(frames*2)) && wet.TailFrames>rendered.TailFrames,"P3: reflections add directional energy and a measured delay tail");
        var pureAgain=await SpatialRendering.RenderAsync(input,snapshot with { Options=options with { Room=room with { Mode="Pure HRTF" } } },true,ct);
        Check(File.ReadAllBytes(pureAgain.PcmPath).SequenceEqual(File.ReadAllBytes(rendered.PcmPath)),"P3: room OFF exactly restores pure-HRTF PCM");
        using(var control = new PreviewPlaybackControl())
        {
            capture.Clear();var stats=await SpatialPcmPlayer.PlayAsync(input,roomSnapshot,control,ct,outputFactory:()=>new SilentDevice(),rawDspCapture:(a,n)=>capture.AddRange(a.Take(n*2)));
            var offline=FloatFile(wet.PcmPath); Check(capture.Count==offline.Length && capture.Zip(offline).All(x=>Math.Abs(x.First-x.Second)<=1e-6),"P3: early-reflection playback and export share exact DSP state");measured.Add(new{mode="5.1 Studio",stats});
        }
        string demo=Path.Combine(root,"original-adm-demo.wav");AdmDemo.Create(demo);var adm=AdmWave.Open(demo);
        Check(adm.Scene.Sources.Length==3 && adm.Scene.Sources.Count(s=>s.Kind==SpatialSourceKind.Object)==1,"P4: original ADM maps PCM, UID, object and two common-definition bed channels");
        var moving=adm.Scene.Sources.Single(s=>s.Kind==SpatialSourceKind.Object);
        Check(moving.At(48000).Position.X<0 && moving.At(192000).Position.X>-.001 && moving.At(240000).Position.Z<0,"P4: ADM positive-left, smooth movement and jumpPosition have distinct timing");
        Check(moving.At(6*48000).Position.Z<0 && moving.At(8*48000).Position.Y>.9,"P4: interpolationLength reaches the elevated position at its specified time");
        Check(moving.At(0).Gain==0 && moving.At(11*48000).Gain==0,"P4: object start/end gates prevent unrelated PCM outside object duration");
        Check(AdmWave.Time("00:00:00.1S48000")==1 && AdmWave.Time("00:01:00.00000")==2880000,"P4: rational and decimal ADM times convert to sample coordinates");
        var admInput=await SpatialRendering.PrepareAdmAsync(adm,0,48000*2,ct);
        var admRendered=await SpatialRendering.RenderAsync(admInput,snapshot,true,ct);
        using(var control=new PreviewPlaybackControl())
        {
            capture.Clear();var stats=await SpatialPcmPlayer.PlayAsync(admInput,snapshot,control,ct,outputFactory:()=>new SilentDevice(),rawDspCapture:(a,n)=>capture.AddRange(a.Take(n*2)));
            Check(capture.SequenceEqual(FloatFile(admRendered.PcmPath)),"P4: mixed bed/object real-time PCM equals offline output");measured.Add(new{mode="ADM Objects + bed",stats});
        }
        string broken=Path.Combine(root,"unsupported-adm.wav");var bbytes=File.ReadAllBytes(demo);string xmlText=Encoding.UTF8.GetString(bbytes);
        // Fixed-size attribute mutation leaves the RIFF chunk sizes valid.
        int pos=xmlText.IndexOf("typeDefinition=\"Objects\"",StringComparison.Ordinal);
        Check(pos>=0,"P4: test fixture contains real object definitions");
        var marker=Encoding.UTF8.GetBytes("typeDefinition=\"Objects\"");int offset=Find(bbytes,marker,Find(bbytes,marker)+marker.Length);Encoding.ASCII.GetBytes("typeDefinition=\"HOA    \"").CopyTo(bbytes,offset);File.WriteAllBytes(broken,bbytes);
        bool unsupported=false;try{AdmWave.Open(broken);}catch(NotSupportedException){unsupported=true;} Check(unsupported,"P4: unsupported object type fails instead of dropping a source");
        string bad=Path.Combine(root,"truncated.wav");File.WriteAllBytes(bad,File.ReadAllBytes(demo)[..100]);bool invalid=false;try{AdmWave.Open(bad);}catch(InvalidDataException){invalid=true;}Check(invalid,"P4: truncated chunk bounds are rejected");
        var peakAudio=rendered with { Peak=1.5 };bool clipped=false;try{await SpatialRendering.ExportAsync(peakAudio,Path.Combine(root,"unsafe.flac"),paths.Ffmpeg,true,ct);}catch(InvalidDataException){clipped=true;}
        Check(clipped && !File.Exists(Path.Combine(root,"unsafe.flac")),"P2: above-unity FLAC is rejected without an incomplete success file");
        SpatialJson.Save(Path.Combine(root,"results.json"),new{passed=checks.Count,checks,measured,Library=library.Entries,Comparison=new{ma,mb,ab.GainADb,ab.GainBDb}});
        Console.WriteLine($"{checks.Count} spatial checks passed.");
    }
    static int Find(byte[] source,byte[] needle,int start=0){for(int i=start;i<=source.Length-needle.Length;i++)if(source.AsSpan(i,needle.Length).SequenceEqual(needle))return i;throw new InvalidDataException("Marker missing");}
    static float[] FloatFile(string path){var bytes=File.ReadAllBytes(path);return MemoryMarshal.Cast<byte,float>(bytes).ToArray();}
    public sealed class SilentDevice : IHeadphoneOutput
    {
        readonly WasapiHeadphoneOutput device=new();readonly float[] zero=new float[512];
        public long SubmittedFrames=>device.SubmittedFrames;public long PlayedFrames=>device.PlayedFrames;public double BufferMilliseconds=>device.BufferMilliseconds;public double StreamLatencyMilliseconds=>device.StreamLatencyMilliseconds;
        public bool HasStarted=>device.HasStarted;public long EmptyBufferCount=>device.EmptyBufferCount;public bool SupportsPause=>true;
        public void Pause()=>device.Pause();public void Resume()=>device.Resume();public void WaitForSpace(int n,CancellationToken ct)=>device.WaitForSpace(n,ct);
        public void Write(float[] pcm,int frames)=>device.Write(zero,frames);public void Drain(CancellationToken ct)=>device.Drain(ct);public void Dispose()=>device.Dispose();
    }
}
