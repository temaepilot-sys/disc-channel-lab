using Disc2Flac;
using PureHDF;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

internal static class BrirTests
{
    public static async Task Run(string output)
    {
        string root = Path.GetFullPath(output); Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("DISCCHANNEL_DATA_HOME", Path.Combine(root, "profile"));
        var checks = new List<string>(); var measurements = new List<object>();
        void Check(bool ok, string name) { if (!ok) throw new InvalidDataException(name); checks.Add(name); Console.WriteLine("PASS " + name); }
        void Reject(Action a, string name) { bool rejected = false; try { a(); } catch (Exception ex) when (ex is InvalidDataException or NotSupportedException) { rejected = true; } Check(rejected, name); }
        var random = new Random(2891);
        foreach (int length in new[] { 1, 255, 256, 257, 1023, 1024, 1025, 4097, 16383, 16384, 16385, 48003, 288000 })
        {
            float[] l = new float[length], r = new float[length];
            foreach (int index in new[] { 0, 3, 255, 1023, 1024, 16383, 16384, length - 1 }.Where(i => i < length).Distinct()) { l[index] = (float)(random.NextDouble() * .4 - .2); r[index] = (float)(random.NextDouble() * .4 - .2); }
            var conv = new BrirConvolution(l,r,7); float[] input = new float[256], a = new float[256], b = new float[256]; double error = 0;
            input[0] = 1;
            for (int start = 0; start < length + 8192; start += 256)
            {
                conv.Process(input,a,b); Array.Clear(input);
                for (int j=0;j<256;j++) { int k=start+j; error=Math.Max(error,Math.Abs(a[j]-(k<length?l[k]:0))); error=Math.Max(error,Math.Abs(b[j]-(k<length?r[k]:0))); }
            }
            Check(error < 1e-6, $"FFT: impulse / ear mapping / partition boundary / tail, N={length}, max error={error:E2}");
        }
        {
            float[] l = Enumerable.Range(0,18003).Select(_=>(float)(random.NextDouble()-.5)*.004f).ToArray(), r = l.Reverse().ToArray();
            float[] source = Enumerable.Range(0,777).Select(_=>(float)(random.NextDouble()-.5)).ToArray();
            var expectedL = new double[l.Length+source.Length]; var expectedR = new double[expectedL.Length];
            for(int i=0;i<source.Length;i++)for(int j=0;j<l.Length;j++){expectedL[i+j]+=(double)source[i]*l[j];expectedR[i+j]+=(double)source[i]*r[j];}
            var conv = new BrirConvolution(l,r); float[] input=new float[256], a=new float[256], b=new float[256]; double error=0;
            for(int start=0;start<expectedL.Length;start+=256)
            {
                Array.Clear(input); if(start<source.Length) Array.Copy(source,start,input,0,Math.Min(256,source.Length-start)); conv.Process(input,a,b);
                for(int j=0;j<256&&start+j<expectedL.Length;j++){error=Math.Max(error,Math.Abs(a[j]-expectedL[start+j]));error=Math.Max(error,Math.Abs(b[j]-expectedR[start+j]));}
            }
            Check(error<2e-6,"FFT: random signal matches independent double direct convolution across all three tiers");
            for(int i=0;i<64;i++)conv.Process(input,a,b);
            long allocated=GC.GetAllocatedBytesForCurrentThread();for(int i=0;i<256;i++)conv.Process(input,a,b);
            Check(GC.GetAllocatedBytesForCurrentThread()==allocated,"FFT: steady-state convolution allocates zero managed bytes");
        }
        string[] codes=["FL","FR","FC","LFE","SL","SR","BL","BR"];
        float[] irL=new float[96001],irR=new float[96001]; irL[37]=.6f;irR[44]=.3f;
        for(int i=1000;i<irL.Length;i++){irL[i]=(float)(random.NextDouble()-.5)*.002f*(float)Math.Exp(-i/16000d);irR[i]=(float)(random.NextDouble()-.5)*.002f*(float)Math.Exp(-i/16000d);}
        string ir=Path.Combine(root,"synthetic-test-response.wav");WriteWave(ir,irL,irR,48000);
        var profile=new BrirProfile("Synthetic verification fixture (not a measured room)",codes.Select(c=>new BrirRoute(c,ir)).ToArray(),"Generated for numerical tests","CC0 test fixture",LayoutConfirmed:true);
        string manifest=Path.Combine(root,"synthetic-test.brir.json");SpatialJson.Save(manifest,profile);
        var loaded=await BrirLibrary.PrepareAsync(manifest); Check(loaded.Hash.Length==64&&loaded.Responses.Count==8,"Profile: all eight explicitly mapped speaker responses and content hash");
        loaded.RequireChannels(codes); Check(true,"Profile: 7.1 route coverage accepted without inferred channel ordering");
        Reject(()=>loaded.RequireChannels(["BC"]),"Profile: missing channel is rejected rather than dropped");
        Reject(()=>loaded.RequireChannels(["FL"],new(48000,256,[new("FL","object",0,SpatialSourceKind.Object,0,256,[new(0,256,Vector3.UnitZ)])],"test",[])),"Profile: moving/ADM object routing rejected");
        SpatialJson.Save(manifest,profile with { LayoutConfirmed=false });Reject(()=>BrirLibrary.Prepare(manifest),"Profile: measurement orientation / ear confirmation required");
        SpatialJson.Save(manifest,profile with { Routes=[new("FL",ir),new("FL",ir)] });Reject(()=>BrirLibrary.Prepare(manifest),"Profile: duplicate channels rejected");
        SpatialJson.Save(manifest,profile); var options=new HeadphoneOptions {Enabled=true,Method=HeadphoneMethod.MeasuredBrir,BrirPath=manifest,BrirHash=loaded.Hash,HeadroomDb=-12,SmoothingMs=0};
        float[] interleaved = new float[48000*6*8];
        for(int i=0;i<interleaved.Length;i++){int c=i%8,f=i/8;interleaved[i]=.025f*(float)Math.Sin(2*Math.PI*(130+c*170)*f/48000);}
        string pcm=Path.Combine(root,"test-7.1.f32");File.WriteAllBytes(pcm,MemoryMarshal.AsBytes(interleaved.AsSpan()).ToArray());
        var scene=SpatialScene.ChannelBed(codes,interleaved.Length/8);var inputFile=new SpatialInput(pcm,8,scene,0,interleaved.Length/8,"Original synthetic 7.1 test tones","test");
        var mix=new PreviewMixState(StereoMixSettings.Default,null);var snapshot=new SpatialRenderSnapshot(options,new(0,0,0),mix,1,loaded.Hash);
        var rendered=await SpatialRendering.RenderAsync(inputFile,snapshot,true,CancellationToken.None);
        Check(rendered.TailFrames>=96000&&rendered.Peak>0&&rendered.ClippedSamples==0,"DSP: finite 7.1 stereo output with complete two-second measured-response tail");
        Check(rendered.Snapshot.Brir?.Assets.Length==8&&rendered.Snapshot.Brir.Profile.Source==profile.Source,"Export: reproducible profile, asset hashes, measurement/ear routes in render metadata");
        using(var captured=new MemoryStream())using(var transport=new PreviewPlaybackControl())
        {
            var stats=await SpatialPcmPlayer.PlayAsync(inputFile,snapshot,transport,CancellationToken.None,outputFactory:()=>new SpatialLabTests.SilentDevice(),rawDspCapture:(block,n)=>captured.Write(MemoryMarshal.AsBytes(block.AsSpan(0,n*2))));
            Check(captured.ToArray().AsSpan().SequenceEqual(File.ReadAllBytes(rendered.PcmPath)),"DSP: realtime pre-device float PCM is bit-exact with offline DSP including the tail");
            measurements.Add(new{kind="7.1 realtime preflight",stats.PreflightP95Milliseconds,stats.MaxBlockMilliseconds,rendered.RenderMilliseconds});
        }
        using(var a=new HeadphoneRenderer(codes,options))using(var b=new HeadphoneRenderer(codes,options with{Room=new(){Mode="Studio"},SofaPath="deliberately-missing.sofa",Hrtf=HeadphoneHrtf.Sofa}))
        {
            float[][] block=codes.Select(_=>Enumerable.Repeat(.01f,256).ToArray()).ToArray();float[] x=new float[512],y=new float[512];
            a.Render(block,x,new(0,0,0),options,mix,1); b.Render(block,y,new(90,30,15),options,mix,1);
            Check(x.AsSpan().SequenceEqual(y),"DSP: BRIR ignores head rotation, unused SOFA and added reflections; no double HRTF / room processing");
        }
        {
            float[][] block=[Enumerable.Repeat(.01f,256).ToArray(),Enumerable.Repeat(.01f,256).ToArray()];
            float[] result=new float[512];
            using(var engine=new HeadphoneRenderer(["FL","FR"],options))
            {
                var cancel=new PreviewMixState(StereoMixSettings.Default,null,[new("FL",1,0,false),new("FR",1,0,false,InvertPolarity:true)]);
                double error=0;for(int j=0;j<80;j++){engine.Render(block,result,new(0,0,0),options,cancel,1);error=Math.Max(error,result.Max(v=>Math.Abs(v)));}
                Check(error<2e-6,"Mixer: opposite polarity cancels identical measured routes across staggered FFT tiers");
            }
            using(var engine=new HeadphoneRenderer(["FL","FR"],options))
            {
                engine.Render(block,result,new(0,0,0),options,new(StereoMixSettings.Default,null,[new("FL",1,0,true),new("FR",1,0,true)]),1);
                Check(result.All(v=>v==0),"Mixer: Mute prevents BRIR excitation");
            }
            using(var solo=new HeadphoneRenderer(["FL","FR"],options))using(var isolated=new HeadphoneRenderer(["FL","FR"],options))
            {
                float[] single=new float[512];var state=new PreviewMixState(StereoMixSettings.Default,null,[new("FL",.4,0,false,Solo:true),new("FR",1,0,false)]);
                solo.Render(block,result,new(0,0,0),options,state,1);isolated.Render([block[0],new float[256]],single,new(0,0,0),options,state,1);
                Check(result.AsSpan().SequenceEqual(single),"Mixer: Solo and fader isolate the same BRIR source");
            }
        }
        var tools=new ToolPaths();string flac=Path.Combine(root,"brir-render.flac");
        if(File.Exists(flac))File.Delete(flac);if(File.Exists(flac+".render.json"))File.Delete(flac+".render.json");
        await SpatialRendering.ExportAsync(rendered,flac,tools.Ffmpeg,true,CancellationToken.None,writeRenderJson:true);
        string probe=await SpatialRendering.RunAsync(tools.Ffprobe,["-v","error","-show_entries","stream=sample_rate,channels,bits_per_raw_sample","-of","json",flac],CancellationToken.None);
        Check(probe.Contains("48000")&&probe.Contains("24")&&File.ReadAllText(flac+".render.json").Contains(loaded.Hash),"Export: 48 kHz / 24-bit stereo FLAC and BRIR settings sidecar");
        var ab=await SpatialRendering.CompareAsync(inputFile with{Frames=240000},snapshot with{Options=options with{Method=HeadphoneMethod.SteamAudio},HrtfHash=HrtfEntry.BuiltIn.Hash},snapshot,tools.Ffmpeg,true,CancellationToken.None,30);
        Check(ab.Matched&&Math.Abs(ab.MetricsA.IntegratedLufs!.Value+ab.GainADb-ab.MetricsB.IntegratedLufs!.Value-ab.GainBDb)<.00001,"A/B: same fixed source interval, scalar loudness match, no EQ or room blending");
        foreach(var bits in new[]{16,24,32})
        {
            string wav=Path.Combine(root,$"pcm-{bits}.wav");WritePcmWave(wav,bits);SpatialJson.Save(manifest,profile with{Routes=[new("FL",wav)]});var p=BrirLibrary.Prepare(manifest);
            Check(Math.Abs(p.Responses["FL"].Left[0]-.25f)<1e-6&&Math.Abs(p.Responses["FL"].Right[0]+.5f)<1e-6,$"WAV: PCM {bits}-bit / signed samples / ear order");
        }
        foreach(int rate in new[]{44100,96000})
        {
            var taps=new float[rate/10];taps[rate/20]=1;string wav=Path.Combine(root,$"ir-{rate}.wav");WriteWave(wav,taps,taps,rate);SpatialJson.Save(manifest,profile with{Routes=[new("FL",wav)]});var p=BrirLibrary.Prepare(manifest);
            var l=p.Responses["FL"].Left;double sum=l.Sum(v=>(double)v);int peak=Array.IndexOf(l,l.Max());Check(Math.Abs(sum-1)<.001&&Math.Abs(peak-2400-p.ResamplingLatencyFrames)<=1,$"IR resampling: {rate}→48000, propagation time and DC transfer gain preserved with documented 4 ms conversion latency");
            taps[rate/20]=0;taps[0]=1;WriteWave(wav,taps,taps,rate);p=BrirLibrary.Prepare(manifest);l=p.Responses["FL"].Left;
            Check(Math.Abs(l.Sum(v=>(double)v)-1)<.001&&Array.IndexOf(l,l.Max())==192,$"IR resampling: sample-zero impulse keeps both filter wings at {rate} Hz");
        }
        string sofa=Path.Combine(root,"test-general-fir.sofa");WriteSofa(sofa,0);SpatialJson.Save(manifest,profile with{Routes=[new("FL",sofa,1,1,0)]});var sp=BrirLibrary.Prepare(manifest);
        Check(sp.Responses["FL"].Left.Length==516&&sp.Responses["FL"].Left[4]==.75f&&sp.Responses["FL"].Right[3]==.5f,"SOFA: hyperslab selects explicit M and ears; nonzero per-ear Data.Delay preserved");
        WriteSofa(sofa,.25);Reject(()=>BrirLibrary.Prepare(manifest),"SOFA: unsupported fractional delay rejected, never rounded or stripped");
        WriteSofa(sofa,0,"FIR-E");Reject(()=>BrirLibrary.Prepare(manifest),"SOFA: unsupported emitter dimension rejected");
        WriteSofa(sofa,0,"FIR","SimpleFreeFieldHRIR");Reject(()=>BrirLibrary.Prepare(manifest),"SOFA: HRIR cannot be mislabeled as a measured room");
        SpatialJson.Save(manifest,profile with{Routes=profile.Routes.Where(r=>r.Channel!="LFE").ToArray(),CenteredDryLfe=true});var dry=BrirLibrary.Prepare(manifest);dry.RequireChannels(codes);
        using(var engine=new HeadphoneRenderer(["LFE"],options with{BrirHash=dry.Hash}))
        {
            float[] result=new float[512],source=new float[256];source[0]=.2f;engine.Render([source],result,new(0,0,0),options,mix,1);
            double expected=.2*.7*.70710678*Math.Pow(10,-12/20d);
            Check(Math.Abs(result[0]-expected)<1e-7&&result[0]==result[1],"LFE: explicit dry fallback is centered with existing LFE gain; no guessed room response");
        }
        SpatialJson.Save(manifest,profile);using(var append=new FileStream(ir,FileMode.Append)){append.WriteByte(0);}
        Reject(()=>BrirLibrary.Prepare(manifest,loaded.Hash),"Identity: modified BRIR source rejected before playback / export");
        WriteWave(ir,irL,irR,48000);SpatialJson.Save(manifest,profile);loaded=BrirLibrary.Prepare(manifest);
        // Real WASAPI output is deliberately silent; exercise scheduling without playing test tones to the user.
        using(var transport=new PreviewPlaybackControl())
        {
            var stats=await SpatialPcmPlayer.PlayAsync(inputFile,snapshot,transport,CancellationToken.None,outputFactory:()=>new SilentRealOutput());
            Check(stats.EmptyBuffers==0,"WASAPI: 7.1 / 2 s BRIR, real device delivery without empty buffers");
            measurements.Add(new{kind="Real WASAPI, silent verification",stats});
        }
        SpatialJson.Save(Path.Combine(root,"results.json"),new{passed=checks.Count,checks,measurements,limits="Synthetic fixtures only. IEM/WDR measurements and perceptual quality not verified."});
    }
    public static void WriteWave(string path,float[] l,float[] r,int rate)
    {
        int n=Math.Max(l.Length,r.Length);using var f=File.Create(path);using var w=new BinaryWriter(f);w.Write(Encoding.ASCII.GetBytes("RIFF"));w.Write(36+n*8);w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));w.Write(16);w.Write((ushort)3);w.Write((ushort)2);w.Write(rate);w.Write(rate*8);w.Write((ushort)8);w.Write((ushort)32);w.Write(Encoding.ASCII.GetBytes("data"));w.Write(n*8);for(int i=0;i<n;i++){w.Write(i<l.Length?l[i]:0);w.Write(i<r.Length?r[i]:0);}
    }
    static void WritePcmWave(string path,int bits)
    {
        using var f=File.Create(path);using var w=new BinaryWriter(f);int bytes=bits/8;w.Write(Encoding.ASCII.GetBytes("RIFF"));w.Write(36+bytes*2);w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));w.Write(16);w.Write((ushort)1);w.Write((ushort)2);w.Write(48000);w.Write(48000*bytes*2);w.Write((ushort)(bytes*2));w.Write((ushort)bits);w.Write(Encoding.ASCII.GetBytes("data"));w.Write(bytes*2);foreach(double v in new[]{.25,-.5}){long sample=(long)(v*Math.Pow(2,bits-1));for(int i=0;i<bytes;i++)w.Write((byte)(sample>>(8*i)));}
    }
    static void WriteSofa(string path,double extra,string type="FIR",string convention="GeneralFIR")
    {
        if(File.Exists(path))File.Delete(path);var data=new double[2,2,512];data[1,0,0]=.5;data[1,1,0]=.75;
        new H5File { ["Data.IR"]=data,["Data.SamplingRate"]=new H5Dataset(new[]{48000d}) { Attributes=new(){["Units"]="hertz"} },["Data.Delay"]=new double[,]{{0,0},{3+extra,4}},Attributes=new(){["SOFAConventions"]=convention,["DataType"]=type} }.Write(path);
    }
    sealed class SilentRealOutput : IHeadphoneOutput
    {
        readonly WasapiHeadphoneOutput output=new();readonly float[] silence=new float[512];
        public long SubmittedFrames=>output.SubmittedFrames;public long PlayedFrames=>output.PlayedFrames;public double BufferMilliseconds=>output.BufferMilliseconds;public double StreamLatencyMilliseconds=>output.StreamLatencyMilliseconds;public bool HasStarted=>output.HasStarted;public long EmptyBufferCount=>output.EmptyBufferCount;public bool SupportsPause=>output.SupportsPause;
        public void Pause()=>output.Pause();public void Resume()=>output.Resume();public void WaitForSpace(int n,CancellationToken t)=>output.WaitForSpace(n,t);public void Write(float[] data,int n)=>output.Write(silence,n);public void Drain(CancellationToken t)=>output.Drain(t);public void Dispose()=>output.Dispose();
    }
}
